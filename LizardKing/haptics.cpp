#include "haptics.h"
#include "bonds.h"
#include "config.h"
#include "rf_link.h"
// usbTxBoost/Unboost -- flood-rate CDC prints share the dcd DMA claim window
#include "usb_tx.h"
// puckNotePowerOff() -- hold a powered-off slot disconnected to Steam
#include "puck_hid.h"
#include "steam_commands.h"

// USBDevice.suspended() -> autonomous controller power-off on host sleep
#include <Adafruit_TinyUSB.h>
#include <Arduino.h>
#include <string.h>

uint8_t g_relayOp = 0xE3; // E3 poll
// Power the controllers off when host sleep persists (see haptics.h for the wake trade-off). Persisted;
// console "SO" toggles.
uint8_t g_suspendOff = 1;
// Host-rumble shaping (persisted in cfg.bin; console "RS<pct>" / "RY<n>").
uint16_t g_rumbleScale = RUMBLE_SCALE_PCT;
uint8_t g_rumbleStyle = RUMBLE_STYLE_NORMAL;

// Controller power-off. CONFIRMED from a real Windows USB capture of the Valve puck: Steam's "turn off
// controller" is the single feature-0x01 command 0x9F with payload ASCII "off!" (6F 66 66 21). The dongle
// forwards host feature reports verbatim, so the controller acts on report 0x9F directly -- we relay it the
// same way (E3 SET sub-TLV). The wire relay is NO-ACK, so send a small burst: a single lost frame must not
// leave the controller on.
void hapticSendShutdown(uint8_t slot)
{
	static const uint8_t OFF[4] = { 0x6f, 0x66, 0x66, 0x21 }; // "off!"
	// slot-targeted: Steam's 0x9F arrives on ONE controller's interface and must power off only that
	// controller (broadcasting it killed every connected controller when the user turned off one). The
	// broadcast default (0xFF) remains for the trigger that logically means "all off": host suspend.
	for (uint8_t i = 0; i < HAPTIC_SHUTDOWN_SHOTS; i++)
		relayEnqueue(IBEX_CMD_TURN_OFF_CONTROLLER, OFF, sizeof OFF,
			     false, slot);
	// Tell the puck presentation layer to show this slot (or all, for 0xFF broadcast) cleanly DISCONNECTED to
	// Steam and hold it there through the controller's post-off F1 tail -- otherwise the dying replies bounce
	// the connection state (phantom reconnect / never-removed). Covers every power-off path (Steam 0x9F, the
	// Guide + Back chord, host suspend) since they all funnel through here.
	puckNotePowerOff(slot);
}

// millis of last translated host rumble (0x80), per-slot (4 XInput interfaces each have their own stream)
static unsigned long g_rumble80Ms[NSLOT] = { 0 };

// Steam/Triton rumble is latched on until an explicit zero report; tracked per-slot so each controller's
// stuck-rumble watchdog is independent
static bool g_rumble80On[NSLOT] = { false, false, false, false };

// A rumble STOP is relayed as this many copies over successive poll cycles (see hapticSteamRumble): the relay
// is NO-ACK, and a lost final stop leaves the controller latched rumbling. 3 gives temporal diversity a
// single-frame RF loss can't wipe out without meaningfully changing steady-state traffic.
#define RUMBLE_STOP_REPS 3

// ---- relay rings: one per bond slot. Multi-producer (USB ISR + loop-context console/xinput), one consumer
// per slot (rfConnFlushRelay on that slot's poll turn). Producers serialize under PRIMASK.
struct RelayMsg {
	uint8_t rid, len;
	bool isHaptic;
	uint8_t data[RELAY_MAXP];
	// set by the caller at relayEnqueue() time (see haptics.h) -- true iff a real controller reply is
	// wanted for this specific message, i.e. whether rfConnFlushRelay appends the query-reply trailer.
	bool expectReply;
};
// deep enough to hold a full Steam settings/LED transaction burst without loss
#define RELAY_QLEN 32
static RelayMsg g_rq[NSLOT][RELAY_QLEN];
static volatile uint8_t g_rqHead[NSLOT];
static volatile uint8_t
	g_rqTail[NSLOT]; // head=next write, tail=next read; empty when equal
static inline uint8_t rqNext(uint8_t i)
{
	return (uint8_t)((i + 1) % RELAY_QLEN);
}
// Counts times a ring-drain loop hit its iteration cap = head/tail were desynced or corrupted (e.g. an
// out-of-range g_rqHead from a stray write/stack overflow). These loops run with IRQs DISABLED, so an
// unterminated one spins forever with interrupts off -> millis()/USB/SOF all freeze and only the hardware
// watchdog recovers (an invisible "watchdog (hang)" -- the live stall monitor can't see it because the SOF
// IRQ is dead). The cap turns that into a logged, recovered event instead of a hang (flight recorder).
volatile uint16_t g_ringFault = 0;

bool relayEnqueue(uint8_t rid, const uint8_t *payload, uint8_t plen,
		  bool isHaptic, uint8_t slot, bool expectReply)
{
	if (plen > RELAY_MAXP)
		plen = RELAY_MAXP;
	if (slot != 0xFF && slot >= NSLOT)
		return false;
	uint32_t pm = __get_PRIMASK();
	__disable_irq();
	// slot=0xFF: broadcast -- enqueue into every BONDED slot's ring. Unbonded slots are skipped: only used
	// slots are ever flushed (rfConnStep/doPoll), so anything queued for an empty slot sits in its ring
	// indefinitely and is then delivered to whatever controller later pairs INTO that slot -- e.g. a host
	// suspend with one controller bonded left "off!" (0x9F) frames in the empty rings, and the next
	// controller paired there powered itself off on its first flush. puckNotePowerOff() already gates its
	// broadcast on used; this is the same rule for the wire side.
	// Full queue: evict the oldest entry, never the newest. Steam bursts end with the commit/stop, so
	// dropping the oldest keeps the most-recent (meaningful) frame.
	uint8_t s0 = (slot == 0xFF) ? 0 : slot;
	uint8_t s1 = (slot == 0xFF) ? NSLOT : slot + 1;
	for (uint8_t s = s0; s < s1; s++) {
		if (slot == 0xFF && !g_slot[s].used)
			continue;
		uint8_t h = g_rqHead[s], nx = rqNext(h);
		if (nx == g_rqTail[s])
			g_rqTail[s] = rqNext(g_rqTail[s]);
		g_rq[s][h].rid = rid;
		g_rq[s][h].len = plen;
		g_rq[s][h].expectReply = expectReply;
		g_rq[s][h].isHaptic = isHaptic;
		if (plen)
			memcpy(g_rq[s][h].data, payload, plen);
		g_rqHead[s] = nx;
	}

	__set_PRIMASK(pm);
	return true;
}
void relayClearSlot(uint8_t slot)
{
	if (slot >= NSLOT)
		return;
	uint32_t pm = __get_PRIMASK();
	__disable_irq();
	g_rqHead[slot] = g_rqTail[slot] = 0;
	__set_PRIMASK(pm);
}

// Per-slot helpers. slot==-1 (default) checks the CURRENT poll slot (g_curSlot), used by flush-time code
// paths that don't have a slot in hand. Callers with a real slot pass it in.
bool hapticLinkUp(int slot)
{
	int s = (slot >= 0) ? slot : g_curSlot;
	if (s < 0 || s >= NSLOT)
		return false;
	return g_slot[s].used && (millis() - g_connReplyMs[s]) < 300;
}
bool haptic82Blocked(int slot)
{
	int s = (slot >= 0) ? slot : g_curSlot;
	if (s < 0 || s >= NSLOT)
		return true;
	return !hapticLinkUp(s);
}
// "Is haptics from this USB interface's slot allowed through?" -- the slot must be currently connected.
bool hapticRelaySlotOk(int slot)
{
	return slot >= 0 && slot < NSLOT && hapticLinkUp(slot);
}
static void hapticCancelPendingOn(int slot)
{
	// void queued ON entries (stale haptics / rumble across a reconnect). Per-slot: only the reconnected
	// slot's queue is scrubbed -- another controller's pending haptics are its own business. slot=-1 keeps
	// the old scrub-everything behavior (boot).
	int c0 = (slot >= 0 && slot < NSLOT) ? slot : 0;
	int c1 = (slot >= 0 && slot < NSLOT) ? slot + 1 : NSLOT;
	uint32_t pm = __get_PRIMASK();
	__disable_irq();
	for (int s = c0; s < c1; s++) {
		uint8_t guard = RELAY_QLEN + 1;
		for (uint8_t i = g_rqTail[s]; i != g_rqHead[s]; i = rqNext(i)) {
			// desynced/corrupt ring -> recover, don't spin IRQs-off
			if (!guard--) {
				g_rqHead[s] = g_rqTail[s] = 0;
				g_ringFault++;
				break;
			}
			RelayMsg &m = g_rq[s][i];
			if (m.rid == 0x82) {
				bool on = false;
				for (uint8_t j = 2; j < m.len; j++)
					if (m.data[j]) {
						on = true;
						break;
					}
				if (on)
					m.rid = 0;
			}
			if (m.rid == 0x80) {
				bool on = false;
				for (uint8_t j = 0; j < m.len; j++)
					if (m.data[j]) {
						on = true;
						break;
					}
				if (on)
					m.rid = 0;
			}
		}
	}
	__set_PRIMASK(pm);
}
// Integer square root (binary restoring). Used by RUMBLE_STYLE_SOFT; avoids pulling float math into the USB
// OUT callback that calls hapticSteamRumble().
static uint32_t isqrt32(uint32_t v)
{
	uint32_t r = 0, b = 1UL << 30;
	while (b > v)
		b >>= 2;
	while (b) {
		if (v >= r + b) {
			v -= r + b;
			r = (r >> 1) + b;
		} else {
			r >>= 1;
		}
		b >>= 2;
	}
	return r;
}

bool hapticSteamRumble(uint16_t lowFreq, uint16_t highFreq, uint8_t slot)
{
	if (slot >= NSLOT)
		return false;
	// Shape the decoded host amplitudes: style first (which motor plays, and the response curve), then the
	// strength scale. Integer-only -- this runs in the USB OUT callback (ISR context), so no float math.
	{
		uint32_t l = lowFreq, h = highFreq, t;
		switch (g_rumbleStyle) {
		case RUMBLE_STYLE_MONO:
			l = h = (l > h) ? l : h;
			break;
		case RUMBLE_STYLE_HEAVY:
			h = 0;
			break;
		case RUMBLE_STYLE_LIGHT:
			l = 0;
			break;
		case RUMBLE_STYLE_SWAP:
			t = l;
			l = h;
			h = t;
			break;
		case RUMBLE_STYLE_PUNCHY:
			// x^2/FS: 0xFFFF*0xFFFF fits uint32 exactly, so no intermediate overflow
			l = l * l / 0xFFFF;
			h = h * h / 0xFFFF;
			break;
		case RUMBLE_STYLE_SOFT:
			l = isqrt32(l * 0xFFFF);
			h = isqrt32(h * 0xFFFF);
			break;
		default: // RUMBLE_STYLE_NORMAL
			break;
		}
		l = l * g_rumbleScale / 100;
		h = h * g_rumbleScale / 100;
		lowFreq = (l > 0xFFFF) ? 0xFFFF : (uint16_t)l;
		highFreq = (h > 0xFFFF) ? 0xFFFF : (uint16_t)h;
	}
	bool on = lowFreq || highFreq;
	// Per-slot settle gate (the per-slot reconnect block + link-up check). 0x82 haptics in Steam mode use the
	// same gate; for XInput, the host only sends a stream while a controller is connected, so this also doubles
	// as "no controller here, no relay".
	if (on && haptic82Blocked(slot))
		return false;
	if (!on && !hapticLinkUp(slot))
		return false;

	// SDL's current Steam/Triton structs define output report 0x80 as:
	//   type, uint16 intensity, {uint16 speed, int8 gain} left/right.
	// We map conventional gamepad low/high-frequency motors to left/right speeds and use max as intensity.
	uint16_t intensity = lowFreq > highFreq ? lowFreq : highFreq;
	uint8_t p[9];

	// haptic_type_t::HAPTIC_TYPE_RUMBLE; 0 is the off/zero report
	p[0] = on ? 0x04 : 0x00;
	p[1] = (uint8_t)(intensity & 0xFF);
	p[2] = (uint8_t)(intensity >> 8);
	p[3] = (uint8_t)(lowFreq & 0xFF);
	p[4] = (uint8_t)(lowFreq >> 8);
	p[5] = 0;
	p[6] = (uint8_t)(highFreq & 0xFF);
	p[7] = (uint8_t)(highFreq >> 8);
	p[8] = 0;
	// The RF relay is NO-ACK -- any single frame can be lost. For an ON that's self-healing: a continuous
	// stream of rumble commands follows during play, so a dropped frame is corrected microseconds later. A
	// STOP is the dangerous one: if the host's FINAL zero (game/stream quit) -- or the watchdog's stop below
	// -- is the last frame on the wire and it's lost, the controller stays latched rumbling with nothing left
	// to correct it (the reported "constant rumble that didn't stop after closing GFN", cleared only by a
	// replug). And once we optimistically mark g_rumble80On=false, the watchdog can't rescue it either. So
	// relay a STOP as a short BURST: rfConnFlushRelay drains one ring entry per poll cycle, so N copies go out
	// on successive cycles (~4ms apart) -- temporal diversity that a single-frame RF loss can't wipe out.
	// Bursting only the on->off transition leaves steady-state (repeated-ON / repeated-OFF) traffic unchanged.
	bool stopping = !on && g_rumble80On[slot];
	uint8_t reps = stopping ? RUMBLE_STOP_REPS : 1;
	bool queued = false;
	for (uint8_t i = 0; i < reps; i++)
		if (relayEnqueue(0x80, p, sizeof p, true, slot))
			queued = true;
	if (!queued)
		return false;
	g_rumble80Ms[slot] = millis();
	g_rumble80On[slot] = on;
	return true;
}
// Queue a pending test-haptic / stop relay (runs inside the poll cadence -- never at raw loop rate). Test
// haptics broadcast to all connected slots (slot 0xFF); the stop frame is broadcast too (a stuck latch can
// affect any controller, and the haptic-engine clear-re-init is settings-only so it's harmless on healthy
// ones).

// rfConnFlushRelay(ch, s1): drain one entry from the current slot's relay queue and TX it. Each slot's queue
// is independent, so each controller only sees its own commands. With N connected slots the per-slot relay
// rate is 1/N of the per-cycle rate; sustained buzz streams are still 1 packet/cycle/slot.
bool rfConnFlushRelay(uint8_t ch, uint8_t s1)
{
	int cur = (g_curSlot >= 0 && g_curSlot < NSLOT) ? g_curSlot : 0;
	// Snapshot one entry under a short critical section. relayEnqueue() (the producer) runs on the
	// high-priority usbd task and, when the ring is full, evicts the oldest by advancing g_rqTail itself --
	// the same variable this consumer reads/advances. Reading the entry while a producer could overwrite it
	// is a torn read (and dual-writing g_rqTail desyncs head/tail), so copy the entry out and consume the
	// slot atomically here, then do the (slow) RF TX on the copy with interrupts enabled.
	RelayMsg msg;
	bool have = false;
	uint32_t pm = __get_PRIMASK();
	__disable_irq();
	uint8_t guard = RELAY_QLEN + 1;
	while (g_rqTail[cur] != g_rqHead[cur]) {
		// desynced/corrupt ring -> recover, don't spin IRQs-off
		// (watchdog-hang class)
		if (!guard--) {
			g_rqHead[cur] = g_rqTail[cur] = 0;
			g_ringFault++;
			break;
		}
		RelayMsg &m = g_rq[cur][g_rqTail[cur]];
		g_rqTail[cur] = rqNext(g_rqTail[cur]); // consume the slot
		// rid 0 = entry voided by hapticCancelPendingOn -> skip
		if (m.rid) {
			// copy out before the producer can reuse the slot
			msg = m;
			have = true;
			break;
		}
	}
	__set_PRIMASK(pm);
	if (have) {
		RelayMsg &m = msg;
		{
			uint8_t rl = m.len;
			if (rl > RELAY_MAXP)
				rl = RELAY_MAXP;
			// On-air sub-TLV framing. CONFIRMED from real puck<->controller sniffs: a command LANDS on
			// the controller only with the type-01 + inner-len form E3 [2+rl][01][rid][innerlen][data];
			// the legacy form E3 [1+rl][05][rid][data] makes the controller DISCARD any 0x87+ command.

			// Same shape as the `[len][tag][value]` TLV grammar the F1 REPLY side
			// already uses (tags 0x02/0x04/0x06, docs/PROTOCOL.md sec 7.3): read as len=1, tag=3,
			// value=0. If the caller decides they want an answer, we add the queryTrailer.
			// Should check later if there's a way to somehow determine which types need the trailer and which ones don't.
			bool queryTrailer = m.expectReply;
			uint8_t p[5 + RELAY_MAXP + 3], plen;
			if (!m.isHaptic) {
				// Non-haptics use type 01
				p[0] = g_relayOp;
				p[1] = (uint8_t)(2 + rl);
				p[2] = 0x01;
				p[3] = m.rid;
				p[4] = rl;
				memcpy(p + 5, m.data, rl);
				plen = (uint8_t)(5 + rl);
				if (queryTrailer && plen + 3 <= sizeof p) {
					p[plen + 0] = 0x01;
					p[plen + 1] = 0x03;
					p[plen + 2] = 0x00;
					plen = (uint8_t)(plen + 3);
				}
			} else {
				// Haptics use type 05
				p[0] = g_relayOp;
				p[1] = (uint8_t)(1 + rl);
				p[2] = 0x05;
				p[3] = m.rid;
				memcpy(p + 4, m.data, rl);
				plen = (uint8_t)(4 + rl);
			}
			// Lifecycle log (CDC debug boot): EXACTLY what we TX to the controller, so a capture shows
			// whether OpenPuck (or Steam in the background) pokes the controller right before a buzz
			// latches -- the piece the I45 stream (controller->us) can't show. Low rate outside a haptic
			// burst; guarded so it never blocks the loop. cur = target slot.
			if (Serial.availableForWrite() > 60) {
				// boosted: this print runs at relay-flood rate and CDC flush enters the same
				// TinyUSB DMA claim window as HID sends (the issue-72 livelock; see usb_tx.cpp)
				usbTxBoost();
				Serial.printf("# TX t=%lu slot%d %s rid=%02X:",
					      (unsigned long)millis(), cur,
					      m.isHaptic ? "L05" : "L01",
					      m.rid);
				for (uint8_t i = 0; i < rl && i < 8; i++)
					Serial.printf(" %02X", m.data[i]);
				Serial.println();
				usbTxUnboost();
			}
			// slot was already consumed under the critical section above.
			// s1 carries a PID distinct from the GET poll (caller cycles it) so the controller's ESB
			// dedup never treats the GET as a retransmit of this relay.
			//
			// HARVEST the relay's reply as INPUT. Like any frame we send, the controller auto-ACKs a
			// relay with its current input in the ACK payload (~90us later, per the RE poll->ACK
			// capture). The old 80us window closed BEFORE that reply arrived, so every relay's input was
			// discarded -- and yet HW A/B showed the delivered input rate RISING when haptics relay
			// (an extra frame/cycle during a trackpad drag), because each frame makes the controller
			// refresh its ACK payload and the following E3 poll caught the fresher value. Reading the
			// reply directly turns each relay into a SECOND input sample per cycle: rfConnTx runs the
			// full F1 decode (seq-dedup guards double-forward), so a drag streaming haptics now collects
			// ~2x the samples, closing the gap to the real puck. A present reply returns early (~90us);
			// only a genuine no-reply pays the bounded 400us window, so airtime stays in budget.
			rfConnTx(ch, s1, p, plen,
				 // one relay per poll cycle -- reply harvested
				 // as input
				 400);
		}
	}

	// true = a relay frame went out this cycle (its reply is harvested as
	// input, above)
	return have;
}

void hapticInit()
{
	for (int s = 0; s < NSLOT; s++) {
		g_rqHead[s] = g_rqTail[s] = 0;
		g_rumble80On[s] = false;
		g_rumble80Ms[s] = 0;
	}
}
// LizardKing reads the controller's RF IMU samples for gyro aiming.
static const uint32_t IMU_KEEP_MS = 5000;
static bool modeWantsImu(uint8_t m)
{
	return m == MODE_LIZARDKING;
}
// Steam can leave the controller's persisted IMU mode off, and it survives across pucks. Every emulated
// gyro mode needs the raw RF samples that setting suppresses, so write raw accel + gyro for this slot.
static void imuForceRaw(int slot)
{
	static const uint8_t RAW_IMU[] = {
		SETTING_IMU_MODE,
		SETTING_GYRO_MODE_SEND_RAW_ACCEL |
			SETTING_GYRO_MODE_SEND_RAW_GYRO,
		0x00
	};
	if (!modeWantsImu(g_usbMode))
		return;
	relayEnqueue(IBEX_CMD_SET_SETTINGS_VALUES, RAW_IMU, sizeof RAW_IMU,
		     false, (uint8_t)slot);
}
// Schedule the clearing re-init for ONE slot. Called on the reliable first-reply signal from rf_link and
// again from hapticTask's link-up edge detector. Strictly per-slot: only the reconnected controller gets
// re-inits / its pending haptics scrubbed -- the other controllers hear nothing about it.
void hapticOnReconnect(int slot)
{
	if (slot < 0 || slot >= NSLOT)
		return;
	g_rumble80On[slot] = false;
	g_rumble80Ms[slot] = 0;
	// Scrub haptics queued before the link came up (stale across the reconnect) -- this slot only.
	hapticCancelPendingOn(slot);
	imuForceRaw(slot);
	// No haptic re-init (0x81 CLEAR DIGITAL MAPPINGS) at connect: 0x81 is non-idempotent (ibex
	// FUN_0001f554) and every call re-runs a peripheral re-arm, so repeats click audibly; the real puck
	// clears once and holds.
}
// Console rumble test (TR): buzz every connected controller at RUMBLE_TEST_AMP through the normal
// hapticSteamRumble() path (so g_rumbleStyle / g_rumbleScale apply), and
// schedule the stop. Deadline 0 = idle; millis()+MS can be 0 only once every 49 days, so bias it off zero.
static unsigned long g_rumbleTestStop = 0;
void hapticTestRumble()
{
	for (uint8_t s = 0; s < NSLOT; s++)
		if (hapticLinkUp((int)s))
			hapticSteamRumble(RUMBLE_TEST_AMP, RUMBLE_TEST_AMP, s);
	g_rumbleTestStop = millis() + RUMBLE_TEST_MS;
	if (!g_rumbleTestStop)
		g_rumbleTestStop = 1;
}

void hapticTask()
{
	// stop the test buzz -- signed compare so the millis() rollover cannot strand a latched actuator
	if (g_rumbleTestStop && (long)(millis() - g_rumbleTestStop) >= 0) {
		g_rumbleTestStop = 0;
		for (uint8_t s = 0; s < NSLOT; s++)
			hapticSteamRumble(0, 0, s);
	}

	// id9 steering (SET_SETTINGS index 9 = digital-mappings / the controller's AUTONOMOUS mapping+haptic
	// engine, which is what generates the trackpad tick haptics). We decide per mode whether that autonomous
	// engine should be ON, then either land id9=1 ONCE per connect episode (engine on) or hold id9=0 every
	// LIZKEEP_MS (engine off). id9 gates the whole autonomous pad layer INCLUDING the trackpad ticks; 0x87 is
	// change-guarded in the controller so a repeated same-value write is silent (no 0x81-style click), and
	// re-landing id9=1 on (re)connect restores the pad layer immediately instead of waiting for the
	// controller's revert timer (which also resets all settings = audible pop).
	//
	// wantAuto = should the controller run its own pad layer (trackpad ticks) for the ACTIVE mode?
	//  - puck modes (STEAM/LIZARD): NOT STEERED AT ALL. Steam owns the controller's haptics in puck mode
	//    (it writes id9 itself), so the whole block below is skipped -- we used to drive id9 from
	//    puckLizardActive() here, which fought Steam's own writes.
	//  - LizardKing: holds id9=0 (its own pad ticks replace the controller's).
	static unsigned long lastKeep[NSLOT] = { 0 };
	static bool landedAuto[NSLOT] = { false };
	static const uint8_t DATA_LIZARD_OFF[3] = { SETTING_LIZARD_MODE, 0x00,
						    0x00 };
	static const uint8_t DATA_LIZARD_ON[3] = { SETTING_LIZARD_MODE, 0x01,
						   0x00 };

	// In puck mode Steam owns haptics; skip id9 steering. Pure
	// lizard is the exception: Steam never sees the controller, so
	// nothing else would re-enable the pad layer after a session
	// left id9=0, and the trackpad ticks stay silent.
	const bool pureLizard = (g_usbMode == MODE_LIZARD);
	if (!modeIsPuck(g_usbMode) || pureLizard) {
		// LizardKing draws its own pad ticks at a gain it
		// controls (mode_lizardking.cpp); the controller's are as
		// strong as a click and blur the two together.
		bool wantAuto = pureLizard;
		for (int s = 0; s < NSLOT; s++) {
			if (!g_slot[s].used || !hapticLinkUp(s)) {
				// re-land id9 on the next (re)connect: a fresh controller defaults to
				// autonomous, but one carrying our previous session's id9 does not
				landedAuto[s] = false;
				lastKeep[s] = 0;
				continue;
			}
			if (wantAuto) {
				if (!landedAuto[s]) {
					landedAuto[s] = true;
					relayEnqueue(
						IBEX_CMD_SET_SETTINGS_VALUES,
						DATA_LIZARD_ON,
						sizeof DATA_LIZARD_ON, false,
						(uint8_t)s);
				}
			} else {
				landedAuto[s] = false;
				if (lastKeep[s] &&
				    (uint32_t)(millis() - lastKeep[s]) <
					    LIZKEEP_MS)
					continue;
				lastKeep[s] = millis();
				relayEnqueue(IBEX_CMD_SET_SETTINGS_VALUES,
					     DATA_LIZARD_OFF,
					     sizeof DATA_LIZARD_OFF, false,
					     (uint8_t)s);
			}
		}
	} // !modeIsPuck
	// Re-assert the raw IMU mode on a mode switch (the link stays up, so hapticOnReconnect never fires)
	// and periodically, in case Steam or the controller turned it back off while we were connected.
	{
		static uint8_t lastMode = 0xFF;
		static unsigned long lastImu[NSLOT] = { 0 };
		bool modeChanged = (g_usbMode != lastMode);
		lastMode = g_usbMode;
		for (int s = 0; s < NSLOT; s++) {
			if (!g_slot[s].used || !hapticLinkUp(s))
				continue;
			if (!modeChanged &&
			    (uint32_t)(millis() - lastImu[s]) < IMU_KEEP_MS)
				continue;
			lastImu[s] = millis();
			imuForceRaw(s);
		}
	}
	// Per-slot link-edge detect (backup for hapticOnReconnect in rf_link).
	static bool wasHapticLinkUp[NSLOT] = { 0 };
	for (int s = 0; s < NSLOT; s++) {
		if (!g_slot[s].used)
			continue;
		bool up = hapticLinkUp(s);
		if (up && !wasHapticLinkUp[s]) {
			hapticOnReconnect(s);
		}
		if (!up && wasHapticLinkUp[s]) {
			// Lifecycle log (CDC debug boot): the 300ms link-up watchdog saw this slot go silent. Pairs
			// with the CONNECT/RECONNECT lines from rf_link so a session of cycles shows the full
			// down->up cadence -- how often the link actually drops (churn) vs stays up.
			if (Serial.availableForWrite() > 50)
				Serial.printf("# LC t=%lu slot%d link DOWN\n",
					      (unsigned long)millis(), s);
		}
		wasHapticLinkUp[s] = up;
	}
	// (No automatic 0x81 re-init -- see hapticOnReconnect: repeats are the click train the real puck never
	// makes.)
	// Power-off on host sleep: only when VBUS is present (genuine sleep, not a cable unplug which also
	// trips the suspend edge briefly) AND the suspend has PERSISTED >= SUSPEND_OFF_MS. A brief USB
	// selective-suspend (host idle power-management) resumes in <1s; firing the power-off on its edge
	// powered the controllers off ourselves -> random drop/reconnect churn. Arm only on a genuine
	// resume->suspend edge (wasSusp=true at boot suppresses a false fire on boot-into-suspended).
	static bool wasSusp = true;
	static unsigned long suspSinceMs = 0;
	static bool suspArmed = false;
	bool susp = USBDevice.suspended();
	bool vbus = (NRF_POWER->USBREGSTATUS &
		     POWER_USBREGSTATUS_VBUSDETECT_Msk) != 0;
	if (susp && !wasSusp) {
		suspSinceMs = millis();
		suspArmed = true;
	}
	if (!susp)
		suspArmed = false;
	// g_suspendOff: opt out of the power-off entirely -- keeps the controller awake through host sleep so its
	// short-Steam-press remote wakeup stays available (see haptics.h). Disarm either way so re-enabling it
	// mid-suspend can't fire late.
	if (suspArmed && vbus && (millis() - suspSinceMs) >= SUSPEND_OFF_MS) {
		if (g_suspendOff)
			hapticSendShutdown();
		suspArmed = false; // fire once per suspend
	}
	wasSusp = susp;
	// Per-slot stuck-rumble watchdog: force zero after 2.5s without a refresh.
	for (int s = 0; s < NSLOT; s++) {
		if (g_rumble80On[s] && millis() - g_rumble80Ms[s] > 2500u)
			hapticSteamRumble(0, 0, (uint8_t)s);
	}
	// (No automatic idle-clear re-init either, for the same reason. Verbatim relay -- like the real
	// puck -- is the steady-state behavior.)
}
