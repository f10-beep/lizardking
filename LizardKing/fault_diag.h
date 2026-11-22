// fault_diag.h -- "why did we reboot?" diagnostic for issue #72 (intermittent disconnects).
//
// Field reports conflate THREE different events that all look identical from the host (USB re-enumerates, the
// controller drops):
//   1. a genuine RF link drop -- NO reboot, the link self-recovers in seconds (handled in rf_link, not here);
//   2. a loop() hang -- the ~8s hardware watchdog fires and resets the MCU (red LED);
//   3. an MCU fault -- a HardFault triggers an immediate reset (the Adafruit core's default handler resets).
// Without recording which one happened, (2) and (3) are indistinguishable in the field, so the thread keeps
// guessing. This module classifies the cause of each boot and reports it (the boot banner over CDC).
//
// How: the nRF52 RESETREAS register already distinguishes watchdog (DOG) / software (SREQ) / pin (RESETPIN) /
// lockup resets; the Adafruit core latches it at boot (readResetReason()). A SREQ alone can't tell "we rebooted
// on purpose" (mode-switch chord, config reboot, DFU) from "a HardFault reset us" -- so we stamp the GPREGRET2
// retention register (retained across soft/watchdog/pin reset, cleared only on power-on; GPREGRET itself is
// reserved by the bootloader for the DFU magic): faultDiagArmIntentionalReset() before every deliberate
// NVIC_SystemReset, and the HardFault handler stamps a distinct fault marker.
#pragma once
#include <stdint.h>

// Boot-cause classification (kept in sync with REASON_STR[] in fault_diag.cpp).
enum {
	RR_UNKNOWN = 0,
	RR_POWERON, // cold power-on / brownout
	RR_PIN, // reset pin / cable replug
	RR_WATCHDOG, // WDT fired -> loop() stopped feeding it (a hang)
	RR_LOCKUP, // CPU lockup
	RR_HARDFAULT, // HardFault -> our handler reset us
	RR_REBOOT, // intentional NVIC_SystemReset (mode switch / config / DFU)
	RR_SOFT, // software reset, unattributed
	RR_WAKE, // wake from System OFF
	RR_COUNT,
};

// Classify + log the cause of THIS boot. Call once, early in setup() (after Serial.begin so the line lands).
void faultDiagBoot();
// Stamp "the reset about to happen is intentional" -- call immediately before a deliberate NVIC_SystemReset so
// the next boot classifies it as RR_REBOOT, not RR_HARDFAULT.
void faultDiagArmIntentionalReset();

// ---- hang-stage breadcrumb -------------------------------------------------------------------------------
// loop() writes the stage it is ENTERING to GPREGRET2 (encoded 0x80|stage, distinct from the intentional/fault
// markers and from 0 during normal SREQ). If loop() wedges, the ~8s watchdog fires; GPREGRET2 survives that
// reset, so the next boot can report WHICH loop stage was stuck -- the missing piece for the clone hangs.
// Stages, in loop() order: 0 ctrl.task, 1 serial, 2 rflink, 3 haptic, 4 led, 5 usbtx.
void faultDiagSetStage(uint8_t stage);
const char *faultDiagHangStageStr();

// Live hang localization (does NOT need a reset to survive): faultDiagBeat() is called once per loop()
// iteration; faultDiagStallMs() is ms since that last beat (~0 healthy, grows while loop() is wedged). The
// flight recorder snapshots it with the current stage.
void faultDiagBeat();
uint32_t faultDiagStallMs();
const char *faultDiagStageStr(uint8_t s);

// Watchdog pre-reset PC capture ("software SWD"): arm in setup() right after NRF_WDT->TASKS_START. After a
// watchdog hang, faultDiagHangPC()/LR() return the PC/LR of the stuck code (0 if the hang hard-masked
// interrupts so the capture ISR couldn't run). Map the PC with addr2line on the build .elf.
void faultDiagArmHangCapture();
// Flash black box: 4 Hz TIMER4 watchdog-of-the-watchdog; at ~6s of loop stall it dumps both tasks' stacked
// PCs + vitals to a reserved flash page (survives ANY reset, incl. boards that wipe .noinit/GPREGRET2).
// Reported + consumed by faultDiagBoot on the next boot. Arm once from setup(), after the WDT starts.
void faultDiagBlackBoxArm();

// Per-task stack headroom (words of stack never used = free). Call faultDiagStackTick() from loop() (self-gated
// to ~1 Hz). faultDiagUsbdStackFree() trending toward 0 under haptic load confirms the usbd-task overflow.
void faultDiagStackTick();
uint16_t faultDiagUsbdStackFree();
uint16_t faultDiagLoopStackFree();

// ---- flight recorder (catch a watchdog hang post-mortem) -------------------------------------------------
// A .noinit ring of recent high-level events + a live vitals snapshot, BOTH surviving a watchdog reset (same
// mechanism as the hang PC frame: .noinit RAM isn't zeroed by the C runtime, so it rides through the reset even
// on clone boards whose bootloader wipes GPREGRET2). A hang drops USB the instant it resets, so live CDC
// logging can never catch the moment itself -- instead we record the trail continuously and DUMP IT ON THE NEXT
// BOOT when the last reset was a hang. Every push is a few stores under PRIMASK: safe to call from the fragile
// 800B usbd task and from ISRs (NO printf, NO allocation). This is the "what was going on when it wedged" the
// cheap boards (Teyleten hangs ~every 4 min) need. `arg` is event-specific, usually packed (high<<8)|low.
enum {
	FR_NONE = 0,
	// periodic loop heartbeat (arg = stall ms) -- the gap between beats
	// localizes the wedge in time
	FR_BEAT,
	FR_SET, // usbd handleSet feature write     (arg = (rid<<8)|cmd)
	FR_GET, // usbd handleGet feature read      (arg = (rid<<8)|cmd)
	FR_RELAY, // host->controller relay enqueued (arg = (slot<<8)|rid)
	FR_RFUP, // RF (re)connect                   (arg = slot)
	FR_RFDN, // RF link down                     (arg = slot)
	FR_HEAL, // RF-stall radio self-heal (power-cycle)
	FR_MOUNT, // USB HID mount/unmount changed   (arg = mounted-slot count)
	FR_SUSP, // USB bus suspend edge
	FR_RESUME, // USB bus resume edge
	FR_OFF, // controller power-off (0x9F) relayed (arg = slot)
	FR_RINGF, // relay-ring fault caught          (arg = running count)
	FR_SAVE, // bond flash write (saveBonds)
};

// Push one event into the flight recorder. Cheap + ISR/usbd-safe (a few stores under PRIMASK, no printf).
void faultDiagTrace(uint8_t evt, uint16_t arg);
// Per-loop upkeep: refresh the persistent vitals snapshot + emit a heartbeat (~4x/s). Call once per loop()
// iteration.
void faultDiagFlightTick(void);
// Print the pre-reset flight trail + vitals-at-wedge over CDC (from the boot-time copy of the surviving ring).
// Called automatically from faultDiagBoot() after a hang; also re-dumpable on demand from the console ("FR").
void faultDiagDumpFlight(void);
