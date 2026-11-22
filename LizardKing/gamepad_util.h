// gamepad_util.h -- shared helpers for building host USB gamepad reports from g_in.
//
// These are reused across the DualSense / DS4 / Switch personalities: analog-stick rescaling, Steam-trackpad
// -> absolute-touch mapping, and the PlayStation-layout button/hat/face/shoulder packers. Per-target button
// code maps (codeToXB / codeToSwitch / codeToJc) live with their own controllers; only the cross-mode pieces
// are here.
#pragma once
#include <stdint.h>
#include "triton.h"

// Shared Steam-controller -> Sony sensor coordinate transform.
// Gyro and accelerometer use the same signed permutation for coherent fusion.
struct PsImuFrame {
	int16_t gx, gy, gz;
	int16_t ax, ay, az;
};

// Steam trackpad s16 coords -> absolute touch surface. TOUCH_PAD_W is split into left/right halves so both
// pads can co-exist as two contacts on a single DualSense/DS4 touchpad.
#define TOUCH_PAD_W 1920u
uint16_t padNormU16(int16_t v, uint16_t maxv);
void touchPackPoint(uint8_t *base, int finger, bool touch, uint16_t x,
		    uint16_t y);
