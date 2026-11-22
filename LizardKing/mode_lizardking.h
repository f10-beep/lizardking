// mode_lizardking.h -- the LizardKing presentation (MODE_LIZARDKING).
//
// One fixed layout on every host: a composite device presenting an Xbox 360
// (XInput) pad plus a standard HID keyboard and mouse. Both pads touched =
// the keyboard; anything else = the controller, whose pads are the mouse. A
// held button keeps the layer it was pressed in until released.
// Design notes: docs/DESIGN.md.
#pragma once
#include "mode_xinput.h"
#include <stdint.h>

class LizardKingController : public XboxController {
    public:
	void begin() override;
	void usbIdentity();
	void onReport45(int slot, const uint8_t *rep, bool fresh,
			uint8_t bodyTlen) override;
	void task() override;

    protected:
	void emit(uint8_t slot, uint32_t generation,
		  const uint8_t *rep) override;
};
extern LizardKingController g_lizardKingCtl;

// serial console '~r': print one ball-state line per report, for tuning
extern bool g_lkTrace;
// serial console '~w': "W ms touching radius turn speed pressure" per report
// while the wheel scrolls or coasts (speed: while turned, else the coast)
extern bool g_lkScrollTrace;
// serial console '~y': "Y us gx gy gz ax ay az" per fresh report, raw
extern bool g_lkGyroTrace;
// serial console '~m': print the keyboard layer's KEYMAP as "KM pad zone
// k0..k7" lines (HID usage codes, ring order from N), then "KM end"
void lkDumpKeymap();
// serial console '~g': the Guide's practice-mode heartbeat; while it keeps
// coming the host gets no controller input (see mode_lizardking.cpp)
void lkGuideBeat();
// serial console '~h': the Guide left practice mode; hand input back now
void lkGuideRelease();
// serial console '~K pad zone k0..k7': replace a key map page until reboot
// (hex, as "KM" prints it)
void lkSetKeymapPage(const char *args);
// serial console '~T [name [value]]': list the feel constants, or show or
// set one until reboot (names as in mode_lizardking.cpp)
void lkTune(const char *args);
