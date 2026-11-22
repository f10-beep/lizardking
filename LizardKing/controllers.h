// controllers.h -- the USB presentation for this boot.
//
// Two controllers exist: LizardKing (mode_eliz) and the Steam puck
// (puck_hid), kept for pairing. Exactly one is active per boot, selected by
// g_usbMode.
//   begin()      -- set the USB identity and register the interfaces.
//   onReport45() -- called by rf_link for each decoded input report 0x45;
//                   `slot` is the bond slot it arrived on (-1 = pre-bond).
//   task()       -- called every loop().
//   isPuck()     -- true for the Steam puck.
#pragma once
#include <stdint.h>

class IController {
    public:
	virtual ~IController()
	{
	}
	virtual void begin() = 0;
	virtual void onReport45(int slot, const uint8_t *rep, bool fresh,
				uint8_t bodyTlen)
	{
		(void)slot;
		(void)rep;
		(void)fresh;
		(void)bodyTlen;
	}
	// Other controller->host input reports decoded from the F1 reply (NOT 0x45): power/battery status report
	// 0x43, status event 0x44. The real puck forwards these to Steam verbatim -- that's how Steam reads battery.
	// rid = report id, data/n = body after the id. Default no-op (clean modes don't expose these reports).
	virtual void onAuxReport(int slot, uint8_t rid, const uint8_t *data,
				 uint8_t n)
	{
		(void)slot;
		(void)rid;
		(void)data;
		(void)n;
	}
	virtual void task()
	{
	}

	// a wake gesture fired while suspended; queue any post-resume input the host needs to actually wake
	virtual void wakeEvent()
	{
	}
	virtual bool isPuck() const
	{
		return false;
	}
};

// The singleton for a given mode (nullptr if the mode is unknown). Defined in controllers.cpp.
IController *controllerFor(uint8_t mode);

// The active controller for this boot (set in setup()). rf_link/loop dispatch through it.
extern IController *g_active;

