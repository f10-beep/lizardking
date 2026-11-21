// Static wired Xbox 360 personality; one selected RF bond supplies input.
#pragma once
#include "controllers.h"
#include <stdint.h>

enum {
	XB_DUP = 0x0001,
	XB_DDOWN = 0x0002,
	XB_DLEFT = 0x0004,
	XB_DRIGHT = 0x0008,
	XB_START = 0x0010,
	XB_BACK = 0x0020,
	XB_L3 = 0x0040,
	XB_R3 = 0x0080,
	XB_LB = 0x0100,
	XB_RB = 0x0200,
	XB_GUIDE = 0x0400,
	XB_A = 0x1000,
	XB_B = 0x2000,
	XB_X = 0x4000,
	XB_Y = 0x8000
};

class XboxController : public IController {
    public:
	void onReport45(int slot, const uint8_t *rep, bool fresh,
			uint8_t bodyTlen) override;
	void task() override;

    protected:
	// Build and queue the host report for the selected bond. `generation`
	// must be passed through to xinputSubmit unchanged.
	virtual void emit(uint8_t slot, uint32_t generation,
			  const uint8_t *rep) = 0;
	// Reset XInput state and register the IN/OUT endpoint drain.
	void beginShared();
};

// Queue an XInput report for `slot`; dropped if that bond is no longer the
// selected one or the USB session changed since `generation` was read.
void xinputSubmit(uint8_t slot, uint32_t generation, uint16_t buttons,
		  uint8_t lt, uint8_t rt, int16_t lx, int16_t ly, int16_t rx,
		  int16_t ry);
