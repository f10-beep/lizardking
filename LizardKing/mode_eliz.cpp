#include "mode_eliz.h"
#include "eliz_drive.h"
#include "xinput_descriptors.h"
#include "triton.h"
#include "config.h"
#include "bonds.h"
#include "usb_tx.h"
#include "haptics.h"
#include "rf_link.h"
#include <Adafruit_TinyUSB.h>
#include <Arduino.h>
#include <math.h>
#include <string.h>

EnhancedLizardController g_elizCtl;
bool g_elizTrace;

// ---- tunables (see docs/ENHANCED_LIZARD.md) ----

// a layer change must hold this long before buttons follow it
#define ELIZ_SWITCH_MS 30u

// Pad clicks come from pressure: the controller's own click bit needs
// ~8900, which tires the thumb. ON/OFF give hysteresis like its own click.
#define ELIZ_CLICK_ON 3200
#define ELIZ_CLICK_OFF 2300
// With both thumbs on the pads (typing) clicks are lighter still: there is
// no cursor for a stray click to disturb, and typing wants a light touch.
#define ELIZ_KEY_CLICK_ON 2400
#define ELIZ_KEY_CLICK_OFF 1700

// pad counts per cursor pixel at the default sensitivity (g_mDiv 64)
#define ELIZ_PX_DIV 160

// While touching, the cursor chases the thumb's absolute (1:1) position,
// closing a fraction of the gap each report: all of it at thumb speeds up
// to FOLLOW_SLOW px/report, easing to FOLLOW_MIN at FOLLOW_FAST. Fast swipes
// are smoothed, yet the cursor lands where 1:1 puts it once the thumb slows,
// so it never drifts. The speed is smoothed by SPEED_ALPHA and starts from
// the ball's speed when the thumb lands on a moving ball, so a tap's jitter
// cannot fling it.
#define ELIZ_FOLLOW_SLOW 1.0f
#define ELIZ_FOLLOW_FAST 8.0f
#define ELIZ_FOLLOW_MIN 0.3f
#define ELIZ_SPEED_ALPHA 0.4f
// The pad refreshes the thumb position only every 2nd-3rd report and repeats
// it in between, so the thumb speed is measured per new position: its travel
// over the time since the previous new position, in px per nominal report
// (REPORT_US). Repeats are skipped; none for STILL_US means the thumb is
// still. Taken per report, the repeats read as stops.
#define ELIZ_REPORT_US 4000.0f
#define ELIZ_STILL_US 16000u

// On lift the ball keeps the cursor's velocity, smoothed by LAUNCH_ALPHA so
// the twist at the end of a swipe does not decide the glide, and the rest
// of the gap is dropped. Touching a moving ball stops it.
#define ELIZ_LAUNCH_ALPHA 0.35f

// Free-spinning ball: exponential friction plus a small constant drag, so
// slow spins end instead of crawling.
#define ELIZ_GLIDE_DECAY 0.9835f
#define ELIZ_GLIDE_DRAG 0.006f
#define ELIZ_GLIDE_MAX 40.0f
// A touch that is not driving the cursor (keyboard layer) brakes the ball
// like a resting finger instead of stopping it dead.
#define ELIZ_GHOST_DECAY 0.88f

// Pressure dead zone: the cursor ignores thumb travel within a radius that
// is DEADZONE_PX at zero pressure (a feather touch, about to lift off),
// shrinks to 0 by DZ_START, then grows back to DEADZONE_PX at DZ_FULL (below
// the click) and stays full for the whole click, so presses, releases and
// light lift-offs land where aimed. Beyond it the cursor is pulled along at
// the edge, so drags still work. A radius never makes the cursor creep while
// a click is held, which slowing the follow rate did.
#define ELIZ_DZ_START 400
#define ELIZ_DZ_FULL 2200
#define ELIZ_DEADZONE_PX 10.0f
// Lift dead zone: a thumb rolling off drops pressure fast and its contact
// point wobbles. Each pad count per report of pressure drop opens LIFT_GAIN
// px of dead zone (up to LIFT_MAX), shrinking by LIFT_DECAY per report, so
// a thumb that presses back down is followed 1:1 again. The launch velocity
// updates more slowly the wider it is (half as fast at LIFT_HOLD_PX), so a
// flick ending in a lift keeps its launch and a settled lift has none,
// while pressure wobble barely matters.
#define ELIZ_LIFT_GAIN 0.1f
#define ELIZ_LIFT_MAX 40.0f
#define ELIZ_LIFT_DECAY 0.8f
#define ELIZ_LIFT_HOLD_PX 2.0f

// Circular scrolling on the left pad: one turn is SCROLL_REV notches,
// clockwise scrolls down. Starting a scroll needs the thumb to turn past a
// dead zone that shrinks from a half turn at radius SCROLL_R_IN (fraction of
// the pad) to 0 at SCROLL_R_OUT: near the center a tiny move swings the
// angle wildly, so a click there never starts one, while the outer ring
// starts at once. Once started it follows the thumb anywhere but the very
// center (SCROLL_R_HOLD) until the thumb lifts.
#define ELIZ_SCROLL_REV 24.0f
#define ELIZ_SCROLL_R_IN 0.2f
#define ELIZ_SCROLL_R_OUT 0.65f
// Scroll momentum, like a free-spinning wheel, and built like the
// trackball's: while the thumb turns the wheel its speed is kept up to date
// (see wheelSpeed), and lifting the thumb at more than SCROLL_FLING notches
// per report lets the wheel keep turning, slowing by SCROLL_DECAY per report
// until under SCROLL_STOP. A thumb back on the left pad catches it; slow
// scrolling stops dead. A single reading more than SCROLL_OUTLIER times the
// current speed (a junk position at the pad's edge) is capped.
#define ELIZ_SCROLL_OUTLIER 3.0f
// Wheel reports at most every WHEEL_GAP_MS: fast scrolling goes out as fewer,
// larger steps instead of a report every 4 ms, which some apps stutter on.
// Slow scrolling looks the same (it never needs more often than this).
#define ELIZ_WHEEL_GAP_MS 16
// Once a scroll is under way the thumb may drift inward without stopping it;
// only this close to the center, where the angle is noise, does it pause.
#define ELIZ_SCROLL_R_HOLD 0.1f
#define ELIZ_SCROLL_FLING 0.12f
#define ELIZ_SCROLL_DECAY 0.996f
#define ELIZ_SCROLL_STOP 0.008f
// a scroll texture tick every SCROLL_TICK notches, fine to match the
// high-resolution wheel
#define ELIZ_SCROLL_TICK (2.0f / 3.0f)
// High-resolution wheel: once Windows sets the HID Resolution Multiplier,
// wheel reports count 1/120 notch.
#define ELIZ_WHEEL_HIRES 120
// a per-report pad delta this large is a glitch, not a swipe
#define ELIZ_MAX_DELTA 12000

// Clicks use Triton OUTPUT 0x81 HAPTIC_PULSE [side][on_us][off_us][repeat]
// for a sharp pulse; a 0x83 tone is dull and can swallow the next click.
// Pulse side 0 is the RIGHT pad.
static const uint8_t PULSE_SIDE[2] = { 0, 1 }; // PAD_R, PAD_L
#define ELIZ_CLICK_ON_US 1500
#define ELIZ_RELEASE_ON_US 700
// 0x81 has no gain, so key clicks are made stronger by doubling the on-time
#define ELIZ_KEY_ON_US 3000
#define ELIZ_KEY_RELEASE_ON_US 1400

// Ticks use Triton OUTPUT 0x83 HAPTIC_LFO_TONE
// [side][gain_db s8][freq u16][dur u16][lfo_freq u16][lfo_depth]: its gain
// sets strength, while 0x81 on-time only sets length. Tone side 0 is the
// LEFT pad, the reverse of 0x81. A 20-unit tick is clearly felt at -24 dB
// and barely at -36 dB.
static const uint8_t TONE_SIDE[2] = { 1, 0 }; // PAD_R, PAD_L
#define ELIZ_TONE_HZ 200
#define ELIZ_TICK_DB -24
#define ELIZ_TICK_DUR 20
// entering / leaving the keyboard layer: a tone on both pads, dull and
// round so it never reads as a click
#define ELIZ_KBD_ON_DB -12
#define ELIZ_KBD_OFF_DB -18
#define ELIZ_KBD_DUR 30
// a tick per TICK_PX of cursor travel, so ticks slow down with the ball;
// scrolling ticks once per wheel notch
#define ELIZ_TICK_PX 24.0f
#define ELIZ_TICK_MIN_MS 12u

// Keyboard layer: each pad is a 3x3 grid of equal thirds. A zone is left
// only once the thumb is ZONE_HYST past its edge, so a thumb resting on a
// border does not flicker between zones. Side (N/E/S/W) zones have a texture
// tick per TEX_COUNTS of travel; corners and the center have none.
#define ELIZ_ZONE_EDGE 10923
#define ELIZ_ZONE_HYST 1300
#define ELIZ_TEX_COUNTS 2500
#define ELIZ_TEX_DB -24
// a bump on every zone change, and on entering the band along the pad's
// outer edge (the sensor can lose a thumb that is still on the pad)
#define ELIZ_BUMP_DB -12
#define ELIZ_BUMP_DUR 20
#define ELIZ_PAD_EDGE 26000
// triggers act as keys in the desktop layers at a light pull (0..255)
#define ELIZ_TRIG_ON 24
#define ELIZ_TRIG_OFF 12

// sticks / triggers beyond these count as "in use" for layer latching
#define ELIZ_STICK_REST 8000
#define ELIZ_TRIG_REST 16
// a bond silent this long has dropped; matches the Xbox mode's window
#define ELIZ_CONN_MS 1200u

enum { LY_PAD, LY_MOUSE, LY_KBD, LY_N };

// Physical buttons that carry a meaning. Touch bits (pads, sticks, grips)
// select layers and are never bound.
#define ELIZ_BTN_MASK                                                          \
	(TB_A | TB_B | TB_X | TB_Y | TB_QAM | TB_R3 | TB_VIEW | TB_R4 |        \
	 TB_R5 | TB_RB | TB_DDN | TB_DRT | TB_DLF | TB_DUP | TB_MENU | TB_L3 | \
	 TB_STEAM | TB_L4 | TB_L5 | TB_LB | TB_RPADC | TB_LPADC | TB_R2 |      \
	 TB_L2)
#define ELIZ_PADCLICK (TB_RPADC | TB_LPADC)

// ---- USB: XInput interface 0 + one HID keyboard/mouse interface + CDC ----

enum { RID_KBD = 1, RID_MOUSE = 2, RID_WHEEL_MULT = 3, RID_MEDIA = 4 };
// Mouse with the Resolution Multiplier feature in the wheel's logical
// collection (Microsoft's "enhanced wheel" layout): logical 0..1 maps to a
// physical multiplier of 1..120, so a host that sets it reads the wheel in
// 1/120 notches. Input report: buttons, x, y, wheel (4 bytes).
static const uint8_t ELIZ_HID_DESC[] = {
	TUD_HID_REPORT_DESC_KEYBOARD(HID_REPORT_ID(RID_KBD)),
	0x05, 0x01, // Usage Page (Generic Desktop)
	0x09, 0x02, // Usage (Mouse)
	0xA1, 0x01, // Collection (Application)
	0x85, RID_MOUSE, //   Report ID
	0x09, 0x01, //   Usage (Pointer)
	0xA1, 0x00, //   Collection (Physical)
	0x05, 0x09, //     Usage Page (Button)
	0x19, 0x01, 0x29, 0x05, //     Usage Min 1, Max 5
	0x15, 0x00, 0x25, 0x01, //     Logical 0..1
	0x95, 0x05, 0x75, 0x01, 0x81, 0x02, //     5 x 1 bit, Input
	0x95, 0x01, 0x75, 0x03, 0x81, 0x03, //     3 bit pad
	0x05, 0x01, //     Usage Page (Generic Desktop)
	0x09, 0x30, 0x09, 0x31, //     Usage X, Y
	0x15, 0x81, 0x25, 0x7F, //     Logical -127..127
	0x75, 0x08, 0x95, 0x02, 0x81, 0x06, //     2 x 8 bit, Input Rel
	0xA1, 0x02, //     Collection (Logical)
	0x85, RID_WHEEL_MULT, //       Report ID
	0x09, 0x48, //       Usage (Resolution Multiplier)
	0x15, 0x00, 0x25, 0x01, //       Logical 0..1
	0x35, 0x01, 0x45, ELIZ_WHEEL_HIRES, //       Physical 1..120
	0x75, 0x02, 0x95, 0x01, 0xB1, 0x02, //       2 bit, Feature
	0x75, 0x06, 0xB1, 0x03, //       6 bit pad
	0x85, RID_MOUSE, //       Report ID
	0x09, 0x38, //       Usage (Wheel)
	0x15, 0x81, 0x25, 0x7F, //       Logical -127..127
	0x35, 0x00, 0x45, 0x00, //       Physical 0
	0x75, 0x08, 0x95, 0x01, 0x81, 0x06, //       8 bit, Input Rel
	0xC0, //     End Collection
	0xC0, //   End Collection
	0xC0, // End Collection
	// media keys: one 16-bit consumer usage, 0 = none
	TUD_HID_REPORT_DESC_CONSUMER(HID_REPORT_ID(RID_MEDIA)),
};
static Adafruit_USBD_HID g_elizHid;
// set by the host through RID_WHEEL_MULT (usbd task)
static volatile bool s_wheelHiRes;

static uint16_t elizGetReport(uint8_t id, hid_report_type_t type,
			      uint8_t *buf, uint16_t len)
{
	if (id != RID_WHEEL_MULT || type != HID_REPORT_TYPE_FEATURE || !len)
		return 0;
	buf[0] = s_wheelHiRes ? 1 : 0;
	return 1;
}

static void elizSetReport(uint8_t id, hid_report_type_t type,
			  const uint8_t *buf, uint16_t len)
{
	if (id == RID_WHEEL_MULT && type == HID_REPORT_TYPE_FEATURE && len)
		s_wheelHiRes = buf[0] & 0x03;
}

// Interface 0 of the genuine pad only: the vendor 0x21 descriptor and the two
// interrupt endpoints. xi_open() checks these bytes, including EP 0x81/0x02.
#define ELIZ_XI_LEN 40

class ElizXInputItf : public Adafruit_USBD_Interface {
    public:
	uint16_t getInterfaceDescriptor(uint8_t itfnum, uint8_t *buf,
					uint16_t bufsize) override
	{
		(void)itfnum;
		if (!buf)
			return ELIZ_XI_LEN;
		if (bufsize < ELIZ_XI_LEN)
			return 0;
		uint8_t num = TinyUSBDevice.allocInterface(1);
		uint8_t in = TinyUSBDevice.allocEndpoint(TUSB_DIR_IN);
		// EP OUT 0x01 stays unused: the XInput body pins OUT to 0x02
		TinyUSBDevice.allocEndpoint(TUSB_DIR_OUT);
		uint8_t out = TinyUSBDevice.allocEndpoint(TUSB_DIR_OUT);
		if (num != 0 || in != 0x81 || out != 0x02)
			return 0;
		memcpy(buf, XINPUT_CONFIG_BODY, ELIZ_XI_LEN);
		return ELIZ_XI_LEN;
	}
	bool begin()
	{
		return TinyUSBDevice.addInterface(*this);
	}
};
static ElizXInputItf g_elizXi;

// MS OS 1.0: Windows' Xbox driver (xusb22.inf) binds either the genuine
// 045E:028E device or the compatible ID USB\MS_COMP_XUSB10. Only the latter
// works for one interface of a composite device, so interface 0 advertises it
// through the 0xEE string + extended compat ID descriptor. Windows caches the
// "no MS OS descriptor" answer per VID/PID/bcdDevice, so bump bcdDevice if an
// earlier build ever enumerated without it.
#define ELIZ_MS_VENDOR 0x20

const uint16_t *elizStringDescriptor(uint8_t index)
{
	static const uint16_t msos[] = { (TUSB_DESC_STRING << 8) | 18,
					 'M',
					 'S',
					 'F',
					 'T',
					 '1',
					 '0',
					 '0',
					 ELIZ_MS_VENDOR };
	return index == 0xEE ? msos : nullptr;
}

bool elizVendorControl(uint8_t rhport, uint8_t stage,
		       const tusb_control_request_t *request)
{
	// extended compat ID descriptor, one function: interface 0 -> XUSB10
	static const uint8_t compat[40] = {
		40,  0,	  0,   0,   0x00, 0x01, 0x04, 0x00, // len, ver, index
		1,   0,	  0,   0,   0,	  0,	0,    0, // count, reserved
		0,   1,	  'X', 'U', 'S',  'B',	'1',  '0', // itf 0, compat ID
		0,   0,	  0,   0,   0,	  0,	0,    0, // sub-compat ID
		0,   0,	  0,   0,   0,	  0, // reserved
	};
	if (request->bRequest != ELIZ_MS_VENDOR || request->wIndex != 4 ||
	    !(request->bmRequestType & 0x80))
		return false;
	if (stage != CONTROL_STAGE_SETUP)
		return true;
	uint16_t len = request->wLength < sizeof compat ? request->wLength :
							  sizeof compat;
	return tud_control_xfer(rhport, request, (void *)compat, len);
}

// ---- input state ----

struct Pad {
	bool on; // touched last report
	bool click;
	int16_t x, y;
	uint8_t bx, by; // keyboard zone column / row band, 0..2
	float tex; // pad travel since the last texture tick
	uint8_t kst; // KS_* typing state of this pad's click
	uint8_t key; // HID key held, 0 = none
	uint8_t keyZone; // zone the held key was typed in
	uint8_t zone; // zone last report
	bool edge; // in the outer edge band
};

struct GamepadOut {
	uint16_t btn;
	uint8_t lt, rt;
	int16_t lx, ly, rx, ry;
};

enum { PAD_R, PAD_L };
static const uint32_t CLICK_BIT[2] = { TB_RPADC, TB_LPADC };
static const uint32_t TRIG_BIT[2] = { TB_R2, TB_L2 };
// A click in the center is armed: sliding out types the zone it enters. A
// held key ends when the thumb leaves its zone; sliding straight into another
// key zone types nothing (dead) until the thumb returns to the center. Both
// centers clicked together is a command (KS_CHORD), inert until released.
enum { KS_IDLE, KS_CENTER, KS_KEY, KS_DEAD, KS_CHORD };
enum { AN_LT, AN_RT, AN_LSTICK, AN_RSTICK, AN_N };
enum { AN_FREE, AN_OWNED, AN_BLOCKED };

static int s_slot = -1;
static uint8_t s_layer = LY_PAD, s_cand = LY_PAD;
static uint32_t s_candMs;
static uint32_t s_prevBtn;
static uint32_t s_own[LY_N];
static uint8_t s_an[AN_N];
static Pad s_pad[2];
static GamepadOut s_gp;
static uint8_t s_sentMBtn, s_sentMod, s_sentKeys[6];
static uint16_t s_sentMedia;
static float s_carryX, s_carryY;
static float s_tickDist;
static uint32_t s_tickMs;

// circular scroll on the left pad
static struct {
	bool on;
	bool engaged; // scrolling since the thumb landed
	float anchor; // angle where the wheel last moved, radians
	float notches; // wheel travel not yet sent, notches
	float tick; // travel since the last notch tick
	float vel; // the wheel's speed while turned, notches per report
	uint32_t sampleUs; // when the pad last gave a new position
	int16_t prevP; // pad pressure on the previous report
	float liftR; // the thumb rolling off: as the ball's lift dead zone
	float glide; // momentum after a fast lift, notches per report
	uint32_t sentMs; // when the last wheel report went out
	bool wasActive; // scrolling on the previous report
} s_scroll;

// serial console '~w': one line per report while scrolling or coasting
bool g_elizScrollTrace;

// the virtual trackball behind the cursor (cursor px per report)
static struct {
	bool held; // thumb is driving the cursor
	float vx, vy;
	float gapX, gapY; // thumb travel the cursor has not caught up to
	float speed;
	uint32_t sampleUs; // when the pad last reported a new thumb position
	int16_t prevP;
	float liftR; // lift dead zone radius, px
} s_ball;

static bool bondAlive(int slot, unsigned long now)
{
	return slot >= 0 && slot < NSLOT && g_slot[slot].used &&
	       g_connReplyMs[slot] && now - g_connReplyMs[slot] <= ELIZ_CONN_MS;
}

// Both pads = keyboard, unless a pad click is held as a mouse button: then
// the other thumb joins in moving the cursor, for long drags.
static uint8_t layerFromTouch(uint32_t b)
{
	bool l = b & TB_LPADT, r = b & TB_RPADT;
	if (l && r)
		return s_own[LY_MOUSE] & ELIZ_PADCLICK ? LY_MOUSE : LY_KBD;
	return (l || r) ? LY_MOUSE : LY_PAD;
}

// ---- gamepad layer: the back buttons carry A/B/X/Y ----

static uint16_t gamepadButtons(uint32_t b)
{
	static const struct {
		uint32_t tb;
		uint16_t xb;
	} MAP[] = {
		{ TB_R4, XB_A },
		{ TB_L4, XB_B },
		{ TB_R5, XB_X },
		{ TB_L5, XB_Y },
		{ TB_A, XB_A },
		{ TB_B, XB_B },
		{ TB_X, XB_X },
		{ TB_Y, XB_Y },
		{ TB_DUP, XB_DUP },
		{ TB_DDN, XB_DDOWN },
		{ TB_DLF, XB_DLEFT },
		{ TB_DRT, XB_DRIGHT },
		// TB_VIEW is the physical Start side; see triton.h
		{ TB_VIEW, XB_START },
		{ TB_MENU, XB_BACK },
		{ TB_STEAM, XB_GUIDE },
		{ TB_LB, XB_LB },
		{ TB_RB, XB_RB },
		{ TB_L3, XB_L3 },
		{ TB_R3, XB_R3 },
	};
	uint16_t out = 0;
	for (const auto &m : MAP)
		if (b & m.tb)
			out |= m.xb;
	return out;
}

// ---- desktop keys (mouse and keyboard layers) ----

struct ElizKey {
	uint32_t tb;
	uint8_t mod;
	uint8_t key;
	uint8_t layers; // ON_* layers the binding applies in
};
#define ON_PAD (1u << LY_PAD)
#define ON_MOUSE (1u << LY_MOUSE)
#define ON_KBD (1u << LY_KBD)
#define ON_DESK (ON_MOUSE | ON_KBD)
static const ElizKey DESK_KEYS[] = {
	// back buttons: modifiers, reachable while both thumbs are on the pads.
	// Shift over Ctrl on the left as on a keyboard, so both can be held
	// while the right thumb drives the cursor; Alt mirrors Ctrl.
	{ TB_L4, KEYBOARD_MODIFIER_LEFTSHIFT, 0, ON_DESK },
	{ TB_L5, KEYBOARD_MODIFIER_LEFTCTRL, 0, ON_DESK },
	{ TB_R4, KEYBOARD_MODIFIER_LEFTGUI, 0, ON_DESK },
	{ TB_R5, KEYBOARD_MODIFIER_LEFTALT, 0, ON_DESK },
	// face buttons: a navigation cluster by direction while mousing; off
	// while typing, where both thumbs are on the pads
	{ TB_Y, 0, HID_KEY_PAGE_UP, ON_MOUSE },
	{ TB_A, 0, HID_KEY_PAGE_DOWN, ON_MOUSE },
	{ TB_X, 0, HID_KEY_HOME, ON_MOUSE },
	{ TB_B, 0, HID_KEY_END, ON_MOUSE },
	// QAM has no Xbox equivalent: screenshots from the gamepad layer
	{ TB_QAM, 0, HID_KEY_PRINT_SCREEN, ON_PAD },
	// arrows, Esc and the menu key only while mousing: with both thumbs on
	// the pads they are out of reach, and offering them only confused
	{ TB_DUP, 0, HID_KEY_ARROW_UP, ON_MOUSE },
	{ TB_DDN, 0, HID_KEY_ARROW_DOWN, ON_MOUSE },
	{ TB_DLF, 0, HID_KEY_ARROW_LEFT, ON_MOUSE },
	{ TB_DRT, 0, HID_KEY_ARROW_RIGHT, ON_MOUSE },
	// index fingers: the most frequent non-letter keys, reachable while the
	// thumbs are on the pads (Alt-Tab = R5 + LB from the mouse layer)
	{ TB_R2, 0, HID_KEY_SPACE, ON_DESK },
	{ TB_L2, 0, HID_KEY_BACKSPACE, ON_DESK },
	{ TB_RB, 0, HID_KEY_ENTER, ON_DESK },
	{ TB_LB, 0, HID_KEY_TAB, ON_DESK },
	// the center buttons echo their gamepad roles: Back = Esc, Start (the
	// "menu" button) = the context-menu key
	{ TB_MENU, 0, HID_KEY_ESCAPE, ON_MOUSE },
	{ TB_VIEW, 0, HID_KEY_APPLICATION, ON_MOUSE },
};

// ---- keyboard layer: 3x3 zones per pad ----

enum { Z_NW, Z_N, Z_NE, Z_W, Z_C, Z_E, Z_SW, Z_S, Z_SE, Z_COUNT };

// A click in one of a pad's 8 outer zones types KEYMAP[pad][zone the other
// thumb rests in][ring position], ring = clockwise from N. Layout and its
// rationale: docs/KEYBOARD_LAYOUT.md. Letters are placed by English letter
// pairs (vowels on the left thumb, consonants on the right, so thumbs
// alternate); numbers and F-keys are placed to be memorable.
// Each symbol appears once as its main-keyboard key, so Shift acts as on a
// keyboard (/ -> ?). The number pages use numpad operators, the keyboard's
// own duplicates, which ignore Shift; = has no numpad key there.
static const int8_t RING_POS[Z_COUNT] = { 7, 0, 1, 6, -1, 2, 5, 4, 3 };
#define K(k) HID_KEY_##k
static const uint8_t KEYMAP[2][Z_COUNT][8] = {
	// PAD_R clicked; ring N NE E SE S SW W NW
	{
		{},
		// left thumb N
		{ K(T), K(F), K(M), K(Z), K(V), K(J), K(D), K(W) },
		{},
		// left thumb W: ] on E, backslash on N (the keyboard's
		// right-hand pair). Thumbs spread outward, each bracket's zone
		// is the other's page, so [ and ] alternate without moving.
		{ K(BACKSLASH), 0, K(BRACKET_RIGHT), 0, 0, 0, 0, 0 },
		// left thumb center
		{ K(S), K(P), K(R), K(B), K(L), K(G), K(N), K(C) },
		{},
		// left thumb SW: F6..F10 where 6..0 are, F12 bottom
		{ K(F7), K(F8), K(F10), 0, K(F12), 0, K(F9), K(F6) },
		// left thumb S: 6..0 on the top five, numpad keys below
		{ K(7), K(8), K(0), K(KEYPAD_DECIMAL), K(KEYPAD_DIVIDE),
		  K(KEYPAD_MULTIPLY), K(9), K(6) },
		{},
	},
	// PAD_L clicked
	{
		{},
		// right thumb N
		{ K(H), K(Q), K(APOSTROPHE), K(MINUS), K(SLASH), K(SEMICOLON),
		  K(K), K(X) },
		{},
		{},
		// right thumb center
		{ K(E), K(U), K(O), K(PERIOD), K(Y), K(COMMA), K(A), K(I) },
		// right thumb E: [ on W, backtick on N (left-hand pair)
		{ K(GRAVE), 0, 0, 0, 0, 0, K(BRACKET_LEFT), 0 },
		{},
		// right thumb S: 1..5 on the top five, numpad keys below
		{ K(2), K(3), K(5), K(EQUAL), K(KEYPAD_ADD),
		  K(KEYPAD_SUBTRACT), K(4), K(1) },
		// right thumb SE: F1..F5 where 1..5 are, F11 bottom
		{ K(F2), K(F3), K(F5), 0, K(F11), 0, K(F4), K(F1) },
	},
};
#undef K

// one axis of the grid: 0 = low third, 1 = middle, 2 = high
static uint8_t zoneBand(int v, uint8_t cur)
{
	int lo = -ELIZ_ZONE_EDGE, hi = ELIZ_ZONE_EDGE;
	if (cur == 0) {
		lo += ELIZ_ZONE_HYST;
	} else if (cur == 2) {
		hi -= ELIZ_ZONE_HYST;
	} else {
		lo -= ELIZ_ZONE_HYST;
		hi += ELIZ_ZONE_HYST;
	}
	return v < lo ? 0 : v > hi ? 2 : 1;
}

// Update and return the pad's zone. Call before padDelta() for the report.
static uint8_t padZone(Pad &p, bool touch, int16_t x, int16_t y)
{
	if (!touch || !p.on)
		p.bx = p.by = 1;
	if (!touch)
		return Z_C;
	p.bx = zoneBand(x, p.bx);
	p.by = zoneBand(y, p.by);
	// pad y grows upward
	return (2 - p.by) * 3 + p.bx;
}

static bool zoneSide(uint8_t z)
{
	return z == Z_N || z == Z_E || z == Z_S || z == Z_W;
}

// ---- helpers ----

static int clampI8(float v)
{
	return v > 127 ? 127 : v < -127 ? -127 : (int)v;
}

static bool stickActive(int16_t x, int16_t y)
{
	return x > ELIZ_STICK_REST || x < -ELIZ_STICK_REST ||
	       y > ELIZ_STICK_REST || y < -ELIZ_STICK_REST;
}

// Analog inputs follow the same latching rule as buttons: one in use when the
// layer changes keeps its old meaning (gamepad, or off) until it rests.
static bool analogPasses(uint8_t ch, bool active)
{
	if (!active)
		s_an[ch] = AN_FREE;
	else if (s_an[ch] == AN_FREE)
		s_an[ch] = s_layer == LY_PAD ? AN_OWNED : AN_BLOCKED;
	if (s_layer == LY_PAD)
		return s_an[ch] != AN_BLOCKED;
	return s_an[ch] == AN_OWNED;
}

// Pad travel since last report, in pad counts; 0 on the first touched report.
static void padDelta(Pad &p, bool touch, int16_t x, int16_t y, int *dx,
		     int *dy)
{
	*dx = *dy = 0;
	if (touch && p.on) {
		int ddx = x - p.x, ddy = y - p.y;
		if (ddx <= ELIZ_MAX_DELTA && ddx >= -ELIZ_MAX_DELTA &&
		    ddy <= ELIZ_MAX_DELTA && ddy >= -ELIZ_MAX_DELTA) {
			*dx = ddx;
			*dy = ddy;
		}
	}
	p.on = touch;
	p.x = x;
	p.y = y;
}

static float followRate(float speed)
{
	if (speed <= ELIZ_FOLLOW_SLOW)
		return 1.0f;
	if (speed >= ELIZ_FOLLOW_FAST)
		return ELIZ_FOLLOW_MIN;
	float t = (speed - ELIZ_FOLLOW_SLOW) /
		  (ELIZ_FOLLOW_FAST - ELIZ_FOLLOW_SLOW);
	t = t * t * (3.0f - 2.0f * t);
	return 1.0f + (ELIZ_FOLLOW_MIN - 1.0f) * t;
}

// see ELIZ_DEADZONE_PX: full at 0 and from DZ_FULL up, 0 at DZ_START
static float deadZone(int16_t p, bool click)
{
	if (click || p >= ELIZ_DZ_FULL)
		return ELIZ_DEADZONE_PX;
	float t;
	if (p < ELIZ_DZ_START) {
		t = 1.0f - (float)(p > 0 ? p : 0) / (float)ELIZ_DZ_START;
		return ELIZ_DEADZONE_PX * t * t;
	}
	t = (float)(p - ELIZ_DZ_START) / (float)(ELIZ_DZ_FULL - ELIZ_DZ_START);
	return ELIZ_DEADZONE_PX * (1.0f - (1.0f - t) * (1.0f - t));
}

// Update s_ball.speed from the thumb's travel this report (see
// ELIZ_REPORT_US).
static void thumbSpeed(float tx, float ty)
{
	uint32_t now = micros();
	uint32_t dt = now - s_ball.sampleUs;
	float v;
	if (tx || ty) {
		s_ball.sampleUs = now;
		float reports = dt / ELIZ_REPORT_US;
		v = sqrtf(tx * tx + ty * ty) / (reports > 1 ? reports : 1);
	} else if (dt > ELIZ_STILL_US) {
		v = 0;
	} else {
		return;
	}
	s_ball.speed += (v - s_ball.speed) * ELIZ_SPEED_ALPHA;
}

// Advance the trackball one report and return the cursor motion in px.
// (tx, ty) is the thumb's travel; `held` means it is driving the cursor.
static void ballStep(bool held, bool touch, float tx, float ty, int16_t p,
		     bool click, float *fx, float *fy)
{
	if (held) {
		if (!s_ball.held) {
			s_ball.speed = sqrtf(s_ball.vx * s_ball.vx +
					     s_ball.vy * s_ball.vy);
			s_ball.sampleUs = micros();
			s_ball.vx = s_ball.vy = 0;
			s_ball.gapX = s_ball.gapY = 0;
			s_ball.prevP = p;
			s_ball.liftR = 0;
		}
		s_ball.held = true;
		int drop = s_ball.prevP - p;
		s_ball.prevP = p;
		float lr = drop > 0 ? drop * ELIZ_LIFT_GAIN : 0.0f;
		s_ball.liftR *= ELIZ_LIFT_DECAY;
		if (lr > s_ball.liftR)
			s_ball.liftR = lr < ELIZ_LIFT_MAX ? lr : ELIZ_LIFT_MAX;
		s_ball.gapX += tx;
		s_ball.gapY += ty;
		thumbSpeed(tx, ty);
		float r = deadZone(p, click);
		if (s_ball.liftR > r)
			r = s_ball.liftR;
		float gap = sqrtf(s_ball.gapX * s_ball.gapX +
				  s_ball.gapY * s_ball.gapY);
		float k = gap > r ? (gap - r) / gap * followRate(s_ball.speed) :
				    0.0f;
		*fx = s_ball.gapX * k;
		*fy = s_ball.gapY * k;
		s_ball.gapX -= *fx;
		s_ball.gapY -= *fy;
		float a = ELIZ_LAUNCH_ALPHA * ELIZ_LIFT_HOLD_PX /
			  (ELIZ_LIFT_HOLD_PX + s_ball.liftR);
		s_ball.vx += (*fx - s_ball.vx) * a;
		s_ball.vy += (*fy - s_ball.vy) * a;
		return;
	}
	s_ball.held = false;
	if (touch) {
		s_ball.vx *= ELIZ_GHOST_DECAY;
		s_ball.vy *= ELIZ_GHOST_DECAY;
	}
	float sp = sqrtf(s_ball.vx * s_ball.vx + s_ball.vy * s_ball.vy);
	float nsp = sp > ELIZ_GLIDE_MAX ?
			    ELIZ_GLIDE_MAX :
			    sp * ELIZ_GLIDE_DECAY - ELIZ_GLIDE_DRAG;
	if (nsp <= 0) {
		s_ball.vx = s_ball.vy = 0;
	} else {
		s_ball.vx *= nsp / sp;
		s_ball.vy *= nsp / sp;
	}
	*fx = s_ball.vx;
	*fy = s_ball.vy;
}

// ---- output ----

// The Guide's practice mode: while it shows its full-screen window it asks
// for the controller's input (a "~g" at least every GUIDE_BEAT_MS) and the
// host gets none, only "nothing pressed" -- except cursor movement, so the
// pointer can still be moved off something it covers. The Guide still sees
// everything through its serial feed. The beat comes from the Guide's
// window thread, so a Guide that hangs, crashes or closes the port hands the
// controller back within GUIDE_BEAT_MS.
#define GUIDE_BEAT_MS 1000
static uint32_t s_guideBeat;

void elizGuideBeat()
{
	s_guideBeat = millis() | 1;
}

void elizGuideRelease()
{
	s_guideBeat = 0;
}

static bool guideHolds()
{
	if (!s_guideBeat)
		return false;
	if (!Serial || millis() - s_guideBeat > GUIDE_BEAT_MS)
		s_guideBeat = 0;
	return s_guideBeat;
}

static void sendKeyboard(uint8_t mod, const uint8_t keys[6])
{
	static const uint8_t none[6] = {};
	if (guideHolds()) {
		mod = 0;
		keys = none;
	}
	if (mod == s_sentMod && !memcmp(keys, s_sentKeys, 6))
		return;
	s_sentMod = mod;
	memcpy(s_sentKeys, keys, 6);
	uint8_t rep[8] = { mod, 0, keys[0], keys[1], keys[2], keys[3], keys[4],
			   keys[5] };
	usbTxHid(&g_elizHid, RID_KBD, rep, sizeof rep);
}

static void sendMouse(uint8_t btn, int dx, int dy, int wheel)
{
	if (guideHolds())
		btn = wheel = 0;
	if (!dx && !dy && !wheel && btn == s_sentMBtn)
		return;
	s_sentMBtn = btn;
	int8_t m[4] = { (int8_t)btn, (int8_t)dx, (int8_t)dy, (int8_t)wheel };
	usbTxHid(&g_elizHid, RID_MOUSE, m, sizeof m);
}

static void sendMedia(uint16_t usage)
{
	if (guideHolds())
		usage = 0;
	if (usage == s_sentMedia)
		return;
	s_sentMedia = usage;
	usbTxHid(&g_elizHid, RID_MEDIA, &usage, sizeof usage);
}

static void haptic(uint8_t rid, const uint8_t *p, uint8_t n)
{
	if (s_slot >= 0 && hapticLinkUp(s_slot))
		relayEnqueue(rid, p, n, true, (uint8_t)s_slot);
}

static void hapticPulse(uint8_t side, uint16_t us)
{
	uint8_t p[7] = { side, (uint8_t)us, (uint8_t)(us >> 8), 0, 0, 1, 0 };
	haptic(0x81, p, sizeof p);
}

static void hapticClick(uint8_t side, bool down)
{
	hapticPulse(side, down ? ELIZ_CLICK_ON_US : ELIZ_RELEASE_ON_US);
}

static void hapticKey(uint8_t side, bool down)
{
	hapticPulse(side, down ? ELIZ_KEY_ON_US : ELIZ_KEY_RELEASE_ON_US);
}

static void hapticTone(uint8_t side, int8_t gainDb, uint16_t dur)
{
	uint8_t p[9] = { side,
			 (uint8_t)gainDb,
			 (uint8_t)ELIZ_TONE_HZ,
			 (uint8_t)(ELIZ_TONE_HZ >> 8),
			 (uint8_t)dur,
			 (uint8_t)(dur >> 8),
			 0,
			 0,
			 0 };
	haptic(0x83, p, sizeof p);
}

static void hapticTick(uint8_t side)
{
	hapticTone(side, ELIZ_TICK_DB, ELIZ_TICK_DUR);
}

// one faint tick per ELIZ_TICK_PX of cursor travel, touching or gliding
static void moveTick(float px, uint32_t now)
{
	s_tickDist += px;
	if (s_tickDist < ELIZ_TICK_PX || now - s_tickMs < ELIZ_TICK_MIN_MS)
		return;
	s_tickDist = 0;
	s_tickMs = now;
	hapticTick(TONE_SIDE[PAD_R]);
}

static void releaseAll()
{
	static const uint8_t none[6] = {};
	s_sentMod = 0xFF;
	sendKeyboard(0, none);
	s_sentMBtn = 0xFF;
	sendMouse(0, 0, 0, 0);
	s_sentMedia = 0xFFFF;
	sendMedia(0);
	memset(s_own, 0, sizeof s_own);
	memset(s_an, 0, sizeof s_an);
	memset(s_pad, 0, sizeof s_pad);
	memset(&s_gp, 0, sizeof s_gp);
	memset(&s_ball, 0, sizeof s_ball);
	s_prevBtn = 0;
	s_layer = s_cand = LY_PAD;
	s_carryX = s_carryY = 0;
	memset(&s_scroll, 0, sizeof s_scroll);
}

// The controller's pad-click and trigger-click bits replaced by threshold
// ones with hysteresis: pad clicks from pressure, trigger "keys" at a light
// pull (the controller's own trigger bit needs a full pull).
static uint32_t deriveButtons(const PuckInput &in)
{
	static bool r, l, rt2, lt2;
	bool rt = in.buttons & TB_RPADT, lt = in.buttons & TB_LPADT;
	int16_t on = rt && lt ? ELIZ_KEY_CLICK_ON : ELIZ_CLICK_ON;
	int16_t off = rt && lt ? ELIZ_KEY_CLICK_OFF : ELIZ_CLICK_OFF;
	r = rt && (r ? in.rpz > off : in.rpz >= on);
	l = lt && (l ? in.lpz > off : in.lpz >= on);
	rt2 = rt2 ? in.rt > ELIZ_TRIG_OFF : in.rt >= ELIZ_TRIG_ON;
	lt2 = lt2 ? in.lt > ELIZ_TRIG_OFF : in.lt >= ELIZ_TRIG_ON;
	uint32_t b = in.buttons & ~(ELIZ_PADCLICK | TB_R2 | TB_L2);
	if (r)
		b |= TB_RPADC;
	if (l)
		b |= TB_LPADC;
	if (rt2)
		b |= TB_R2;
	if (lt2)
		b |= TB_L2;
	return b;
}

// Debounced layer switch, with the keyboard on/off tones.
static void layerStep(uint8_t touchLayer, uint32_t now)
{
	if (touchLayer == s_layer) {
		s_cand = s_layer;
		return;
	}
	if (touchLayer != s_cand) {
		s_cand = touchLayer;
		s_candMs = now;
		return;
	}
	if (now - s_candMs < ELIZ_SWITCH_MS)
		return;
	bool on = touchLayer == LY_KBD, off = s_layer == LY_KBD;
	s_layer = touchLayer;
	if (on || off) {
		int8_t db = on ? ELIZ_KBD_ON_DB : ELIZ_KBD_OFF_DB;
		for (int i = 0; i < 2; i++)
			hapticTone(TONE_SIDE[i], db, ELIZ_KBD_DUR);
	}
}

// New presses join the current layer and keep it until released. Pad clicks
// use the undebounced layer: the thumb is down by definition, and a quick
// tap would otherwise land in the gamepad layer.
static void ownButtons(uint32_t b, uint8_t touchLayer)
{
	uint32_t pressed = b & ~s_prevBtn;
	uint32_t released = s_prevBtn & ~b;
	s_prevBtn = b;
	for (int l = 0; l < LY_N; l++)
		s_own[l] &= ~released;
	s_own[s_layer] |= pressed & ~ELIZ_PADCLICK;
	s_own[touchLayer] |= pressed & ELIZ_PADCLICK;
}

static void gamepadStep(const PuckInput &in)
{
	s_gp.btn = gamepadButtons(s_own[LY_PAD]);
	bool lt = analogPasses(AN_LT, in.lt > ELIZ_TRIG_REST);
	bool rt = analogPasses(AN_RT, in.rt > ELIZ_TRIG_REST);
	bool ls = analogPasses(AN_LSTICK, stickActive(in.lx, in.ly));
	bool rs = analogPasses(AN_RSTICK, stickActive(in.rx, in.ry));
	s_gp.lt = lt ? in.lt : 0;
	s_gp.rt = rt ? in.rt : 0;
	s_gp.lx = ls ? in.lx : 0;
	s_gp.ly = ls ? in.ly : 0;
	s_gp.rx = rs ? in.rx : 0;
	s_gp.ry = rs ? in.ry : 0;
}

static void typeKey(int i, uint8_t key, uint8_t z)
{
	s_pad[i].kst = KS_KEY;
	s_pad[i].key = key;
	s_pad[i].keyZone = z;
	hapticKey(PULSE_SIDE[i], true);
	// "KK pad zone" for the trainer's press flash; dropped, never waited
	// for, when the CDC buffer is full
	if (Serial && Serial.availableForWrite() >= 8)
		Serial.printf("KK %d %u\n", i, z);
}

// Pad clicks: a sharp pulse on each edge. In the keyboard layer a click types
// the key for its zone and the other thumb's zone; the center zone is inert
// (no key, no pulse). See KS_*.
static void padClicks(uint32_t raw, const uint8_t zone[2])
{
	for (int i = 0; i < 2; i++) {
		Pad &pd = s_pad[i];
		bool click = raw & CLICK_BIT[i];
		uint8_t z = zone[i];
		int8_t pos = RING_POS[z];
		uint8_t key = pos >= 0 ? KEYMAP[i][zone[1 - i]][pos] : 0;
		if (click != pd.click) {
			pd.click = click;
			if (!click) {
				if (pd.kst == KS_KEY)
					hapticKey(PULSE_SIDE[i], false);
				else if (pd.kst == KS_IDLE)
					hapticClick(PULSE_SIDE[i], false);
				else if (pd.kst == KS_CHORD)
					hapticTone(TONE_SIDE[i], ELIZ_BUMP_DB,
						   ELIZ_BUMP_DUR);
				pd.kst = KS_IDLE;
				pd.key = 0;
			} else if (!(s_own[LY_KBD] & CLICK_BIT[i])) {
				hapticClick(PULSE_SIDE[i], true);
			} else if (pos < 0) {
				pd.kst = KS_CENTER;
			} else {
				typeKey(i, key, z);
			}
			continue;
		}
		if (!click)
			continue;
		if (pd.kst == KS_CENTER && pos >= 0) {
			typeKey(i, key, z);
		} else if (pd.kst == KS_KEY && z != pd.keyZone) {
			hapticKey(PULSE_SIDE[i], false);
			pd.key = 0;
			pd.kst = pos < 0 ? KS_CENTER : KS_DEAD;
		} else if (pd.kst == KS_DEAD && pos < 0) {
			pd.kst = KS_CENTER;
		}
	}
	// both centers clicked together: move the trainer overlay
	if (s_pad[PAD_R].kst == KS_CENTER && s_pad[PAD_L].kst == KS_CENTER) {
		for (int i = 0; i < 2; i++) {
			s_pad[i].kst = KS_CHORD;
			hapticTone(TONE_SIDE[i], ELIZ_BUMP_DB, ELIZ_BUMP_DUR);
		}
		if (Serial && Serial.availableForWrite() >= 4)
			Serial.printf("KP\n");
	}
}

// Trigger keys have no physical click at a light pull: pulse the pad on that
// side on press and release, in the desktop layers.
static void triggerPulses(uint32_t raw)
{
	static bool down[2], keyed[2];
	for (int i = 0; i < 2; i++) {
		bool d = raw & TRIG_BIT[i];
		if (d == down[i])
			continue;
		down[i] = d;
		if (d)
			keyed[i] = s_layer != LY_PAD;
		if (keyed[i])
			hapticClick(PULSE_SIDE[i], d);
	}
}

// Keyboard texture: a faint tick per stretch of travel through a side zone,
// and a bump on each zone change and on reaching the pad's outer edge band.
static void zoneHaptics(const PuckInput &in, const uint8_t zone[2],
			const bool wasOn[2], const int travel[2])
{
	const int16_t px[2] = { in.rpx, in.lpx }, py[2] = { in.rpy, in.lpy };
	for (int i = 0; i < 2; i++) {
		Pad &pd = s_pad[i];
		bool kbd = s_layer == LY_KBD && pd.on;
		int reach = max(abs(px[i]), abs(py[i]));
		bool edge = pd.edge ? reach > ELIZ_PAD_EDGE - ELIZ_ZONE_HYST :
				      reach > ELIZ_PAD_EDGE;
		bool bump = kbd && wasOn[i] &&
			    (zone[i] != pd.zone || (edge && !pd.edge));
		pd.zone = zone[i];
		pd.edge = pd.on && edge;
		if (bump)
			hapticTone(TONE_SIDE[i], ELIZ_BUMP_DB, ELIZ_BUMP_DUR);
		if (!kbd || !zoneSide(zone[i])) {
			pd.tex = 0;
			continue;
		}
		pd.tex += travel[i];
		if (pd.tex >= ELIZ_TEX_COUNTS) {
			pd.tex = 0;
			hapticTone(TONE_SIDE[i], ELIZ_TEX_DB, ELIZ_TICK_DUR);
		}
	}
}

// Guide feed (tools/guide): a "KS layer zoneR zoneL
// shift" line whenever one of them changes, only while a host holds the serial
// port open, and skipped (retried next report) rather than blocking when the
// CDC buffer is full.
static uint8_t s_ksLast[4] = { 0xFF, 0xFF, 0xFF, 0xFF };

static void streamState(const uint8_t zone[2])
{
	const uint8_t shifts = KEYBOARD_MODIFIER_LEFTSHIFT |
			       KEYBOARD_MODIFIER_RIGHTSHIFT;
	// s_sentMod is 0xFF ("unknown") right after releaseAll()
	bool shift = s_sentMod != 0xFF && (s_sentMod & shifts);
	const uint8_t cur[4] = { s_layer, zone[PAD_R], zone[PAD_L], shift };
	if (!memcmp(cur, s_ksLast, sizeof cur) || !Serial ||
	    Serial.availableForWrite() < 24)
		return;
	memcpy(s_ksLast, cur, sizeof cur);
	Serial.printf("KS %u %u %u %u\n", cur[0], cur[1], cur[2], cur[3]);
}

// Live input for the Guide's controller drawing: "KB buttons lt rt lx ly rx
// ry lpx lpy rpx rpy" (buttons in hex as the Guide sees them, with derived
// pad clicks and trigger bits; triggers 0..31; sticks and pad positions
// -32..32, pads 0 when untouched). Coarse steps keep stick and pad jitter
// from flooding the port; sent only on a change, at most every 8 ms, and
// dropped rather than waited for when the CDC buffer is full.
static void streamInput(const PuckInput &in, uint32_t b, uint32_t now)
{
	static int16_t last[10];
	static uint32_t lastB = 0xFFFFFFFF, lastMs;
	bool lTouch = b & TB_LPADT, rTouch = b & TB_RPADT;
	const int16_t cur[10] = {
		(int16_t)(in.lt >> 3),
		(int16_t)(in.rt >> 3),
		(int16_t)(in.lx / 1024),
		(int16_t)(in.ly / 1024),
		(int16_t)(in.rx / 1024),
		(int16_t)(in.ry / 1024),
		(int16_t)(lTouch ? in.lpx / 1024 : 0),
		(int16_t)(lTouch ? in.lpy / 1024 : 0),
		(int16_t)(rTouch ? in.rpx / 1024 : 0),
		(int16_t)(rTouch ? in.rpy / 1024 : 0),
	};
	if (b == lastB && !memcmp(cur, last, sizeof cur))
		return;
	if (now - lastMs < 8 || !Serial || Serial.availableForWrite() < 72)
		return;
	lastB = b;
	lastMs = now;
	memcpy(last, cur, sizeof cur);
	Serial.printf("KB %lx %d %d %d %d %d %d %d %d %d %d\n",
		      (unsigned long)b, cur[0], cur[1], cur[2], cur[3], cur[4],
		      cur[5], cur[6], cur[7], cur[8], cur[9]);
}

void elizDumpKeymap()
{
	for (int pad = 0; pad < 2; pad++) {
		for (int z = 0; z < Z_COUNT; z++) {
			Serial.printf("KM %d %d", pad, z);
			for (int k = 0; k < 8; k++)
				Serial.printf(" %02X", KEYMAP[pad][z][k]);
			Serial.printf("\n");
		}
	}
	Serial.printf("KM end\n");
	// a freshly started trainer also needs the current state
	memset(s_ksLast, 0xFF, sizeof s_ksLast);
}

// Media keys in the mouse layer, where the left thumb is free: left stick
// up/down = volume, left/right = previous/next track, QAM = play/pause. The
// stick counts once it is in use in this layer (see analogPasses), so one
// still held from the gamepad layer stays a gamepad stick.
static void mediaStep(const PuckInput &in)
{
	uint16_t usage = 0;
	if (s_own[LY_MOUSE] & TB_QAM)
		usage = HID_USAGE_CONSUMER_PLAY_PAUSE;
	if (s_layer == LY_MOUSE && s_an[AN_LSTICK] == AN_BLOCKED) {
		bool vert = abs(in.ly) >= abs(in.lx);
		bool pos = vert ? in.ly > 0 : in.lx > 0;
		if (vert)
			usage = pos ? HID_USAGE_CONSUMER_VOLUME_INCREMENT :
				      HID_USAGE_CONSUMER_VOLUME_DECREMENT;
		else
			usage = pos ? HID_USAGE_CONSUMER_SCAN_NEXT :
				      HID_USAGE_CONSUMER_SCAN_PREVIOUS;
	}
	sendMedia(usage);
}

static void keyboardStep()
{
	uint8_t mod = 0, keys[6] = {}, nk = 0;
	for (const auto &k : DESK_KEYS) {
		uint32_t own = 0;
		for (int l = 0; l < LY_N; l++)
			if (k.layers & (1u << l))
				own |= s_own[l];
		if (!(own & k.tb))
			continue;
		mod |= k.mod;
		if (k.key && nk < 6)
			keys[nk++] = k.key;
	}
	for (const auto &pd : s_pad)
		if (pd.key && nk < 6)
			keys[nk++] = pd.key;
	sendKeyboard(mod, keys);
}

// angle difference folded into (-pi, pi]
static float wrapAngle(float d)
{
	const float turn = 2.0f * (float)M_PI;
	while (d > (float)M_PI)
		d -= turn;
	while (d <= -(float)M_PI)
		d += turn;
	return d;
}

// Wheel travel this report, in notches, from the left pad's angle (see
// ELIZ_SCROLL_REV).
static float circularScroll(bool touch, int16_t x, int16_t y)
{
	float r = sqrtf((float)x * x + (float)y * y) / 32767.0f;
	float a = atan2f((float)y, (float)x);
	if (!touch || !s_scroll.on) {
		s_scroll.on = touch;
		s_scroll.engaged = false;
		s_scroll.anchor = a;
		return 0;
	}
	float d = wrapAngle(a - s_scroll.anchor);
	if (s_scroll.engaged) {
		if (r < ELIZ_SCROLL_R_HOLD) {
			s_scroll.anchor = a;
			return 0;
		}
	} else {
		// The dead zone only decides whether a scroll starts, so a
		// click near the center never turns into one. Until then the
		// wheel follows the thumb, so the scroll starts where it enters
		// the outer ring instead of replaying the way out from the
		// center.
		float t = (r - ELIZ_SCROLL_R_IN) /
			  (ELIZ_SCROLL_R_OUT - ELIZ_SCROLL_R_IN);
		t = t < 0 ? 0 : t > 1 ? 1 : t;
		float dz = (float)M_PI * (1.0f - t) * (1.0f - t);
		if (d > dz) {
			d -= dz;
		} else if (d < -dz) {
			d += dz;
		} else {
			s_scroll.anchor = a;
			return 0;
		}
		s_scroll.engaged = true;
	}
	// the anchor stays within one turn, so the next difference wraps right
	s_scroll.anchor = wrapAngle(s_scroll.anchor + d);
	// counter-clockwise (angle up) scrolls up, like turning a wheel away
	return d * ELIZ_SCROLL_REV / (2.0f * (float)M_PI);
}

// Wheel report value this report: circular scroll with a texture tick every
// ELIZ_SCROLL_TICK notches, in 1/120 notches once the host enabled them.
// The wheel's speed while the thumb turns it, the way thumbSpeed and
// ballStep track the cursor's: measured per new pad position over the real
// time since the previous one (the pad repeats positions; none for STILL_US
// means the thumb is still), and smoothed by LAUNCH_ALPHA -- less while the
// pressure falls as the thumb rolls off (as the ball's lift dead zone), so
// the lift's wobble doesn't decide the coast.
static void wheelSpeed(float n, int16_t p)
{
	uint32_t now = micros();
	int drop = s_scroll.prevP - p;
	s_scroll.prevP = p;
	float lr = drop > 0 ? drop * ELIZ_LIFT_GAIN : 0.0f;
	s_scroll.liftR *= ELIZ_LIFT_DECAY;
	if (lr > s_scroll.liftR)
		s_scroll.liftR = lr < ELIZ_LIFT_MAX ? lr : ELIZ_LIFT_MAX;
	uint32_t dt = now - s_scroll.sampleUs;
	float v;
	if (n != 0) {
		s_scroll.sampleUs = now;
		float reports = dt / ELIZ_REPORT_US;
		v = n / (reports > 1 ? reports : 1);
		float cap = fabsf(s_scroll.vel) * ELIZ_SCROLL_OUTLIER +
			    ELIZ_SCROLL_FLING;
		v = v > cap ? cap : v < -cap ? -cap : v;
	} else if (dt > ELIZ_STILL_US) {
		v = 0;
	} else {
		return;
	}
	float a = ELIZ_LAUNCH_ALPHA * ELIZ_LIFT_HOLD_PX /
		  (ELIZ_LIFT_HOLD_PX + s_scroll.liftR);
	s_scroll.vel += (v - s_scroll.vel) * a;
}

static int wheelStep(bool active, bool lTouch, int16_t x, int16_t y,
		     int16_t p, uint32_t now)
{
	float n = circularScroll(active, x, y);
	if (active) {
		if (!s_scroll.wasActive) {
			s_scroll.vel = s_scroll.liftR = 0;
			s_scroll.sampleUs = micros();
			s_scroll.prevP = p;
		}
		wheelSpeed(n, p);
		s_scroll.glide = 0;
	} else {
		bool fling = fabsf(s_scroll.vel) >= ELIZ_SCROLL_FLING;
		if (s_scroll.wasActive && fling)
			s_scroll.glide = s_scroll.vel;
		if (lTouch || fabsf(s_scroll.glide) < ELIZ_SCROLL_STOP)
			s_scroll.glide = 0;
		n = s_scroll.glide;
		s_scroll.glide *= ELIZ_SCROLL_DECAY;
	}
	if (g_elizScrollTrace && (active || s_scroll.wasActive || n != 0) &&
	    Serial.availableForWrite() > 48)
		Serial.printf("W %lu %d %.2f %.3f %.3f %d\n",
			      (unsigned long)now, active ? 1 : 0,
			      sqrtf((float)x * x + (float)y * y) / 32767.0f, n,
			      active ? s_scroll.vel : s_scroll.glide, p);
	s_scroll.wasActive = active;
	bool stopped = !active && n == 0;
	if (stopped)
		s_scroll.tick = 0;
	s_scroll.notches += n;
	s_scroll.tick += fabsf(n);
	// texture ticks only under the thumb, not while gliding
	if (active && s_scroll.tick >= ELIZ_SCROLL_TICK) {
		s_scroll.tick -= ELIZ_SCROLL_TICK;
		hapticTick(TONE_SIDE[PAD_L]);
	}
	float per = s_wheelHiRes ? ELIZ_WHEEL_HIRES : 1;
	float pending = s_scroll.notches * per;
	// a stopped wheel sends what is left at once, and a step too big for
	// one report goes early, so batching never makes the wheel lag
	bool due = stopped || fabsf(pending) >= 127 ||
		   now - s_scroll.sentMs >= ELIZ_WHEEL_GAP_MS;
	if (!due)
		return 0;
	int wheel = clampI8(pending);
	if (wheel)
		s_scroll.sentMs = now;
	s_scroll.notches -= wheel / per;
	if (stopped && !wheel)
		s_scroll.notches = 0;
	return wheel;
}

// Mouse layer: right pad = trackball cursor, left pad = circular wheel. While
// a pad click is held as a button, both pads move the cursor (drag).
static void mouseStep(const PuckInput &in, bool deskOk, int rdx, int rdy,
		      int ldx, int ldy, uint32_t now)
{
	bool rTouch = in.buttons & TB_RPADT, lTouch = in.buttons & TB_LPADT;
	bool drag = deskOk && rTouch && lTouch;
	float k = 64.0f / (float)g_mDiv;
	int dx0 = rdx + (drag ? ldx : 0), dy0 = rdy + (drag ? ldy : 0);
	float tx = dx0 * k / ELIZ_PX_DIV, ty = -dy0 * k / ELIZ_PX_DIV;
	float fx, fy;
	ballStep(deskOk && rTouch, rTouch, tx, ty, in.rpz, s_pad[PAD_R].click,
		 &fx, &fy);
	if (g_elizTrace && Serial.availableForWrite() > 60)
		Serial.printf("R %lu %d %d %.1f %.1f %.1f %.1f %.1f %.1f %.1f "
			      "%.1f\n",
			      (unsigned long)now, rTouch ? 1 : 0, in.rpz, tx,
			      ty, fx, fy, s_ball.vx, s_ball.vy, s_ball.liftR,
			      s_ball.speed);
	moveTick(sqrtf(fx * fx + fy * fy), now);

	fx += s_carryX;
	fy += s_carryY;
	int dx = clampI8(fx), dy = clampI8(fy), wheel = 0;
	s_carryX = fx - dx;
	s_carryY = fy - dy;

	wheel = wheelStep(deskOk && lTouch && !drag, lTouch, in.lpx, in.lpy,
			  in.lpz, now);

	uint8_t mbtn = 0;
	if (s_own[LY_MOUSE] & TB_RPADC)
		mbtn |= MOUSE_BUTTON_LEFT;
	if (s_own[LY_MOUSE] & TB_LPADC)
		mbtn |= MOUSE_BUTTON_RIGHT;
	// stick clicks: middle (left thumb, while the right one points), back
	if (s_own[LY_MOUSE] & TB_L3)
		mbtn |= MOUSE_BUTTON_MIDDLE;
	if (s_own[LY_MOUSE] & TB_R3)
		mbtn |= MOUSE_BUTTON_BACKWARD;
	sendMouse(mbtn, dx, dy, wheel);
}

static void bumpBoth()
{
	for (int i = 0; i < 2; i++)
		hapticTone(TONE_SIDE[i], ELIZ_BUMP_DB, ELIZ_BUMP_DUR);
}

// A back 4 squeeze counts only when it is clean: all four back buttons going
// down within BACK_HOLD_MS of the first, starting from none held, in one
// layer. Anything messier (some held from before, a pad touched halfway,
// other buttons) is just those buttons.
#define BACK_HOLD_MS 150

// Back 4 (all four back buttons) as a gesture. A plain tap -- a clean
// squeeze, then all released with no other button pressed in between --
// asks the Guide to cycle its mode ("KG") when one holds the serial port,
// and otherwise shows or hides the guide drive. Firing on release keeps it
// apart from the mode-switch chords (back 4 + a face button or d-pad).
// Back 4 + Start asks the Guide to install itself ("KI"); Start is kept
// from the host while the back buttons are held.
static uint32_t back4Gesture(uint32_t raw, uint32_t now)
{
	static bool armed, spoiled, startWas, gathering;
	static uint32_t first;
	static uint8_t layer;
	uint32_t back = raw & CHORD_BACK4;
	bool all = back == CHORD_BACK4;
	if (!back) {
		gathering = false;
	} else if (!gathering && !armed) {
		gathering = true;
		first = now;
		layer = s_layer;
	}
	if (all && !armed) {
		armed = true;
		spoiled = !gathering || now - first > BACK_HOLD_MS;
	}
	if (armed && s_layer != layer)
		spoiled = true;
	bool start = all && (raw & TB_VIEW);
	if (start && !startWas && armed && !spoiled && Serial) {
		Serial.printf("KI\n");
		bumpBoth();
	}
	startWas = start;
	if (armed && (raw & ELIZ_BTN_MASK & ~CHORD_BACK4))
		spoiled = true;
	if (armed && !back) {
		armed = false;
		if (!spoiled) {
			if (Serial)
				Serial.printf("KG\n");
			else
				elizDriveToggle();
			bumpBoth();
		}
	}
	return all ? raw & ~TB_VIEW : raw;
}

// In the mouse and keyboard layers back 4 is Shift + Ctrl + Win + Alt, which
// Windows reads as the Office key (it opens the Microsoft 365 / Copilot
// app). So the first back button's modifier waits up to BACK_HOLD_MS for the
// other three; if all four arrive it is the back 4 gesture and none reach
// the host. Otherwise the held ones go out as soon as anything else happens
// -- another button (so Shift + key still arrive together), a held one let
// go (sent for that one report, so a quick tap still clicks), a layer
// change, or the time running out -- and they belong to the layer they were
// pressed in, like any held button. The gamepad layer gets no delay.
static uint32_t holdBack(uint32_t raw, uint32_t now)
{
	static bool holding, gesture;
	static uint32_t since, held, prev;
	static uint8_t layer;
	uint32_t back = raw & CHORD_BACK4, was = prev;
	prev = back;
	if (gesture) {
		if (!back)
			gesture = false;
		return raw & ~CHORD_BACK4;
	}
	if (!holding) {
		if (!back || was || s_layer == LY_PAD)
			return raw;
		holding = true;
		since = now;
		held = 0;
		layer = s_layer;
	}
	held |= back;
	if (back == CHORD_BACK4) {
		holding = false;
		gesture = true;
		return raw & ~CHORD_BACK4;
	}
	uint32_t letGo = held & ~back;
	bool other = raw & ELIZ_BTN_MASK & ~CHORD_BACK4;
	if (other || letGo || s_layer != layer ||
	    now - since >= BACK_HOLD_MS) {
		holding = false;
		// hand them to the layer they were pressed in, and mark them
		// seen so ownButtons() doesn't reassign them to the current one
		s_own[layer] |= held;
		s_prevBtn |= held;
		return raw | letGo;
	}
	return raw & ~CHORD_BACK4;
}

static void elizProcess(const PuckInput &in)
{
	uint32_t now = millis();
	uint32_t derived = deriveButtons(in);
	streamInput(in, derived, now);
	uint32_t raw = back4Gesture(derived, now);
	uint8_t touchLayer = layerFromTouch(in.buttons);
	layerStep(touchLayer, now);
	ownButtons(holdBack(raw, now) & ELIZ_BTN_MASK, touchLayer);
	gamepadStep(in);

	bool rTouch = in.buttons & TB_RPADT, lTouch = in.buttons & TB_LPADT;
	const uint8_t zone[2] = {
		padZone(s_pad[PAD_R], rTouch, in.rpx, in.rpy),
		padZone(s_pad[PAD_L], lTouch, in.lpx, in.lpy)
	};
	const bool wasOn[2] = { s_pad[PAD_R].on, s_pad[PAD_L].on };
	int rdx, rdy, ldx, ldy;
	padDelta(s_pad[PAD_R], rTouch, in.rpx, in.rpy, &rdx, &rdy);
	padDelta(s_pad[PAD_L], lTouch, in.lpx, in.lpy, &ldx, &ldy);
	const int travel[2] = { abs(rdx) + abs(rdy), abs(ldx) + abs(ldy) };

	padClicks(raw, zone);
	triggerPulses(raw);
	zoneHaptics(in, zone, wasOn, travel);
	streamState(zone);
	keyboardStep();
	mediaStep(in);
	mouseStep(in, touchLayer == LY_MOUSE && s_layer != LY_KBD, rdx, rdy,
		  ldx, ldy, now);
}

// ---- controller ----

void EnhancedLizardController::usbIdentity()
{
	// pid.codes test PID: fine for private use; a published build needs
	// its own allocation (https://pid.codes)
	USBDevice.setID(0x1209, 0x0001);
	USBDevice.setVersion(0x0200);
	USBDevice.setDeviceVersion(0x0100);
	USBDevice.setManufacturerDescriptor("LizardKing");
	USBDevice.setProductDescriptor("LizardKing");
	USBDevice.setConfigurationMaxPower(500);
	// CDC is described with an IAD, which needs the Misc/IAD device class
	tusb_desc_device_t *device =
		(tusb_desc_device_t *)tud_descriptor_device_cb();
	device->bDeviceClass = TUSB_CLASS_MISC;
	device->bDeviceSubClass = MISC_SUBCLASS_COMMON;
	device->bDeviceProtocol = MISC_PROTOCOL_IAD;
}

void EnhancedLizardController::begin()
{
	beginShared();
	usbIdentity();
	g_elizXi.begin();
	g_elizHid.setReportDescriptor(ELIZ_HID_DESC, sizeof ELIZ_HID_DESC);
	g_elizHid.setReportCallback(elizGetReport, elizSetReport);
	g_elizHid.setStringDescriptor("LizardKing Keyboard/Mouse");
	g_elizHid.setPollInterval(1);
	g_elizHid.begin();
	// Standard CDC: keeps the Arduino 1200-baud "touch" reset into the
	// bootloader (arduino-cli upload / make flash) and the serial console.
	Serial.setStringDescriptor("LizardKing Serial");
	TinyUSBDevice.addInterface(Serial);
	// last, so the CDC function keeps its interface number (and Windows
	// its COM port) whether or not the drive is built in
	elizDriveBegin();
}

void EnhancedLizardController::onReport45(int slot, const uint8_t *rep,
					  bool fresh, uint8_t bodyTlen)
{
	unsigned long now = millis();
	if (slot >= 0 && slot < NSLOT) {
		if ((s_slot < 0 || !bondAlive(s_slot, now)) && fresh &&
		    bondAlive(slot, now)) {
			if (s_slot >= 0)
				releaseAll();
			s_slot = slot;
		}
		if (slot == s_slot)
			elizProcess(g_in[slot]);
	}
	XboxController::onReport45(slot, rep, fresh, bodyTlen);
}

void EnhancedLizardController::emit(uint8_t slot, uint32_t generation,
				    const uint8_t *rep)
{
	(void)rep;
	if ((int)slot != s_slot || guideHolds()) {
		xinputSubmit(slot, generation, 0, 0, 0, 0, 0, 0, 0);
		return;
	}
	xinputSubmit(slot, generation, s_gp.btn, s_gp.lt, s_gp.rt, s_gp.lx,
		     s_gp.ly, s_gp.rx, s_gp.ry);
}

// Controller status for the Guide, about once a second while it holds the
// port: "KT connected battery% charge signal" (charge as in report 0x43:
// 1 discharging, 2 charging, 4 charged, 0 unknown; signal in -dBm, 0 = no
// sample yet).
static void streamStatus(uint32_t now)
{
	static uint32_t lastMs;
	if (now - lastMs < 1000 || !Serial || Serial.availableForWrite() < 32)
		return;
	lastMs = now;
	bool on = s_slot >= 0;
	int rssi = on && g_linkRssi[s_slot] > RSSI_DBM_OFFSET ?
			   g_linkRssi[s_slot] - RSSI_DBM_OFFSET :
			   0;
	unsigned batt = on ? g_battery[s_slot] : 0;
	unsigned charge = on ? g_batteryState[s_slot] : 0;
	Serial.printf("KT %d %u %u %d\n", on ? 1 : 0, batt, charge, rssi);
}

void EnhancedLizardController::task()
{
	XboxController::task();
	elizDriveTask();
	streamStatus(millis());
	if (s_slot >= 0 && !bondAlive(s_slot, millis())) {
		releaseAll();
		s_slot = -1;
	}
}
