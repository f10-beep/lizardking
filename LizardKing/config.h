// config.h -- the USB presentation for this boot, the RF poll rate, and the
// settings that persist in cfg.bin (set from the serial console).
//
// One puck speaks the same RF protocol whatever USB face it wears; only the
// USB enumeration and the input-report mapping change. The face is g_usbMode,
// chosen at boot by whether the puck holds a bond.
#pragma once
#include <stdint.h>

// USB presentations (g_usbMode), with upstream's numbers. LizardKing is the
// product; Steam mode exists for pairing, which Steam performs through the
// puck's own HID interface, so an unpaired puck boots into it.
// MODE_LIZARD is Steam mode's keyboard/mouse fallback while Steam is closed.
#define MODE_STEAM 0
#define MODE_LIZARD 3
#define MODE_LIZARDKING 13

static inline bool modeIsPuck(uint8_t m)
{
	return m == MODE_STEAM || m == MODE_LIZARD;
}

// from the bonds, at boot
extern uint8_t g_usbMode;
// cached !modeIsPuck(g_usbMode)
extern bool g_xbox;

// RF poll cadence: 250 Hz (4000 us), the real Valve puck's rate. The
// controller does not stream on its own: the puck polls and the controller
// answers each poll with one input frame carrying fresh IMU data, so the
// delivered report rate equals the poll rate.
#define POLL_US_DEFAULT 4000u
// How long a USB suspend must persist before the controllers are powered off.
// A brief selective suspend (host idle power management) resumes in under a
// second and must not cause a self-inflicted power-off; real sleep persists.
#define SUSPEND_OFF_MS 4000u
// RF poll period, us. The console's PR<hz> changes it for the session only,
// for sweeps; every boot starts at POLL_US_DEFAULT.
extern uint32_t g_pollUs;

// A few bytes of cfg.bin for other modules (the radio's last good channel).
#define CFG_EXT_STORAGE_BYTES 10u
uint8_t cfgExtRead(uint8_t index);
void cfgExtWrite(uint8_t index, uint8_t value);

void loadCfg();
// marks the settings changed; cfgTask() writes them when it can't hurt
void saveCfg();
// call from loop()
void cfgTask();
// First boot after a UF2 install: wipe the file system (bonds and settings).
// Call after InternalFS.begin(), before anything is loaded.
void wipeOnFreshInstall();
