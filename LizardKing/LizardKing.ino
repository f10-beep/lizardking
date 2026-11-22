// LizardKing firmware entry point: setup() presents USB for this boot's mode
// and arms the watchdog; loop() pumps each subsystem. See CODE_MAP.md.
#include <Adafruit_TinyUSB.h>
#include <Adafruit_LittleFS.h>
#include <InternalFileSystem.h>
using namespace Adafruit_LittleFS_Namespace;

#include "config.h"
#include "board_config.h"
#include "build_info.h"
#include "identity.h"
#include "bonds.h"
#include "radio.h"
#include "controllers.h"
#include "haptics.h"
#include "rf_link.h"
#include "puck_hid.h"
#include "rf_diag.h"
#include "usb_app_drivers.h"
#include "lizard_map.h"
#include "serial_console.h"
#include "wake_hid.h"
#include "status_led.h"
#include "pwr_switch.h"
#include "fault_diag.h"
#include "usb_tx.h"
#include <stdio.h>

#if CFG_TUD_HID < 4
#error "CFG_TUD_HID must be >= 4 (the puck's four slot HIDs); build with make build"
#endif

// TinyUSB's event queue defaults to 16. Reports are sent from the loop task;
// a send whose DMA is busy defers onto that queue with a blocking post, so a
// comms burst (haptic floods, the connect-time re-init storm) fills it, the
// loop stops feeding the watchdog, and the puck resets. The value can only be
// set on the build command, so enforce it here.
#if !defined(CFG_TUD_TASK_QUEUE_SZ) || CFG_TUD_TASK_QUEUE_SZ < 32
#error "CFG_TUD_TASK_QUEUE_SZ must be >= 32 or the loop deadlocks under load; build with make build"
#endif

// the puck composite (WebUSB + wake mouse + four slot HIDs) exceeds the
// core's default 256 B configuration buffer
static uint8_t g_usbCfgDesc[512];

static void usbPresent()
{
	USBDevice.detach();
	delay(30);
	USBDevice.clearConfiguration();
	USBDevice.setConfigurationBuffer(g_usbCfgDesc, sizeof g_usbCfgDesc);
	if (g_active->isPuck()) {
		// SDL's puck driver binds slot HIDs only on interfaces 2..5, so
		// WebUSB (0) and the wake mouse (1) go first, as on the real puck.
		usb_web.begin();
		wakeHidBegin();
	} else {
		// Hosts cache USB identity by VID:PID:serial; the bond count in
		// the serial keeps the cached descriptor honest.
		snprintf(g_usbSerial, sizeof g_usbSerial, "%sZ%d", g_unit,
			 bondedSlotCount());
		USBDevice.setSerialDescriptor(g_usbSerial);
	}
	g_active->begin();
	// bus-powered (0x80) with remote wakeup (0x20)
	USBDevice.setConfigurationAttribute(0x80 | 0x20);
	USBDevice.attach();
}

// If loop() ever stops feeding it (a wedged radio wait, a fault, a blocked
// write), the watchdog resets the puck after ~8 s instead of needing a replug.
static void watchdogStart()
{
	NRF_WDT->CONFIG = (WDT_CONFIG_HALT_Pause << WDT_CONFIG_HALT_Pos) |
			  (WDT_CONFIG_SLEEP_Run << WDT_CONFIG_SLEEP_Pos);
	NRF_WDT->CRV = 8UL * 32768UL - 1;
	NRF_WDT->RREN = WDT_RREN_RR0_Msk;
	NRF_WDT->TASKS_START = 1;
	faultDiagArmHangCapture();
	faultDiagBlackBoxArm();
}

void setup()
{
	genSerial();
	ledInit();
#if OPK_PWR_SWITCH
	pwrSwitchInit();
#endif
	// seed defaults so unbonded slots don't share the discovery address
	for (int s = 0; s < NSLOT; s++)
		rfGenSessionAddr(s);
	InternalFS.begin();
	wipeOnFreshInstall();
	loadCfg();
	rfApplyStartupLastGoodChannel();
	loadBonds();
	// Steam mode's keyboard/mouse fallback while Steam is closed
	defaultLizardMap();
	// session addresses derive from each bond's UUID, stable across reboots
	for (int s = 0; s < NSLOT; s++)
		if (g_slot[s].used)
			rfGenSessionAddr(s);
	// unpaired (a fresh install) boots into Steam mode, ready to pair
	g_usbMode = bondedSlotCount() ? MODE_LIZARDKING : MODE_STEAM;

	g_xbox = !modeIsPuck(g_usbMode);
	g_active = controllerFor(g_usbMode);
	usbPresent();
	Serial.begin(115200);
	for (int i = 0; i < 300 && !USBDevice.mounted(); i++)
		delay(10);
	if (USBDevice.suspended()) {
		USBDevice.remoteWakeup();
		ledWakePulse();
	}
	usbTxBegin();
	hapticInit();
	Serial.printf("# LizardKing up: unit=%s board=%s mode=%s\n", g_unit,
		      g_board, g_active->isPuck() ? "steam" : "lizardking");
	faultDiagBoot();
	clockDiagBoot();
	watchdogStart();
}

void loop()
{
	NRF_WDT->RR[0] = WDT_RR_RR_Reload;
	faultDiagBeat();
	faultDiagStackTick();
	faultDiagFlightTick();
	clockDiagTick();
	if (g_dirty) {
		g_dirty = false;
		// a bond write stalls interrupts for ms: bracket it in the
		// flight recorder
		faultDiagTrace(FR_SAVE, 0);
		saveBonds();
	}
	// Each stage leaves a breadcrumb, so after a watchdog reset the next
	// boot reports which stage wedged.
	faultDiagSetStage(1);
	g_active->task();
	faultDiagSetStage(2);
	serialConsolePoll();
	faultDiagSetStage(3);
	rfDiagTask();
	faultDiagSetStage(4);
	rfLinkTask();
	faultDiagSetStage(5);
	hapticTask();
	faultDiagSetStage(6);
	ledTask();
#if OPK_PWR_SWITCH
	pwrSwitchTask();
#endif
	faultDiagSetStage(8);
	// reports drain here, in the loop: sending off-loop jitters the RF poll
	usbTxPump();
	puckCmdLogDrain();
}
