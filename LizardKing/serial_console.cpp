#include "serial_console.h"
#include "radio.h"
#include "rf_link.h"
#include "haptics.h"
#include "steam_commands.h"
#include "bonds.h"
#include "config.h"
#include "mode_lizardking.h" // g_lkTrace
#include <Adafruit_TinyUSB.h>
#include <Arduino.h>
#include <stdlib.h>
#include <string.h>

// '~' commands for tuning haptics, broadcast to every connected controller.
// Payloads follow the Triton OUTPUT reports in docs/PROTOCOL.md:
//   ~p side on_us off_us repeat         0x81 HAPTIC_PULSE
//   ~c side command gain_db             0x82 HAPTIC_COMMAND
//   ~t side gain_db freq dur lfo depth  0x83 HAPTIC_LFO_TONE
//   ~s side script gain_db              0x85 HAPTIC_SCRIPT
//   ~k setting value                    SET_SETTINGS_VALUES (u16)
//   ~r                                  toggle the LizardKing ball trace
//   ~m                                  dump the LizardKing keymap
//   ~K pad zone k0..k7                  set a LizardKing keymap page (hex)
//   ~T [name [value]]                   list / set LizardKing feel constants
static void tildeCommand(const char *a)
{
	char kind = *a++;
	if (kind == 'K') {
		lkSetKeymapPage(a);
		return;
	}
	if (kind == 'T') {
		lkTune(a);
		return;
	}
	long v[6] = {};
	char *e;
	for (int i = 0; i < 6; i++) {
		v[i] = strtol(a, &e, 0);
		if (e == a)
			break;
		a = e;
	}
	uint8_t p[9] = {};
	uint8_t rid, n = 0;
	auto u8 = [&](long x) { p[n++] = (uint8_t)x; };
	auto u16 = [&](long x) {
		u8(x);
		u8(x >> 8);
	};
	switch (kind) {
	case 'r':
		g_lkTrace = !g_lkTrace;
		Serial.printf("# ball trace %s\n", g_lkTrace ? "ON" : "off");
		return;
	case 'm':
		lkDumpKeymap();
		return;
	case 'w':
		g_lkScrollTrace = !g_lkScrollTrace;
		Serial.printf("# scroll trace %s\n",
			      g_lkScrollTrace ? "ON" : "off");
		return;
	case 'y':
		g_lkGyroTrace = !g_lkGyroTrace;
		Serial.printf("# gyro trace %s\n",
			      g_lkGyroTrace ? "ON" : "off");
		return;
	case 'g':
		lkGuideBeat();
		return;
	case 'h':
		lkGuideRelease();
		return;
	case 'b':
		// make flash: the UF2 bootloader's drive takes the install file
		Serial.println("# UF2 bootloader");
		Serial.flush();
		delay(40);
		enterUf2Dfu();
		return;
	case 'p':
		rid = 0x81;
		u8(v[0]);
		u16(v[1]);
		u16(v[2]);
		u16(v[3]);
		break;
	case 'c':
	case 's':
		rid = kind == 'c' ? 0x82 : 0x85;
		u8(v[0]);
		u8(v[1]);
		u8(v[2]);
		break;
	case 't':
		rid = 0x83;
		u8(v[0]);
		u8(v[1]);
		u16(v[2]);
		u16(v[3]);
		u16(v[4]);
		u8(v[5]);
		break;
	case 'k':
		rid = IBEX_CMD_SET_SETTINGS_VALUES;
		u8(v[0]);
		u16(v[1]);
		break;
	default:
		Serial.println("# ~p/~c/~t/~s/~k/~r/~m/~K/~T/~w/~g/~h/~b");
		return;
	}
	bool ok = relayEnqueue(rid, p, n, kind != 'k', 0xFF);
	Serial.printf("# ~%c rid %02X len %u %s\n", kind, rid, n,
		      ok ? "queued" : "DROPPED");
}

// Plain commands, for tuning and diagnosis (the Guide's protocol and the
// haptic experiments are the '~' ones above):
//   SS        one-line link snapshot: poll rate, fresh reports, loss
//   PR<hz>    RF poll rate for this session, 100..1000
//   RS<pct>   host-rumble strength, persisted
//   RY<n>     host-rumble style (RUMBLE_STYLE_*), persisted
//   TR        a test buzz at the current style and strength
//   SO        power the controllers off on host sleep, persisted
//   UP        reprint the boot line, with why the puck last reset
void serialConsolePoll()
{
	// long enough for a key map page (~K)
	static char line[48];
	static uint8_t li = 0;
	while (Serial.available()) {
		char c = Serial.read();
		if (c != '\n' && c != '\r') {
			if (li < sizeof line - 1)
				line[li++] = c;
			continue;
		}
		line[li] = 0;
		li = 0;
		if (line[0] == '~') {
			tildeCommand(line + 1);
		} else if (!strcmp(line, "SS")) {
			// new/s is what the host receives; noRx is RF loss
			Serial.printf(
				"# SS pollrate=%luHz polls=%u/s F1=%u/s new=%u/s crc=%u noRx=%u slot%d\n",
				(unsigned long)(1000000UL / g_pollUs),
				g_pollsps, g_f1ps, g_newps, g_crcps, g_norxps,
				g_curSlot);
		} else if (!strncmp(line, "PR", 2) && line[2] >= '0' &&
			   line[2] <= '9') {
			long hz = constrain(atol(line + 2), 100L, 1000L);
			g_pollUs = (uint32_t)(1000000L / hz);
			Serial.printf("# poll rate -> %ld Hz (%lu us)\n", hz,
				      (unsigned long)g_pollUs);
		} else if (!strncmp(line, "RS", 2) && line[2] >= '0' &&
			   line[2] <= '9') {
			long pct = constrain(atol(line + 2),
					     (long)RUMBLE_SCALE_MIN,
					     (long)RUMBLE_SCALE_MAX);
			// stored as pct/2
			g_rumbleScale = (uint16_t)(pct & ~1L);
			saveCfg();
			Serial.printf("# rumble strength -> %u%%\n",
				      g_rumbleScale);
		} else if (!strncmp(line, "RY", 2) && line[2] >= '0' &&
			   line[2] <= '9') {
			g_rumbleStyle = (uint8_t)constrain(
				atol(line + 2), 0L, (long)RUMBLE_STYLE_MAX);
			saveCfg();
			Serial.printf("# rumble style -> %u\n", g_rumbleStyle);
		} else if (!strcmp(line, "TR")) {
			hapticTestRumble();
			Serial.printf("# test rumble (style %u, %u%%)\n",
				      g_rumbleStyle, g_rumbleScale);
		} else if (!strcmp(line, "SO")) {
			// off keeps the controller's Steam-press remote wake
			// usable through host sleep (see g_suspendOff)
			g_suspendOff = !g_suspendOff;
			saveCfg();
			Serial.printf(
				"# power off controllers on host sleep (>%ums) %s\n",
				(unsigned)SUSPEND_OFF_MS,
				g_suspendOff ? "ON" : "off");
		} else if (!strcmp(line, "UP")) {
			printBoot();
		}
	}
}
