// mode_eliz.h -- LizardKing "enhanced lizard" mode (MODE_ELIZ).
//
// One fixed layout on every host: a composite device presenting an Xbox 360
// (XInput) pad plus a standard HID keyboard and mouse. Trackpad touch picks
// the layer the buttons drive: no pad = gamepad, one pad = mouse, both pads =
// keyboard. A held button keeps the layer it was pressed in until released.
// Design notes: docs/ENHANCED_LIZARD.md.
#pragma once
#include "mode_xinput.h"
#include <stdint.h>

class EnhancedLizardController : public XboxController {
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
extern EnhancedLizardController g_elizCtl;

// serial console '~r': print one ball-state line per report, for tuning
extern bool g_elizTrace;
// serial console '~w': "W ms touching radius turn speed pressure" per report
// while the wheel scrolls or coasts (speed: while turned, else the coast)
extern bool g_elizScrollTrace;
// serial console '~m': print the keyboard layer's KEYMAP as "KM pad zone
// k0..k7" lines (HID usage codes, ring order from N), then "KM end"
void elizDumpKeymap();
// serial console '~g': the Guide's practice-mode heartbeat; while it keeps
// coming the host gets no controller input (see mode_eliz.cpp)
void elizGuideBeat();
// serial console '~h': the Guide left practice mode; hand input back now
void elizGuideRelease();
