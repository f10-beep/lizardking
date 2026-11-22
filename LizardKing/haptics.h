// haptics.h -- host -> controller relay queue (haptics + settings) + the watchdogs that stop a stuck buzz.
//
// Steam and translated host rumble send OUTPUT/feature reports the real dongle forwards to the controller as
// a SET sub-TLV inside the E3 poll. We do the same: handleSet()/the console enqueue with relayEnqueue(), and
// rfConnFlushRelay() emits ONE entry per poll cycle (rf_link.cpp), never at raw loop rate.
//
// One ring per bond slot: the consumer (rfConnFlushRelay) only drains the current slot's queue, so commands
// addressed to other slots are never consumed out of turn. ISR producers write under PRIMASK; the consumer
// runs in loop context and never races the same slot's head pointer.
//
// The controller's haptic LATCHES until told to stop. If the host's stop is lost over RF (or the link drops
// mid-buzz) the actuator whines forever -- so we send 0x82-zero stop bursts on reconnect, but ONLY when a
// haptic was actually active when the link dropped: every extra 0x82 frame is an audible click on the
// controller (captured), so an unconditional burst on each link-up edge is itself a buzz source on a flapping
// link.
#pragma once
#include <stdint.h>
#include "config.h"
#include "bonds.h" // NSLOT

// max relayed payload bytes per entry: RF frame = [E3][len][05][rid][payload] and MAXLEN=64 -> 60
#define RELAY_MAXP 60u
// Controller power-off: hapticSendShutdown() relays Steam's confirmed "turn off controller" command (feature-0x01
// cmd 0x9F, payload "off!" -- captured from the real puck). Sent as a small burst because the RF relay is NO-ACK.
#define HAPTIC_SHUTDOWN_SHOTS 3u
// Rumble strength: percent of the decoded host amplitude applied in LizardKing. 200 (double) is the default.
// Runtime-adjustable and persisted -- console "RS<pct>".
#define RUMBLE_SCALE_PCT 200u
#define RUMBLE_SCALE_MIN 10u
#define RUMBLE_SCALE_MAX 500u
// Rumble style: how the decoded low/high motor amplitudes are shaped before the 0x80 frame is built. Applied
// BEFORE the strength scale, on the raw host amplitudes. Persisted -- console "RY<n>".
#define RUMBLE_STYLE_NORMAL \
	0 // as captured: low -> left speed, high -> right speed
#define RUMBLE_STYLE_MONO 1 // both motors at max(low,high) -- heavier, fuller
#define RUMBLE_STYLE_HEAVY 2 // low-frequency motor only (mute the buzzy one)
#define RUMBLE_STYLE_LIGHT \
	3 // high-frequency motor only (crisp, no deep rumble)
#define RUMBLE_STYLE_SWAP 4 // swap the two motors
#define RUMBLE_STYLE_PUNCHY \
	5 // squared curve: weak effects softer, strong ones untouched
#define RUMBLE_STYLE_SOFT \
	6 // sqrt curve: lifts weak effects so subtle rumble is felt
#define RUMBLE_STYLE_MAX 6
extern uint16_t g_rumbleScale; // percent, RUMBLE_SCALE_MIN..RUMBLE_SCALE_MAX
extern uint8_t g_rumbleStyle; // RUMBLE_STYLE_*
// Test buzz for the console (TR): a fixed mid-scale amplitude pushed through the SAME shaping path host
// rumble takes, so what you feel is what a game at that amplitude would feel like. Auto-stops in hapticTask()
// -- the controller's haptic LATCHES, so the stop is not optional.
#define RUMBLE_TEST_AMP 0x8000u
#define RUMBLE_TEST_MS 500u
void hapticTestRumble();

// ---- relay queue (written by puck_hid.cpp, mode_*.cpp, serial_console.cpp; drained by rf_link.cpp) ----
// Enqueue one host->controller report. `slot` = bond slot (0..NSLOT-1) or 0xFF to broadcast to every
// connected controller (used by hapticSendShutdown / hapticReinit / test haptics). ISR-safe (PRIMASK).
// `expectReply`: true iff the CALLER is relaying this specifically because it wants the controller's real
// answer back (a query like GET_ATTRIBUTES/GET_STRING_ATTRIBUTE/READ_SETTING -- see puck_hid.cpp
// handleSet's `relayQuery`), as opposed to a fire-and-forget action (haptics, settings writes, power-off)
// that nothing will ever wait on a reply for. rfConnFlushRelay uses this.
bool relayEnqueue(uint8_t rid, const uint8_t *payload, uint8_t plen,
		  bool isHaptic, uint8_t slot = 0xFF, bool expectReply = false);
// Drop everything queued for one bond slot. Called when a slot becomes BONDED (Steam's 0xA2 pairing
// write): whatever was queued while the slot was empty was aimed at a controller that no
// longer -- or never did -- live there, and an unbonded slot's ring is never flushed, so it would otherwise
// be handed straight to the freshly paired controller (a stale "off!" powered it back off). ISR-safe.
void relayClearSlot(uint8_t slot);

// id9 steering (LizardKing and pure lizard): land the controller's SET_SETTINGS index 9 (digital-mappings /
// lizard-active) at 0 once per LIZKEEP_MS per connected slot to hold its autonomous mapping/haptic engine
// OFF (LizardKing), or at 1 once per connect episode to turn it on (pure lizard). id9
// gates the whole autonomous pad layer, including the trackpad ticks, and holding it off also stops the
// engine latching the deep-inside buzz seen after repeated reconnects (capture-for-haptics.txt: that buzz is
// controller-internal; OpenPuck relays no haptics in that state). PUCK modes (STEAM/LIZARD) are not steered:
// Steam writes id9 itself there, and driving it from the puck side fought those writes.
#define LIZKEEP_MS 2000u
// Autonomous controller power-off on host sleep (see hapticTask). 1 = power the controllers off once the
// USB suspend has persisted SUSPEND_OFF_MS (default -- what the real puck does, so the controllers don't sit
// awake draining while the host sleeps). 0 = leave them on.
//
// TRADE-OFF, deliberate: with this ON the controller is off while the host sleeps, so the short-Steam-press
// remote-wakeup gesture in rf_link (guarded on USBDevice.suspended()) can no longer be sent from it. Waking
// the host from the controller then goes through the OTHER path: pressing Steam on a powered-off controller
// turns it back on, the link comes up, and the reconnect-wake in rfConnStep issues the remote wakeup. Same
// physical gesture; the power-off fires once per suspend so the returning controller is not shut off again.
// Turn this OFF (console "SO") if a controller must stay awake through host sleep. Persisted.
extern uint8_t g_suspendOff;
extern uint8_t g_relayOp; // relay frame opcode (E3 poll)

// relay the controller power-off (0x9F "off!"), burst x3. Steam's per-interface 0x9F passes that slot so
// only that controller powers off; host suspend keeps the broadcast default (all off).
void hapticSendShutdown(uint8_t slot = 0xFF);

bool hapticLinkUp(int slot = -1);
bool haptic82Blocked(int slot = -1);
bool hapticRelaySlotOk(int slot);
// queue a Steam/Triton 0x80 rumble frame. `slot` = bond slot of the originating controller (0..NSLOT-1);
// defaults to 0 for the legacy single-controller callers. Per-slot so each connected controller can have its
// own active rumble stream when the host presents multiple gamepads (e.g. 4 XInput devices).
bool hapticSteamRumble(uint16_t lowFreq, uint16_t highFreq, uint8_t slot = 0);

// queue + flush the pending host/test/stop relay inside the poll cadence (called from rf_link).
// rfConnFlushRelay's s1 must carry a PID distinct from the GET poll that follows it. g_relayPid
// is initialised 2 ahead of g_pollPid and both increment once per cycle, so the 2-bit PIDs stay
// 2 apart (mod 4) forever and never collide — keeping the controller from deduplicating the GET.

// returns true if a relay frame was actually transmitted this call (queue had an entry), so the poll loop can
// count relay TXs separately from poll cycles.
bool rfConnFlushRelay(uint8_t ch, uint8_t s1);
// times a relay-ring drain hit its iteration cap (head/tail desync or corruption) -- non-zero means we caught
// and recovered from what would otherwise be an IRQ-off watchdog hang. Recorded by the flight recorder.
extern volatile uint16_t g_ringFault;

// boot reset: clear relay/active flags
void hapticInit();

// per-loop upkeep: link-edge markers + steam 0x82 quiet timeout + fires the scheduled re-init
void hapticTask();

// Called from rf_link the instant a controller (re)connect is detected (an F-reply after a gap): resets that
// slot's rumble state and scrubs haptics queued before the link came up. Independent of hapticTask's link
// heuristic; the other slots keep relaying.
void hapticOnReconnect(int slot);
