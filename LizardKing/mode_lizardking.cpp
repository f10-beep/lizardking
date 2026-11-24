#include "mode_lizardking.h"
#include "guide_drive.h"
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

LizardKingController g_lizardKingCtl;
bool g_lkTrace;

// ---- tunables (see docs/DESIGN.md) ----

// a layer change must hold this long before buttons follow it
// The feel constants (static int / float, in caps) are variables, not
// #defines, so the console can set them live by name (~T, see lkTune) for
// A/B tests without a reflash. Every boot starts from these values.
static int LK_SWITCH_MS = 30;

// Pad clicks come from pressure: the controller's own click bit needs
// ~8900, which tires the thumb. ON/OFF give hysteresis like its own click.
static int LK_CLICK_ON = 3200;
static int LK_CLICK_OFF = 2300;
// With both thumbs on the pads (typing) clicks are lighter still: there is
// no cursor for a stray click to disturb, and typing wants a light touch.
static int LK_KEY_CLICK_ON = 2400;
static int LK_KEY_CLICK_OFF = 1700;

// pad counts per cursor pixel
static int LK_PX_DIV = 160;

// While touching, the cursor chases the thumb's absolute (1:1) position,
// closing a fraction of the gap each report: all of it at thumb speeds up
// to FOLLOW_SLOW px/report, easing to FOLLOW_MIN at FOLLOW_FAST. Fast swipes
// are smoothed, yet the cursor lands where 1:1 puts it once the thumb slows,
// so it never drifts. The speed is smoothed by SPEED_ALPHA and starts from
// the ball's speed when the thumb lands on a moving ball, so a tap's jitter
// cannot fling it.
static float LK_FOLLOW_SLOW = 1.0f;
static float LK_FOLLOW_FAST = 8.0f;
static float LK_FOLLOW_MIN = 0.3f;
static float LK_SPEED_ALPHA = 0.4f;
// The pad refreshes the thumb position only every 2nd-3rd report and repeats
// it in between, so the thumb speed is measured per new position: its travel
// over the time since the previous new position, in px per nominal report
// (REPORT_US). Repeats are skipped; none for STILL_US means the thumb is
// still. Taken per report, the repeats read as stops.
#define LK_REPORT_US 4000.0f
#define LK_STILL_US 16000u

// On lift the ball keeps the cursor's velocity, smoothed by LAUNCH_ALPHA so
// the twist at the end of a swipe does not decide the glide, and the rest
// of the gap is dropped. Touching a moving ball stops it.
static float LK_LAUNCH_ALPHA = 0.35f;

// Free-spinning ball: exponential friction plus a small constant drag, so
// slow spins end instead of crawling.
static float LK_GLIDE_DECAY = 0.9835f;
static float LK_GLIDE_DRAG = 0.006f;
static float LK_GLIDE_MAX = 40.0f;
// A touch that is not driving the cursor (keyboard layer) brakes the ball
// like a resting finger instead of stopping it dead.
static float LK_GHOST_DECAY = 0.88f;

// Pressure dead zone: the cursor ignores thumb travel within a radius that
// is DEADZONE_PX at zero pressure (a feather touch, about to lift off),
// shrinks to 0 by DZ_START, then grows back to DEADZONE_PX at DZ_FULL (below
// the click) and stays full for the whole click, so presses, releases and
// light lift-offs land where aimed. Beyond it the cursor is pulled along at
// the edge, so drags still work. A radius never makes the cursor creep while
// a click is held, which slowing the follow rate did.
static int LK_DZ_START = 400;
static int LK_DZ_FULL = 2200;
static float LK_DEADZONE_PX = 10.0f;
// Lift dead zone: a thumb rolling off drops pressure fast and its contact
// point wobbles. Each pad count per report of pressure drop opens LIFT_GAIN
// px of dead zone (up to LIFT_MAX), shrinking by LIFT_DECAY per report, so
// a thumb that presses back down is followed 1:1 again. The launch velocity
// updates more slowly the wider it is (half as fast at LIFT_HOLD_PX), so a
// flick ending in a lift keeps its launch and a settled lift has none,
// while pressure wobble barely matters.
static float LK_LIFT_GAIN = 0.1f;
static float LK_LIFT_MAX = 40.0f;
static float LK_LIFT_DECAY = 0.8f;
static float LK_LIFT_HOLD_PX = 2.0f;

// Circular scrolling on the left pad: one turn is SCROLL_REV notches,
// clockwise scrolls down. Starting a scroll needs the thumb to turn past a
// dead zone that shrinks from a half turn at radius SCROLL_R_IN (fraction of
// the pad) to 0 at SCROLL_R_OUT: near the center a tiny move swings the
// angle wildly, so a click there never starts one, while the outer ring
// starts at once. Once started it follows the thumb anywhere but the very
// center (SCROLL_R_HOLD) until the thumb lifts.
static float LK_SCROLL_REV = 24.0f;
static float LK_SCROLL_R_IN = 0.2f;
static float LK_SCROLL_R_OUT = 0.65f;
// Scroll momentum, like a free-spinning wheel, and built like the
// trackball's: while the thumb turns the wheel its speed is kept up to date
// (see wheelSpeed), and lifting the thumb at more than SCROLL_FLING notches
// per report lets the wheel keep turning, slowing by SCROLL_DECAY per report
// until under SCROLL_STOP. A thumb back on the left pad catches it; slow
// scrolling stops dead. A single reading more than SCROLL_OUTLIER times the
// current speed (a junk position at the pad's edge) is capped.
static float LK_SCROLL_OUTLIER = 3.0f;
// Wheel reports at most every WHEEL_GAP_MS: fast scrolling goes out as fewer,
// larger steps instead of a report every 4 ms, which some apps stutter on.
// Slow scrolling looks the same (it never needs more often than this).
#define LK_WHEEL_GAP_MS 16
// Once a scroll is under way the thumb may drift inward without stopping it;
// only this close to the center, where the angle is noise, does it pause.
static float LK_SCROLL_R_HOLD = 0.1f;
static float LK_SCROLL_FLING = 0.12f;
static float LK_SCROLL_DECAY = 0.996f;
static float LK_SCROLL_STOP = 0.008f;
// a scroll texture tick every SCROLL_TICK notches, fine to match the
// high-resolution wheel
static float LK_SCROLL_TICK = (2.0f / 3.0f);
// High-resolution wheel: once Windows sets the HID Resolution Multiplier,
// wheel reports count 1/120 notch.
#define LK_WHEEL_HIRES 120
// a per-report pad delta this large is a glitch, not a swipe
#define LK_MAX_DELTA 12000

// Clicks use Triton OUTPUT 0x81 HAPTIC_PULSE [side][on_us][off_us][repeat]
// for a sharp pulse; a 0x83 tone is dull and can swallow the next click.
// Pulse side 0 is the RIGHT pad.
static const uint8_t PULSE_SIDE[2] = { 0, 1 }; // PAD_R, PAD_L
static int LK_CLICK_ON_US = 1500;
static int LK_RELEASE_ON_US = 700;
// 0x81 has no gain, so key clicks are made stronger by doubling the on-time
static int LK_KEY_ON_US = 3000;
static int LK_KEY_RELEASE_ON_US = 1400;

// Ticks use Triton OUTPUT 0x83 HAPTIC_LFO_TONE
// [side][gain_db s8][freq u16][dur u16][lfo_freq u16][lfo_depth]: its gain
// sets strength, while 0x81 on-time only sets length. Tone side 0 is the
// LEFT pad, the reverse of 0x81. A 20-unit tick is clearly felt at -24 dB
// and barely at -36 dB.
static const uint8_t TONE_SIDE[2] = { 1, 0 }; // PAD_R, PAD_L
static int LK_TONE_HZ = 200;
static int LK_TICK_DB = -24;
static int LK_TICK_DUR = 20;
// entering / leaving the keyboard layer, and the right pad starting to aim:
// a tone on both pads, dull and round so it never reads as a click
static int LK_KBD_ON_DB = -12;
static int LK_KBD_OFF_DB = -18;
static int LK_KBD_DUR = 30;
// a tick per TICK_PX of cursor travel, so ticks slow down with the ball;
// scrolling ticks once per wheel notch
static float LK_TICK_PX = 24.0f;
#define LK_TICK_MIN_MS 12u

// Keyboard layer: each pad is a 3x3 grid of equal thirds. A zone is left
// only once the thumb is ZONE_HYST past its edge, so a thumb resting on a
// border does not flicker between zones. Side (N/E/S/W) zones have a texture
// tick per TEX_COUNTS of travel; corners and the center have none.
#define LK_ZONE_EDGE 10923
static int LK_ZONE_HYST = 1300;
static int LK_TEX_COUNTS = 2500;
static int LK_TEX_DB = -24;
// a bump on every zone change, and on entering the band along the pad's
// outer edge (the sensor can lose a thumb that is still on the pad)
static int LK_BUMP_DB = -12;
static int LK_BUMP_DUR = 20;
#define LK_PAD_EDGE 26000
// triggers act as keys in the keyboard layer at a light pull (0..255)
static int LK_TRIG_ON = 24;
static int LK_TRIG_OFF = 12;

// sticks / triggers beyond these count as "in use" for layer latching
#define LK_STICK_REST 8000
#define LK_TRIG_REST 16
// a bond silent this long has dropped; matches the Xbox mode's window
#define LK_CONN_MS 1200u

enum { LY_CTRL, LY_KBD, LY_N };

// Physical buttons that carry a meaning. Touch bits (pads, sticks, grips)
// select layers and are never bound.
#define LK_BTN_MASK                                                            \
	(TB_A | TB_B | TB_X | TB_Y | TB_QAM | TB_R3 | TB_VIEW | TB_R4 |        \
	 TB_R5 | TB_RB | TB_DDN | TB_DRT | TB_DLF | TB_DUP | TB_MENU | TB_L3 | \
	 TB_STEAM | TB_L4 | TB_L5 | TB_LB | TB_RPADC | TB_LPADC | TB_R2 |      \
	 TB_L2)
#define LK_PADCLICK (TB_RPADC | TB_LPADC)

// ---- USB: XInput interface 0 + one HID keyboard/mouse interface + CDC ----

enum {
	RID_KBD = 1,
	RID_MOUSE = 2,
	RID_WHEEL_MULT = 3,
	RID_MEDIA = 4,
	RID_SYSTEM = 5
};
// Mouse with the Resolution Multiplier feature in the wheel's logical
// collection (Microsoft's "enhanced wheel" layout): logical 0..1 maps to a
// physical multiplier of 1..120, so a host that sets it reads the wheel in
// 1/120 notches. Input report: buttons, x, y, wheel (4 bytes).
static const uint8_t LK_HID_DESC[] = {
	TUD_HID_REPORT_DESC_KEYBOARD(HID_REPORT_ID(RID_KBD)),
	0x05,
	0x01, // Usage Page (Generic Desktop)
	0x09,
	0x02, // Usage (Mouse)
	0xA1,
	0x01, // Collection (Application)
	0x85,
	RID_MOUSE, //   Report ID
	0x09,
	0x01, //   Usage (Pointer)
	0xA1,
	0x00, //   Collection (Physical)
	0x05,
	0x09, //     Usage Page (Button)
	0x19,
	0x01,
	0x29,
	0x05, //     Usage Min 1, Max 5
	0x15,
	0x00,
	0x25,
	0x01, //     Logical 0..1
	0x95,
	0x05,
	0x75,
	0x01,
	0x81,
	0x02, //     5 x 1 bit, Input
	0x95,
	0x01,
	0x75,
	0x03,
	0x81,
	0x03, //     3 bit pad
	0x05,
	0x01, //     Usage Page (Generic Desktop)
	0x09,
	0x30,
	0x09,
	0x31, //     Usage X, Y
	0x15,
	0x81,
	0x25,
	0x7F, //     Logical -127..127
	0x75,
	0x08,
	0x95,
	0x02,
	0x81,
	0x06, //     2 x 8 bit, Input Rel
	0xA1,
	0x02, //     Collection (Logical)
	0x85,
	RID_WHEEL_MULT, //       Report ID
	0x09,
	0x48, //       Usage (Resolution Multiplier)
	0x15,
	0x00,
	0x25,
	0x01, //       Logical 0..1
	0x35,
	0x01,
	0x45,
	LK_WHEEL_HIRES, //       Physical 1..120
	0x75,
	0x02,
	0x95,
	0x01,
	0xB1,
	0x02, //       2 bit, Feature
	0x75,
	0x06,
	0xB1,
	0x03, //       6 bit pad
	0x85,
	RID_MOUSE, //       Report ID
	0x09,
	0x38, //       Usage (Wheel)
	0x15,
	0x81,
	0x25,
	0x7F, //       Logical -127..127
	0x35,
	0x00,
	0x45,
	0x00, //       Physical 0
	0x75,
	0x08,
	0x95,
	0x01,
	0x81,
	0x06, //       8 bit, Input Rel
	0xC0, //     End Collection
	0xC0, //   End Collection
	0xC0, // End Collection
	// media keys: one 16-bit consumer usage, 0 = none
	TUD_HID_REPORT_DESC_CONSUMER(HID_REPORT_ID(RID_MEDIA)),
	// Sleep and Power: 1 power down, 2 sleep, 0 none. Windows does with
	// them what it does with the PC's own buttons.
	TUD_HID_REPORT_DESC_SYSTEM_CONTROL(HID_REPORT_ID(RID_SYSTEM)),
};
static Adafruit_USBD_HID g_lkHid;
// set by the host through RID_WHEEL_MULT (usbd task)
static volatile bool s_wheelHiRes;

static uint16_t lkGetReport(uint8_t id, hid_report_type_t type, uint8_t *buf,
			    uint16_t len)
{
	if (id != RID_WHEEL_MULT || type != HID_REPORT_TYPE_FEATURE || !len)
		return 0;
	buf[0] = s_wheelHiRes ? 1 : 0;
	return 1;
}

static void lkSetReport(uint8_t id, hid_report_type_t type, const uint8_t *buf,
			uint16_t len)
{
	if (id == RID_WHEEL_MULT && type == HID_REPORT_TYPE_FEATURE && len)
		s_wheelHiRes = buf[0] & 0x03;
}

// Interface 0 of the genuine pad only: the vendor 0x21 descriptor and the two
// interrupt endpoints. xi_open() checks these bytes, including EP 0x81/0x02.
#define LK_XI_LEN 40

class LkXInputItf : public Adafruit_USBD_Interface {
    public:
	uint16_t getInterfaceDescriptor(uint8_t itfnum, uint8_t *buf,
					uint16_t bufsize) override
	{
		(void)itfnum;
		if (!buf)
			return LK_XI_LEN;
		if (bufsize < LK_XI_LEN)
			return 0;
		uint8_t num = TinyUSBDevice.allocInterface(1);
		uint8_t in = TinyUSBDevice.allocEndpoint(TUSB_DIR_IN);
		// EP OUT 0x01 stays unused: the XInput body pins OUT to 0x02
		TinyUSBDevice.allocEndpoint(TUSB_DIR_OUT);
		uint8_t out = TinyUSBDevice.allocEndpoint(TUSB_DIR_OUT);
		if (num != 0 || in != 0x81 || out != 0x02)
			return 0;
		memcpy(buf, XINPUT_CONFIG_BODY, LK_XI_LEN);
		return LK_XI_LEN;
	}
	bool begin()
	{
		return TinyUSBDevice.addInterface(*this);
	}
};
static LkXInputItf g_lkXi;

// MS OS 1.0: Windows' Xbox driver (xusb22.inf) binds either the genuine
// 045E:028E device or the compatible ID USB\MS_COMP_XUSB10. Only the latter
// works for one interface of a composite device, so interface 0 advertises it
// through the 0xEE string + extended compat ID descriptor. Windows caches the
// "no MS OS descriptor" answer per VID/PID/bcdDevice, so bump bcdDevice if an
// earlier build ever enumerated without it.
#define LK_MS_VENDOR 0x20

const uint16_t *lkStringDescriptor(uint8_t index)
{
	static const uint16_t msos[] = { (TUSB_DESC_STRING << 8) | 18,
					 'M',
					 'S',
					 'F',
					 'T',
					 '1',
					 '0',
					 '0',
					 LK_MS_VENDOR };
	return index == 0xEE ? msos : nullptr;
}

bool lkVendorControl(uint8_t rhport, uint8_t stage,
		     const tusb_control_request_t *request)
{
	// extended compat ID descriptor, one function: interface 0 -> XUSB10
	static const uint8_t compat[40] = {
		40, 0, 0,   0,	 0x00, 0x01, 0x04, 0x00, // len, ver, index
		1,  0, 0,   0,	 0,    0,    0,	   0, // count, reserved
		0,  1, 'X', 'U', 'S',  'B',  '1',  '0', // itf 0, compat ID
		0,  0, 0,   0,	 0,    0,    0,	   0, // sub-compat ID
		0,  0, 0,   0,	 0,    0, // reserved
	};
	if (request->bRequest != LK_MS_VENDOR || request->wIndex != 4 ||
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
	uint32_t keyMs; // when the held key was typed
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
// key zone types nothing (dead) until the thumb returns to the center.
enum { KS_IDLE, KS_CENTER, KS_KEY, KS_DEAD };
enum { AN_LT, AN_RT, AN_LSTICK, AN_RSTICK, AN_N };
enum { AN_FREE, AN_OWNED, AN_BLOCKED };

static int s_slot = -1;
static uint8_t s_layer = LY_CTRL, s_cand = LY_CTRL;
static uint32_t s_candMs;
static uint32_t s_prevBtn;
static uint32_t s_own[LY_N];
static uint8_t s_an[AN_N];
static Pad s_pad[2];
static GamepadOut s_gp;
static uint8_t s_sentMBtn, s_sentMod, s_sentKeys[6];
static uint16_t s_sentMedia;
static uint8_t s_sentSystem;
static float s_carryX, s_carryY;
static float s_tickDist;
static uint32_t s_tickMs;
// the Guide button (see guideButton); the puck owns the key map switches
// and the Guide follows the "KT" line
// key map: 0 off, 1 large, 2 small
static uint8_t s_keymapSize = 1;
static bool s_keymapLow = true;
static uint32_t s_guideTapUntil, s_bumpAgainAt, s_statusMs;

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
bool g_lkScrollTrace;
bool g_lkGyroTrace;

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
	       g_connReplyMs[slot] && now - g_connReplyMs[slot] <= LK_CONN_MS;
}

// Both pads = keyboard; anything else is the controller, whose pads are the
// mouse. A click held into the keyboard stays held, so a drag can take a
// modifier midway.
static uint8_t layerFromTouch(uint32_t b)
{
	return (b & TB_LPADT) && (b & TB_RPADT) ? LY_KBD : LY_CTRL;
}

// ---- controller layer: the back buttons carry A/B/X/Y ----

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
		{ TB_DUP, XB_DUP },
		{ TB_DDN, XB_DDOWN },
		{ TB_DLF, XB_DLEFT },
		{ TB_DRT, XB_DRIGHT },
		// TB_VIEW is the physical Start side; see triton.h
		{ TB_VIEW, XB_START },
		{ TB_MENU, XB_BACK },
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

// ---- keyboard keys from buttons ----

struct LkKey {
	uint32_t tb;
	uint8_t layer;
	uint8_t mod;
	uint8_t key;
};
static const LkKey BUTTON_KEYS[] = {
	// back buttons: modifiers. A held one stays a modifier when a thumb
	// lifts to the pointer (Ctrl-click, Shift-drag). Shift over Ctrl on
	// the left as on a keyboard; Alt mirrors Ctrl.
	{ TB_L4, LY_KBD, KEYBOARD_MODIFIER_LEFTSHIFT, 0 },
	{ TB_L5, LY_KBD, KEYBOARD_MODIFIER_LEFTCTRL, 0 },
	{ TB_R4, LY_KBD, KEYBOARD_MODIFIER_LEFTGUI, 0 },
	{ TB_R5, LY_KBD, KEYBOARD_MODIFIER_LEFTALT, 0 },
	// index fingers: the keys typing needs between letters, reachable while
	// the thumbs are on the pads
	{ TB_R2, LY_KBD, 0, HID_KEY_SPACE },
	{ TB_L2, LY_KBD, 0, HID_KEY_BACKSPACE },
	{ TB_RB, LY_KBD, 0, HID_KEY_DELETE },
	{ TB_LB, LY_KBD, 0, HID_KEY_TAB },
	// face buttons, below the mouse's back and forward (see mouseStep):
	// confirm and cancel where every gamepad menu has them, so a press out
	// of habit does what it meant. Typing reaches Enter by lifting the
	// right thumb.
	{ TB_A, LY_CTRL, 0, HID_KEY_ENTER },
	{ TB_B, LY_CTRL, 0, HID_KEY_ESCAPE },
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
// Media keys are consumer usages, not keyboard ones, so the map holds them as
// codes past the keyboard page's last usage (0xE7) and MEDIA_USAGE gives the
// real usage.
enum { KM_VOL_DOWN = 0xF0, KM_VOL_UP, KM_PREV, KM_NEXT, KM_PLAY, KM_MUTE };
static const uint16_t MEDIA_USAGE[] = {
	HID_USAGE_CONSUMER_VOLUME_DECREMENT,
	HID_USAGE_CONSUMER_VOLUME_INCREMENT,
	HID_USAGE_CONSUMER_SCAN_PREVIOUS,
	HID_USAGE_CONSUMER_SCAN_NEXT,
	HID_USAGE_CONSUMER_PLAY_PAUSE,
	HID_USAGE_CONSUMER_MUTE,
};
// The Copilot key, which Windows defines as Win + Shift + F23: one key here,
// as on the keyboards that have it. The system's Sleep and Power are key map
// keys too.
enum { KM_SLEEP = 0xEB, KM_POWER, KM_COPILOT };
// Sleep and Power act only once held this long (the Guide's key map fills
// their cell meanwhile, and the pad bumps when they act): a stray click would
// end the session, and neither needs to be quick.
#define SYS_HOLD_MS 1000

static bool holdKey(uint8_t key)
{
	return key == KM_SLEEP || key == KM_POWER;
}
#define K(k) HID_KEY_##k
static const uint8_t KEYMAP[2][Z_COUNT][8] = {
	// PAD_R clicked; ring N NE E SE S SW W NW
	{
		// left thumb NW: F6..F10 where 6..0 are, F12 on top
		{ K(F12), 0, K(F10), K(F9), K(F8), K(F7), K(F6), 0 },
		// left thumb N: the number row. Its punctuation along the top,
		// ] - = as on a keyboard after 0, ] mirroring [; the digits
		// in a U below, 6 on the inner side round to 0 on the outer,
		// each a zone from the next (see the left pad's N page)
		{ K(MINUS), K(EQUAL), K(0), K(9), K(8), K(7), K(6),
		  K(BRACKET_RIGHT) },
		// left thumb NE: the numpad's 6..0 where 6..0 are. The top
		// rows run / * - + . Enter across both pads, as a numpad's
		// operators top it.
		{ K(KEYPAD_DECIMAL), K(KEYPAD_ENTER), K(KEYPAD_0), K(KEYPAD_9),
		  K(KEYPAD_8), K(KEYPAD_7), K(KEYPAD_6), K(KEYPAD_ADD) },
		// left thumb W: the rarer letters, placed by the layout search
		// with the home page fixed (docs/KEYBOARD_LAYOUT.md)
		{ K(D), K(W), K(T), K(P), K(M), K(J), K(V), K(Z) },
		// left thumb center
		{ K(N), K(C), K(S), K(F), K(R), K(B), K(L), K(G) },
		// left thumb E: free, and beside Power
		{},
		// left thumb SW: the arrows by direction, the page and line
		// ends in the corners as on a keyboard's nav cluster
		{ K(ARROW_UP), K(PAGE_UP), K(ARROW_RIGHT), K(PAGE_DOWN),
		  K(ARROW_DOWN), K(END), K(ARROW_LEFT), K(HOME) },
		// left thumb S: free, and beside Power
		{},
		// left thumb SE: keys that aren't F-keys or typing, on a corner
		// page the resting thumb finds blind. The top row is a
		// keyboard's Print Screen, Scroll Lock, Pause; the AI key on the
		// outer edge, mirroring Caps Lock. Power on SW (see the left
		// pad's SW page) with every key two zones from it, and the pages
		// beside this one leave SW empty, so reaching Power by mistake
		// takes more than one misstep.
		{ K(SCROLL_LOCK), K(PAUSE), KM_COPILOT, 0, 0, KM_POWER, 0,
		  K(PRINT_SCREEN) },
	},
	// PAD_L clicked
	{
		// right thumb NW: the numpad's 1..5 where 1..5 are, / * - on
		// top (see the right pad's NE page)
		{ K(KEYPAD_MULTIPLY), K(KEYPAD_SUBTRACT), K(KEYPAD_5),
		  K(KEYPAD_4), K(KEYPAD_3), K(KEYPAD_2), K(KEYPAD_1),
		  K(KEYPAD_DIVIDE) },
		// right thumb N: the number row. ` in the corner as on a
		// keyboard, [ across from it; 1 on the outer side round to 5 on
		// the inner, so 5 and 6 face each other across the pads
		{ 0, K(BRACKET_LEFT), K(5), K(4), K(3), K(2), K(1), K(GRAVE) },
		// right thumb NE: F1..F5 where 1..5 are, F11 on top
		{ K(F11), 0, K(F5), K(F4), K(F3), K(F2), K(F1), 0 },
		// right thumb W: free, and beside Sleep
		{},
		// right thumb center
		{ K(O), K(PERIOD), K(U), K(COMMA), K(I), K(A), K(E), K(Y) },
		// right thumb E: the rarer letters (see the right pad's W page),
		// and the rest of the punctuation in the zones they leave
		{ K(SEMICOLON), K(APOSTROPHE), K(Q), K(SLASH), K(K), K(X), K(H),
		  K(BACKSLASH) },
		// right thumb SW: the rest of the keys that aren't F-keys or
		// typing (see the right pad's SE page). Num Lock mirrors Scroll
		// Lock, Insert Print Screen; Caps Lock on the outer edge as on a
		// keyboard. Sleep on SE: thumbs drawn in and down, each zone is
		// the other's page, as with T and H; every key is two zones from
		// it, and the pages beside this one leave SE empty.
		{ K(NUM_LOCK), K(INSERT), 0, KM_SLEEP, 0, 0, K(CAPS_LOCK), 0 },
		// right thumb S: free, and beside Sleep
		{},
		// right thumb SE: play/pause top center; volume along the
		// bottom, mute in the corner
		{ KM_PLAY, 0, KM_NEXT, KM_VOL_UP, KM_VOL_DOWN, KM_MUTE, KM_PREV,
		  0 },
	},
};
#undef K
// the live key map: KEYMAP until the console changes a page (~K)
static uint8_t s_keymap[2][Z_COUNT][8];

// one axis of the grid: 0 = low third, 1 = middle, 2 = high
static uint8_t zoneBand(int v, uint8_t cur)
{
	int lo = -LK_ZONE_EDGE, hi = LK_ZONE_EDGE;
	if (cur == 0) {
		lo += LK_ZONE_HYST;
	} else if (cur == 2) {
		hi -= LK_ZONE_HYST;
	} else {
		lo -= LK_ZONE_HYST;
		hi += LK_ZONE_HYST;
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
	return x > LK_STICK_REST || x < -LK_STICK_REST || y > LK_STICK_REST ||
	       y < -LK_STICK_REST;
}

// Analog inputs follow the same latching rule as buttons: one in use when the
// layer changes keeps its old meaning (gamepad, or off) until it rests.
static bool analogPasses(uint8_t ch, bool active)
{
	if (!active)
		s_an[ch] = AN_FREE;
	else if (s_an[ch] == AN_FREE)
		s_an[ch] = s_layer == LY_CTRL ? AN_OWNED : AN_BLOCKED;
	if (s_layer == LY_CTRL)
		return s_an[ch] != AN_BLOCKED;
	return s_an[ch] == AN_OWNED;
}

// Pad travel since last report, in pad counts; 0 on the first touched report.
static void padDelta(Pad &p, bool touch, int16_t x, int16_t y, int *dx, int *dy)
{
	*dx = *dy = 0;
	if (touch && p.on) {
		int ddx = x - p.x, ddy = y - p.y;
		if (ddx <= LK_MAX_DELTA && ddx >= -LK_MAX_DELTA &&
		    ddy <= LK_MAX_DELTA && ddy >= -LK_MAX_DELTA) {
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
	if (speed <= LK_FOLLOW_SLOW)
		return 1.0f;
	if (speed >= LK_FOLLOW_FAST)
		return LK_FOLLOW_MIN;
	float t = (speed - LK_FOLLOW_SLOW) / (LK_FOLLOW_FAST - LK_FOLLOW_SLOW);
	t = t * t * (3.0f - 2.0f * t);
	return 1.0f + (LK_FOLLOW_MIN - 1.0f) * t;
}

// see LK_DEADZONE_PX: full at 0 and from DZ_FULL up, 0 at DZ_START
static float deadZone(int16_t p, bool click)
{
	if (click || p >= LK_DZ_FULL)
		return LK_DEADZONE_PX;
	float t;
	if (p < LK_DZ_START) {
		t = 1.0f - (float)(p > 0 ? p : 0) / (float)LK_DZ_START;
		return LK_DEADZONE_PX * t * t;
	}
	t = (float)(p - LK_DZ_START) / (float)(LK_DZ_FULL - LK_DZ_START);
	return LK_DEADZONE_PX * (1.0f - (1.0f - t) * (1.0f - t));
}

// Update s_ball.speed from the thumb's travel this report (see
// LK_REPORT_US).
static void thumbSpeed(float tx, float ty)
{
	uint32_t now = micros();
	uint32_t dt = now - s_ball.sampleUs;
	float v;
	if (tx || ty) {
		s_ball.sampleUs = now;
		float reports = dt / LK_REPORT_US;
		v = sqrtf(tx * tx + ty * ty) / (reports > 1 ? reports : 1);
	} else if (dt > LK_STILL_US) {
		v = 0;
	} else {
		return;
	}
	s_ball.speed += (v - s_ball.speed) * LK_SPEED_ALPHA;
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
		float lr = drop > 0 ? drop * LK_LIFT_GAIN : 0.0f;
		s_ball.liftR *= LK_LIFT_DECAY;
		if (lr > s_ball.liftR)
			s_ball.liftR = lr < LK_LIFT_MAX ? lr : LK_LIFT_MAX;
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
		float a = LK_LAUNCH_ALPHA * LK_LIFT_HOLD_PX /
			  (LK_LIFT_HOLD_PX + s_ball.liftR);
		s_ball.vx += (*fx - s_ball.vx) * a;
		s_ball.vy += (*fy - s_ball.vy) * a;
		return;
	}
	s_ball.held = false;
	if (touch) {
		s_ball.vx *= LK_GHOST_DECAY;
		s_ball.vy *= LK_GHOST_DECAY;
	}
	float sp = sqrtf(s_ball.vx * s_ball.vx + s_ball.vy * s_ball.vy);
	float nsp = sp > LK_GLIDE_MAX ? LK_GLIDE_MAX :
					sp * LK_GLIDE_DECAY - LK_GLIDE_DRAG;
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
// host gets none, only "nothing pressed", cursor included (the Guide parks
// the cursor out of its way). The Guide still sees
// everything through its serial feed. The beat comes from the Guide's
// window thread, so a Guide that hangs, crashes or closes the port hands the
// controller back within GUIDE_BEAT_MS.
#define GUIDE_BEAT_MS 1000
static uint32_t s_guideBeat;

void lkGuideBeat()
{
	s_guideBeat = millis() | 1;
}

void lkGuideRelease()
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
	uint8_t rep[8] = { mod,	    0,	     keys[0], keys[1],
			   keys[2], keys[3], keys[4], keys[5] };
	usbTxHid(&g_lkHid, RID_KBD, rep, sizeof rep);
}

static void sendMouse(uint8_t btn, int dx, int dy, int wheel)
{
	if (guideHolds())
		btn = dx = dy = wheel = 0;
	if (!dx && !dy && !wheel && btn == s_sentMBtn)
		return;
	s_sentMBtn = btn;
	int8_t m[4] = { (int8_t)btn, (int8_t)dx, (int8_t)dy, (int8_t)wheel };
	usbTxHid(&g_lkHid, RID_MOUSE, m, sizeof m);
}

static void sendMedia(uint16_t usage)
{
	if (guideHolds())
		usage = 0;
	if (usage == s_sentMedia)
		return;
	s_sentMedia = usage;
	usbTxHid(&g_lkHid, RID_MEDIA, &usage, sizeof usage);
}

static void sendSystem(uint8_t usage)
{
	if (guideHolds())
		usage = 0;
	if (usage == s_sentSystem)
		return;
	s_sentSystem = usage;
	usbTxHid(&g_lkHid, RID_SYSTEM, &usage, sizeof usage);
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
	hapticPulse(side, down ? LK_CLICK_ON_US : LK_RELEASE_ON_US);
}

static void hapticKey(uint8_t side, bool down)
{
	hapticPulse(side, down ? LK_KEY_ON_US : LK_KEY_RELEASE_ON_US);
}

static void hapticTone(uint8_t side, int8_t gainDb, uint16_t dur)
{
	uint8_t p[9] = { side,
			 (uint8_t)gainDb,
			 (uint8_t)LK_TONE_HZ,
			 (uint8_t)(LK_TONE_HZ >> 8),
			 (uint8_t)dur,
			 (uint8_t)(dur >> 8),
			 0,
			 0,
			 0 };
	haptic(0x83, p, sizeof p);
}

static void hapticTick(uint8_t side)
{
	hapticTone(side, LK_TICK_DB, LK_TICK_DUR);
}

// one faint tick per LK_TICK_PX of cursor travel, touching or gliding
static void moveTick(float px, uint32_t now)
{
	s_tickDist += px;
	if (s_tickDist < LK_TICK_PX || now - s_tickMs < LK_TICK_MIN_MS)
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
	s_sentSystem = 0xFF;
	sendSystem(0);
	memset(s_own, 0, sizeof s_own);
	memset(s_an, 0, sizeof s_an);
	memset(s_pad, 0, sizeof s_pad);
	memset(&s_gp, 0, sizeof s_gp);
	memset(&s_ball, 0, sizeof s_ball);
	s_prevBtn = 0;
	s_layer = s_cand = LY_CTRL;
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
	int16_t on = rt && lt ? LK_KEY_CLICK_ON : LK_CLICK_ON;
	int16_t off = rt && lt ? LK_KEY_CLICK_OFF : LK_CLICK_OFF;
	r = rt && (r ? in.rpz > off : in.rpz >= on);
	l = lt && (l ? in.lpz > off : in.lpz >= on);
	rt2 = rt2 ? in.rt > LK_TRIG_OFF : in.rt >= LK_TRIG_ON;
	lt2 = lt2 ? in.lt > LK_TRIG_OFF : in.lt >= LK_TRIG_ON;
	uint32_t b = in.buttons & ~(LK_PADCLICK | TB_R2 | TB_L2);
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
	if (now - s_candMs < LK_SWITCH_MS)
		return;
	bool on = touchLayer == LY_KBD, off = s_layer == LY_KBD;
	s_layer = touchLayer;
	if (on || off) {
		int8_t db = on ? LK_KBD_ON_DB : LK_KBD_OFF_DB;
		for (int i = 0; i < 2; i++)
			hapticTone(TONE_SIDE[i], db, LK_KBD_DUR);
	}
}

// New presses join the current layer and keep it until released. Pad clicks
// use the undebounced layer: the thumb is down by definition, and a quick
// tap would otherwise land in the controller layer.
static void ownButtons(uint32_t b, uint8_t touchLayer)
{
	uint32_t pressed = b & ~s_prevBtn;
	uint32_t released = s_prevBtn & ~b;
	s_prevBtn = b;
	for (int l = 0; l < LY_N; l++)
		s_own[l] &= ~released;
	s_own[s_layer] |= pressed & ~LK_PADCLICK;
	s_own[touchLayer] |= pressed & LK_PADCLICK;
}

static void gamepadStep(const PuckInput &in)
{
	s_gp.btn = gamepadButtons(s_own[LY_CTRL]);
	if (s_guideTapUntil && (int32_t)(s_guideTapUntil - millis()) > 0)
		s_gp.btn |= XB_GUIDE;
	else
		s_guideTapUntil = 0;
	bool lt = analogPasses(AN_LT, in.lt > LK_TRIG_REST);
	bool rt = analogPasses(AN_RT, in.rt > LK_TRIG_REST);
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
	s_pad[i].keyMs = millis();
	hapticKey(PULSE_SIDE[i], true);
}

// A key zone with no key for where the other thumb rests is inert like the
// center: no pulse, so the thumb feels that nothing is there. It stays dead
// until the thumb returns to the center.
static void pressZone(int i, uint8_t key, uint8_t z)
{
	if (key)
		typeKey(i, key, z);
	else
		s_pad[i].kst = KS_DEAD;
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
		uint8_t key = pos >= 0 ? s_keymap[i][zone[1 - i]][pos] : 0;
		if (click != pd.click) {
			pd.click = click;
			if (!click) {
				if (pd.kst == KS_KEY)
					hapticKey(PULSE_SIDE[i], false);
				else if (pd.kst == KS_IDLE)
					hapticClick(PULSE_SIDE[i], false);
				pd.kst = KS_IDLE;
				pd.key = 0;
			} else if (!(s_own[LY_KBD] & CLICK_BIT[i])) {
				hapticClick(PULSE_SIDE[i], true);
			} else if (pos < 0) {
				pd.kst = KS_CENTER;
			} else {
				pressZone(i, key, z);
			}
			continue;
		}
		if (!click)
			continue;
		if (pd.kst == KS_CENTER && pos >= 0) {
			pressZone(i, key, z);
		} else if (pd.kst == KS_KEY && z != pd.keyZone) {
			hapticKey(PULSE_SIDE[i], false);
			pd.key = 0;
			pd.kst = pos < 0 ? KS_CENTER : KS_DEAD;
		} else if (pd.kst == KS_DEAD && pos < 0) {
			pd.kst = KS_CENTER;
		}
	}
}

// Sleep and Power are a deliberate hold, so unlike other held keys anything
// else cancels them, as sliding off their zone does: another button, the other
// pad's click, the other thumb changing page or lifting.
static void holdGuard(uint32_t raw, const uint8_t zone[2])
{
	for (int i = 0; i < 2; i++) {
		Pad &pd = s_pad[i];
		int8_t pos = RING_POS[zone[i]];
		if (!holdKey(pd.key) ||
		    (pos >= 0 && s_keymap[i][zone[1 - i]][pos] == pd.key &&
		     !(raw & LK_BTN_MASK & ~CLICK_BIT[i])))
			continue;
		hapticKey(PULSE_SIDE[i], false);
		pd.key = 0;
		pd.kst = KS_DEAD;
	}
}

// Trigger keys have no physical click at a light pull: pulse the pad on that
// side on press and release, in the keyboard layer.
static void triggerPulses(uint32_t raw)
{
	static bool down[2], keyed[2];
	for (int i = 0; i < 2; i++) {
		bool d = raw & TRIG_BIT[i];
		if (d == down[i])
			continue;
		down[i] = d;
		if (d)
			keyed[i] = s_layer != LY_CTRL;
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
		bool edge = pd.edge ? reach > LK_PAD_EDGE - LK_ZONE_HYST :
				      reach > LK_PAD_EDGE;
		bool bump = kbd && wasOn[i] &&
			    (zone[i] != pd.zone || (edge && !pd.edge));
		pd.zone = zone[i];
		pd.edge = pd.on && edge;
		if (bump)
			hapticTone(TONE_SIDE[i], LK_BUMP_DB, LK_BUMP_DUR);
		if (!kbd || !zoneSide(zone[i])) {
			pd.tex = 0;
			continue;
		}
		pd.tex += travel[i];
		if (pd.tex >= LK_TEX_COUNTS) {
			pd.tex = 0;
			hapticTone(TONE_SIDE[i], LK_TEX_DB, LK_TICK_DUR);
		}
	}
}

// Guide feed (tools/guide): a "KS layer zoneR zoneL
// shift hold" line whenever one of them changes, only while a host holds the serial
// port open, and skipped (retried next report) rather than blocking when the
// CDC buffer is full.
static uint8_t s_ksLast[5] = { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF };

static void streamState(const uint8_t zone[2])
{
	// the Shift held, whether or not it reaches the host (the Guide holds
	// the host's input while it's up, and its key map still shifts)
	bool shift = s_own[LY_KBD] & TB_L4;
	// a bit per pad holding Sleep or Power, for the key map's fill
	uint8_t hold = 0;
	for (int i = 0; i < 2; i++)
		if (holdKey(s_pad[i].key))
			hold |= 1 << i;
	const uint8_t cur[5] = { s_layer, zone[PAD_R], zone[PAD_L], shift,
				 hold };
	if (!memcmp(cur, s_ksLast, sizeof cur) || !Serial ||
	    Serial.availableForWrite() < 24)
		return;
	memcpy(s_ksLast, cur, sizeof cur);
	Serial.printf("KS %u %u %u %u %u\n", cur[0], cur[1], cur[2], cur[3],
		      cur[4]);
}

// Live input for the Guide's controller drawing: "KB buttons lt rt lx ly rx
// ry lpx lpy rpx rpy own0 own1 own2" (buttons in hex as the Guide sees them,
// with derived pad clicks and trigger bits; triggers 0..31; sticks and pad
// positions -32..32, pads 0 when untouched; ownN in hex, the held buttons
// that belong to layer N, so a button held across a layer change stays lit
// in the panel it acts in). Coarse steps keep stick and pad jitter from
// flooding the port; sent only on a change, at most every 8 ms, and dropped
// rather than waited for when the CDC buffer is full.
static void streamInput(const PuckInput &in, uint32_t b, uint32_t now)
{
	static int16_t last[10];
	static uint32_t lastB = 0xFFFFFFFF, lastOwn[LY_N], lastMs;
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
	if (b == lastB && !memcmp(cur, last, sizeof cur) &&
	    !memcmp(s_own, lastOwn, sizeof lastOwn))
		return;
	if (now - lastMs < 8 || !Serial || Serial.availableForWrite() < 100)
		return;
	lastB = b;
	lastMs = now;
	memcpy(last, cur, sizeof cur);
	memcpy(lastOwn, s_own, sizeof lastOwn);
	Serial.printf("KB %lx %d %d %d %d %d %d %d %d %d %d %lx %lx\n",
		      (unsigned long)b, cur[0], cur[1], cur[2], cur[3], cur[4],
		      cur[5], cur[6], cur[7], cur[8], cur[9],
		      (unsigned long)s_own[LY_CTRL],
		      (unsigned long)s_own[LY_KBD]);
}

void lkDumpKeymap()
{
	for (int pad = 0; pad < 2; pad++) {
		for (int z = 0; z < Z_COUNT; z++) {
			Serial.printf("KM %d %d", pad, z);
			for (int k = 0; k < 8; k++)
				Serial.printf(" %02X", s_keymap[pad][z][k]);
			Serial.printf("\n");
		}
	}
	Serial.printf("KM end\n");
	// a freshly started Guide also needs the current state
	memset(s_ksLast, 0xFF, sizeof s_ksLast);
}

static void mediaStep()
{
	uint16_t usage = 0;
	uint8_t sys = 0;
	for (int i = 0; i < 2; i++) {
		const Pad &pd = s_pad[i];
		if (pd.key >= KM_VOL_DOWN)
			usage = MEDIA_USAGE[pd.key - KM_VOL_DOWN];
		if (!holdKey(pd.key) || millis() - pd.keyMs < SYS_HOLD_MS)
			continue;
		sys = pd.key == KM_POWER ? 1 : 2;
		if (!s_sentSystem && !guideHolds())
			hapticTone(TONE_SIDE[i], LK_BUMP_DB, LK_BUMP_DUR);
	}
	sendMedia(usage);
	sendSystem(sys);
}

static void keyboardStep()
{
	uint8_t mod = 0, keys[6] = {}, nk = 0;
	for (const auto &k : BUTTON_KEYS) {
		if (!(s_own[k.layer] & k.tb))
			continue;
		mod |= k.mod;
		if (k.key && nk < 6)
			keys[nk++] = k.key;
	}
	for (const auto &pd : s_pad) {
		uint8_t key = pd.key;
		if (key == KM_COPILOT) {
			mod |= KEYBOARD_MODIFIER_LEFTGUI |
			       KEYBOARD_MODIFIER_LEFTSHIFT;
			key = HID_KEY_F23;
		}
		if (key && key <= HID_KEY_GUI_RIGHT && nk < 6)
			keys[nk++] = key;
	}
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
// LK_SCROLL_REV).
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
		if (r < LK_SCROLL_R_HOLD) {
			s_scroll.anchor = a;
			return 0;
		}
	} else {
		// The dead zone only decides whether a scroll starts, so a
		// click near the center never turns into one. Until then the
		// wheel follows the thumb, so the scroll starts where it enters
		// the outer ring instead of replaying the way out from the
		// center.
		float t = (r - LK_SCROLL_R_IN) /
			  (LK_SCROLL_R_OUT - LK_SCROLL_R_IN);
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
	return d * LK_SCROLL_REV / (2.0f * (float)M_PI);
}

// Wheel report value this report: circular scroll with a texture tick every
// LK_SCROLL_TICK notches, in 1/120 notches once the host enabled them.
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
	float lr = drop > 0 ? drop * LK_LIFT_GAIN : 0.0f;
	s_scroll.liftR *= LK_LIFT_DECAY;
	if (lr > s_scroll.liftR)
		s_scroll.liftR = lr < LK_LIFT_MAX ? lr : LK_LIFT_MAX;
	uint32_t dt = now - s_scroll.sampleUs;
	float v;
	if (n != 0) {
		s_scroll.sampleUs = now;
		float reports = dt / LK_REPORT_US;
		v = n / (reports > 1 ? reports : 1);
		float cap = fabsf(s_scroll.vel) * LK_SCROLL_OUTLIER +
			    LK_SCROLL_FLING;
		v = v > cap ? cap : v < -cap ? -cap : v;
	} else if (dt > LK_STILL_US) {
		v = 0;
	} else {
		return;
	}
	float a = LK_LAUNCH_ALPHA * LK_LIFT_HOLD_PX /
		  (LK_LIFT_HOLD_PX + s_scroll.liftR);
	s_scroll.vel += (v - s_scroll.vel) * a;
}

static int wheelStep(bool active, bool lTouch, int16_t x, int16_t y, int16_t p,
		     uint32_t now)
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
		bool fling = fabsf(s_scroll.vel) >= LK_SCROLL_FLING;
		if (s_scroll.wasActive && fling)
			s_scroll.glide = s_scroll.vel;
		if (lTouch || fabsf(s_scroll.glide) < LK_SCROLL_STOP)
			s_scroll.glide = 0;
		n = s_scroll.glide;
		s_scroll.glide *= LK_SCROLL_DECAY;
	}
	if (g_lkScrollTrace && (active || s_scroll.wasActive || n != 0) &&
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
	if (active && s_scroll.tick >= LK_SCROLL_TICK) {
		s_scroll.tick -= LK_SCROLL_TICK;
		hapticTick(TONE_SIDE[PAD_L]);
	}
	float per = s_wheelHiRes ? LK_WHEEL_HIRES : 1;
	float pending = s_scroll.notches * per;
	// a stopped wheel sends what is left at once, and a step too big for
	// one report goes early, so batching never makes the wheel lag
	bool due = stopped || fabsf(pending) >= 127 ||
		   now - s_scroll.sentMs >= LK_WHEEL_GAP_MS;
	if (!due)
		return 0;
	int wheel = clampI8(pending);
	if (wheel)
		s_scroll.sentMs = now;
	if (g_lkScrollTrace && wheel && Serial.availableForWrite() > 24)
		Serial.printf("WS %d hires=%d\n", wheel, s_wheelHiRes ? 1 : 0);
	s_scroll.notches -= wheel / per;
	if (stopped && !wheel)
		s_scroll.notches = 0;
	return wheel;
}

// ---- gyro aiming ----

// raw gyro counts per degree per second (the +-2000 deg/s range)
#define LK_GYRO_PER_DPS 16.4f
// Cursor counts per degree the controller turns, rising with speed: slow,
// careful aim gets the low end for precision, a fast swing the high end so a
// big turn stays short (JoyShockMapper's acceleration).
static float LK_GYRO_SENS_SLOW = 8.0f;
static float LK_GYRO_SENS_FAST = 20.0f;
static float LK_GYRO_SLOW_DPS = 15.0f;
static float LK_GYRO_FAST_DPS = 75.0f;
// Soft smoothing: below this speed the input blends into the average of the
// last LK_GYRO_SMOOTH_N reports, fading out as speed rises, which steadies
// hand tremor without lagging deliberate movement.
static float LK_GYRO_SMOOTH_DPS = 5.0f;
#define LK_GYRO_SMOOTH_N 8
// Player space (as in JoyShockMapper): turning is measured about gravity, so
// it feels the same however the controller is tilted in the hands; a turn
// leaning into roll counts up to this much more, capped at the full rate.
static float LK_GYRO_YAW_RELAX = 1.41f;
// a sample this close to the bias, while not aiming, is the controller at
// rest, and the bias follows it (the gyro drifts a few counts)
static float LK_GYRO_STILL = 60.0f;
static float LK_GYRO_BIAS_RATE = 0.004f;
// gravity, low-passed from the accelerometer, per report
#define LK_GYRO_GRAVITY_RATE 0.05f
// Signs: turning right moves the cursor right, tilting the top up moves it
// up (controller axes: x right, y toward the triggers, z out of the face).
static float LK_GYRO_YAW_SIGN = -1.0f;
static float LK_GYRO_PITCH_SIGN = -1.0f;

static struct {
	float bx, by, bz;
	float ay, az;
	uint32_t lastUs;
	// recent yaw/pitch rates for smoothing, deg/s
	float histYaw[LK_GYRO_SMOOTH_N], histPitch[LK_GYRO_SMOOTH_N];
	uint8_t histAt;
	bool aimed;
	// gyro travel the cursor hasn't taken yet: the dead zone, as the
	// trackball's gap
	float gapX, gapY;
} s_gyro;

// Pointer movement from the gyro this report. The right pad is the mouse, and
// gyro is a second input to it while the thumb is on the pad, as on the
// original Steam Controller: lifting the thumb pauses it, which is how the
// aim ratchets.
// It shares the trackball's pressure and lift dead zones, radius r: pressing,
// clicking and rolling off the pad tilt the controller too. It never feeds
// the trackball's momentum, so the ratchet stops it dead.
static void gyroStep(const PuckInput &in, bool aiming, float r, float *dx,
		     float *dy)
{
	*dx = *dy = 0;
	s_gyro.ay += LK_GYRO_GRAVITY_RATE * (in.ay - s_gyro.ay);
	s_gyro.az += LK_GYRO_GRAVITY_RATE * (in.az - s_gyro.az);
	float wx = in.gx - s_gyro.bx, wy = in.gy - s_gyro.by,
	      wz = in.gz - s_gyro.bz;
	uint32_t dtUs = in.imuTimestampUs - s_gyro.lastUs;
	bool fresh = dtUs != 0;
	s_gyro.lastUs = in.imuTimestampUs;
	// each touch starts with a clean smoothing history and dead zone
	if (aiming && !s_gyro.aimed) {
		memset(s_gyro.histYaw, 0, sizeof s_gyro.histYaw);
		memset(s_gyro.histPitch, 0, sizeof s_gyro.histPitch);
		s_gyro.gapX = s_gyro.gapY = 0;
	}
	s_gyro.aimed = aiming;
	if (!aiming) {
		if (fresh && fabsf(wx) < LK_GYRO_STILL &&
		    fabsf(wy) < LK_GYRO_STILL && fabsf(wz) < LK_GYRO_STILL) {
			s_gyro.bx += LK_GYRO_BIAS_RATE * wx;
			s_gyro.by += LK_GYRO_BIAS_RATE * wy;
			s_gyro.bz += LK_GYRO_BIAS_RATE * wz;
		}
		return;
	}
	// a gap (a lost report, a reconnect) is not a turn
	if (!fresh || dtUs > 20000)
		return;
	float g = sqrtf(s_gyro.ay * s_gyro.ay + s_gyro.az * s_gyro.az);
	if (g < 1.0f)
		return;
	float worldYaw = (wy * s_gyro.ay + wz * s_gyro.az) / g;
	float yaw = fminf(fabsf(worldYaw) * LK_GYRO_YAW_RELAX,
			  sqrtf(wy * wy + wz * wz));
	yaw = copysignf(yaw, worldYaw) / LK_GYRO_PER_DPS;
	float pitch = wx / LK_GYRO_PER_DPS;

	float speed = sqrtf(yaw * yaw + pitch * pitch);
	uint8_t at = s_gyro.histAt;
	s_gyro.histAt = (uint8_t)((at + 1) % LK_GYRO_SMOOTH_N);
	s_gyro.histYaw[at] = yaw;
	s_gyro.histPitch[at] = pitch;
	float avgYaw = 0, avgPitch = 0;
	for (int i = 0; i < LK_GYRO_SMOOTH_N; i++) {
		avgYaw += s_gyro.histYaw[i];
		avgPitch += s_gyro.histPitch[i];
	}
	avgYaw /= LK_GYRO_SMOOTH_N;
	avgPitch /= LK_GYRO_SMOOTH_N;
	float direct = constrain((speed - LK_GYRO_SMOOTH_DPS / 2) /
					 (LK_GYRO_SMOOTH_DPS / 2),
				 0.0f, 1.0f);
	yaw = direct * yaw + (1 - direct) * avgYaw;
	pitch = direct * pitch + (1 - direct) * avgPitch;

	float t = constrain((speed - LK_GYRO_SLOW_DPS) /
				    (LK_GYRO_FAST_DPS - LK_GYRO_SLOW_DPS),
			    0.0f, 1.0f);
	float sens =
		LK_GYRO_SENS_SLOW + t * (LK_GYRO_SENS_FAST - LK_GYRO_SENS_SLOW);
	float k = dtUs * 1e-6f * sens;
	s_gyro.gapX += LK_GYRO_YAW_SIGN * yaw * k;
	s_gyro.gapY += LK_GYRO_PITCH_SIGN * pitch * k;
	float gap =
		sqrtf(s_gyro.gapX * s_gyro.gapX + s_gyro.gapY * s_gyro.gapY);
	if (gap <= r)
		return;
	float out = (gap - r) / gap;
	*dx = s_gyro.gapX * out;
	*dy = s_gyro.gapY * out;
	s_gyro.gapX -= *dx;
	s_gyro.gapY -= *dy;
}

// Outside the keyboard the pads are the mouse: right pad = trackball pointer,
// left pad = circular wheel.
static void mouseStep(const PuckInput &in, uint8_t touchLayer, int rdx, int rdy,
		      uint32_t now)
{
	bool rTouch = in.buttons & TB_RPADT, lTouch = in.buttons & TB_LPADT;
	bool mouse = touchLayer == LY_CTRL && s_layer == LY_CTRL;
	float tx = (float)rdx / LK_PX_DIV, ty = (float)-rdy / LK_PX_DIV;
	float fx, fy;
	ballStep(mouse && rTouch, rTouch, tx, ty, in.rpz, s_pad[PAD_R].click,
		 &fx, &fy);
	if (g_lkTrace && Serial.availableForWrite() > 60)
		Serial.printf("R %lu %d %d %.1f %.1f %.1f %.1f %.1f %.1f %.1f "
			      "%.1f\n",
			      (unsigned long)now, rTouch ? 1 : 0, in.rpz, tx,
			      ty, fx, fy, s_ball.vx, s_ball.vy, s_ball.liftR,
			      s_ball.speed);
	moveTick(sqrtf(fx * fx + fy * fy), now);

	float gx, gy;
	float r = fmaxf(deadZone(in.rpz, s_pad[PAD_R].click), s_ball.liftR);
	gyroStep(in, mouse && rTouch, r, &gx, &gy);
	fx += gx + s_carryX;
	fy += gy + s_carryY;
	int dx = clampI8(fx), dy = clampI8(fy), wheel = 0;
	s_carryX = fx - dx;
	s_carryY = fy - dy;

	wheel = wheelStep(mouse && lTouch, lTouch, in.lpx, in.lpy, in.lpz, now);

	uint8_t mbtn = 0;
	if (s_own[LY_CTRL] & TB_RPADC)
		mbtn |= MOUSE_BUTTON_LEFT;
	if (s_own[LY_CTRL] & TB_LPADC)
		mbtn |= MOUSE_BUTTON_MIDDLE;
	// QAM, between the pads, puts right click in reach of the thumb that
	// drives the pointer
	if (s_own[LY_CTRL] & TB_QAM)
		mbtn |= MOUSE_BUTTON_RIGHT;
	// back and forward on the face's upper pair, back on the left as in a
	// browser
	if (s_own[LY_CTRL] & TB_X)
		mbtn |= MOUSE_BUTTON_BACKWARD;
	if (s_own[LY_CTRL] & TB_Y)
		mbtn |= MOUSE_BUTTON_FORWARD;
	sendMouse(mbtn, dx, dy, wheel);
}

static void bumpBoth()
{
	for (int i = 0; i < 2; i++)
		hapticTone(TONE_SIDE[i], LK_BUMP_DB, LK_BUMP_DUR);
}

// The Guide button (the controller's Steam button) reaches LizardKing before
// the host. In the controller layer a tap goes to Windows as the Xbox Guide
// press (Game Bar, the Steam overlay), and a hold past GUIDE_MEDIUM_MS is
// LizardKing's own: the Guide on, or without a Guide the guide drive. Guide +
// another button is a chord, in any layer, and neither reaches the host.
// While the Guide is up, any press that isn't a chord puts it away.
#define GUIDE_MEDIUM_MS 400
// how long the forwarded tap holds the Xbox Guide button
#define GUIDE_TAP_MS 80

// a switch confirms with two bumps for on, one for off
static void confirm(bool on)
{
	bumpBoth();
	s_bumpAgainAt = on ? (millis() + 120) | 1 : 0;
}

// Guide + Back pressed: the controller goes off when both are released. A
// Guide button still down as it powers off turns it straight back on, and
// on its USB cable it then comes up wired, past the puck.
static bool s_offPending;

static void guideChord(uint32_t btn)
{
	// Back: the controller off, as Steam's "turn off controller" does,
	// once the chord is let go (see guideButton)
	if (btn & TB_MENU) {
		s_offPending = true;
		return;
	}
	if (!(btn & (TB_RPADC | TB_LPADC | TB_VIEW)))
		return;
	if (!Serial) {
		// these need the Guide
		bumpBoth();
		return;
	}
	if (btn & TB_LPADC) {
		s_keymapSize = (uint8_t)((s_keymapSize + 1) % 3);
		confirm(s_keymapSize);
	} else if (btn & TB_RPADC) {
		s_keymapLow = !s_keymapLow;
		bumpBoth();
	} else {
		Serial.printf("KI\n");
		bumpBoth();
	}
	// report the switches now rather than within the second
	s_statusMs = 0;
}

// Only a press with no thumb on a pad, from start to finish, opens anything
// (the Guide, the drive, Windows' Guide): with a thumb on a pad the Guide
// button is a chord about to happen, and a thumb landing mid-press is one
// on its way. Closing the Guide works from anywhere. The medium press
// answers the moment the hold passes GUIDE_MEDIUM_MS, so the Guide (or the
// drive) appearing is the sign it worked; holding on changes nothing.
static void guideMedium(bool gamepad)
{
	bool up = guideHolds();
	if (up || (gamepad && Serial)) {
		Serial.printf("KG\n");
		confirm(!up);
	} else if (gamepad) {
		confirm(guideDriveToggle());
	}
}

static void guideTap(uint32_t now, bool gamepad)
{
	if (guideHolds())
		guideMedium(gamepad);
	else if (gamepad)
		s_guideTapUntil = (now + GUIDE_TAP_MS) | 1;
}

// Takes the Guide button and any chord buttons out of the report; a chord's
// button stays the puck's until it is released. A press is spent by a chord
// or by reaching the medium press; a release that isn't is a tap.
static uint32_t guideButton(uint32_t raw, uint32_t now)
{
	static bool down, spent, gamepad;
	static uint32_t since, prev, swallow;
	uint32_t fresh = raw & ~prev & LK_BTN_MASK & ~TB_STEAM;
	prev = raw;
	if (raw & TB_STEAM) {
		if (!down) {
			down = true;
			spent = false;
			gamepad = true;
			since = now;
		}
		// a thumb on a pad, at the press or any time during it, means a
		// chord is coming: the press opens nothing
		if (raw & (TB_LPADT | TB_RPADT))
			gamepad = false;
		if (fresh) {
			spent = true;
			swallow |= fresh;
			guideChord(fresh);
		}
		if (!spent && now - since >= GUIDE_MEDIUM_MS) {
			spent = true;
			guideMedium(gamepad);
		}
	} else if (down) {
		down = false;
		if (!spent)
			guideTap(now, gamepad);
	}
	if (s_offPending && !(raw & (TB_STEAM | TB_MENU))) {
		s_offPending = false;
		if (s_slot >= 0)
			hapticSendShutdown((uint8_t)s_slot);
	}
	swallow &= raw;
	return raw & ~TB_STEAM & ~swallow;
}

static void lkProcess(const PuckInput &in)
{
	uint32_t now = millis();
	uint32_t derived = deriveButtons(in);
	uint32_t raw = guideButton(derived, now);
	uint8_t touchLayer = layerFromTouch(in.buttons);
	layerStep(touchLayer, now);
	ownButtons(raw & LK_BTN_MASK, touchLayer);
	streamInput(in, derived, now);
	gamepadStep(in);

	bool rTouch = in.buttons & TB_RPADT, lTouch = in.buttons & TB_LPADT;
	const uint8_t zone[2] = { padZone(s_pad[PAD_R], rTouch, in.rpx, in.rpy),
				  padZone(s_pad[PAD_L], lTouch, in.lpx,
					  in.lpy) };
	const bool wasOn[2] = { s_pad[PAD_R].on, s_pad[PAD_L].on };
	int rdx, rdy, ldx, ldy;
	padDelta(s_pad[PAD_R], rTouch, in.rpx, in.rpy, &rdx, &rdy);
	padDelta(s_pad[PAD_L], lTouch, in.lpx, in.lpy, &ldx, &ldy);
	const int travel[2] = { abs(rdx) + abs(rdy), abs(ldx) + abs(ldy) };

	padClicks(raw, zone);
	holdGuard(raw, zone);
	triggerPulses(raw);
	zoneHaptics(in, zone, wasOn, travel);
	streamState(zone);
	keyboardStep();
	mediaStep();
	mouseStep(in, touchLayer, rdx, rdy, now);
}

// ---- controller ----

void LizardKingController::usbIdentity()
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

void LizardKingController::begin()
{
	memcpy(s_keymap, KEYMAP, sizeof s_keymap);
	beginShared();
	usbIdentity();
	g_lkXi.begin();
	g_lkHid.setReportDescriptor(LK_HID_DESC, sizeof LK_HID_DESC);
	g_lkHid.setReportCallback(lkGetReport, lkSetReport);
	g_lkHid.setStringDescriptor("LizardKing Keyboard/Mouse");
	g_lkHid.setPollInterval(1);
	g_lkHid.begin();
	// standard CDC: the Guide's link and the serial console
	Serial.setStringDescriptor("LizardKing Serial");
	TinyUSBDevice.addInterface(Serial);
	// last, so the CDC function keeps its interface number (and Windows
	// its COM port) whether or not the drive is built in
	guideDriveBegin();
}

void LizardKingController::onReport45(int slot, const uint8_t *rep, bool fresh,
				      uint8_t bodyTlen)
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
			lkProcess(g_in[slot]);
		const PuckInput &in = g_in[slot];
		if (g_lkGyroTrace && slot == s_slot && fresh &&
		    Serial.availableForWrite() > 60)
			Serial.printf("Y %lu %d %d %d %d %d %d\n",
				      (unsigned long)in.imuTimestampUs, in.gx,
				      in.gy, in.gz, in.ax, in.ay, in.az);
	}
	XboxController::onReport45(slot, rep, fresh, bodyTlen);
}

void LizardKingController::emit(uint8_t slot, uint32_t generation,
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

// Status for the Guide, about once a second while it holds the port:
// "KT connected battery% charge signal keymap low dev serial" (charge as in report
// 0x43: 1 discharging, 2 charging, 4 charged, 0 unknown; signal in -dBm, 0 =
// no sample yet; keymap the key map's size, 0 off, 1 large, 2 small, and low
// its place; dev, the console
// has the key map or a feel constant off what ships, see lkTune; serial,
// from its bond, the connected controller's, else the paired one's, or "-").
static bool devOverrides();
static void streamStatus(uint32_t now)
{
	if (now - s_statusMs < 1000 || !Serial ||
	    Serial.availableForWrite() < 40)
		return;
	s_statusMs = now;
	bool on = s_slot >= 0;
	int rssi = on && g_linkRssi[s_slot] > RSSI_DBM_OFFSET ?
			   g_linkRssi[s_slot] - RSSI_DBM_OFFSET :
			   0;
	unsigned batt = on ? g_battery[s_slot] : 0;
	unsigned charge = on ? g_batteryState[s_slot] : 0;
	// the bond record is [4 + 4 uuids][16 serial, ASCII, zero padded]; off,
	// the first paired controller's
	int bond = s_slot;
	for (int i = 0; bond < 0 && i < NSLOT; i++)
		if (g_slot[i].used)
			bond = i;
	char serial[17] = "-";
	if (bond >= 0) {
		memcpy(serial, g_slot[bond].rec + 8, 16);
		serial[16] = 0;
		for (char *c = serial; *c; c++)
			if (*c <= ' ' || *c > '~')
				*c = 0;
		if (!serial[0])
			strcpy(serial, "-");
	}
	Serial.printf("KT %d %u %u %d %d %d %d %s\n", on ? 1 : 0, batt, charge,
		      rssi, s_keymapSize, s_keymapLow, devOverrides(), serial);
}

void LizardKingController::task()
{
	XboxController::task();
	guideDriveTask();
	streamStatus(millis());
	if (s_bumpAgainAt && (int32_t)(millis() - s_bumpAgainAt) >= 0) {
		s_bumpAgainAt = 0;
		bumpBoth();
	}
	if (s_slot >= 0 && !bondAlive(s_slot, millis())) {
		releaseAll();
		s_slot = -1;
	}
}

// ---- live tuning from the serial console ----

// Saving these to flash, so a change survives a reboot, is left for when
// LizardKing lets its owner keep a customization (docs/ROADMAP.md).

void lkSetKeymapPage(const char *a)
{
	char *e;
	long pad = strtol(a, &e, 16), zone = strtol(e, &e, 16);
	uint8_t page[8];
	for (int k = 0; k < 8; k++)
		page[k] = (uint8_t)strtol(e, &e, 16);
	if (pad < 0 || pad > 1 || zone < 0 || zone >= Z_COUNT) {
		Serial.printf("# ~K pad zone k0..k7 (hex)\n");
		return;
	}
	memcpy(s_keymap[pad][zone], page, sizeof page);
	s_statusMs = 0;
}

// each with the value it ships with, read before the console can change it
#define TUNE_I(n) { #n, nullptr, &n, (float)n }
#define TUNE_F(n) { #n, &n, nullptr, n }
static const struct {
	const char *name;
	float *f;
	int *i;
	float shipped;
} TUNABLES[] = {
	TUNE_I(LK_SWITCH_MS),	    TUNE_I(LK_CLICK_ON),
	TUNE_I(LK_CLICK_OFF),	    TUNE_I(LK_KEY_CLICK_ON),
	TUNE_I(LK_KEY_CLICK_OFF),   TUNE_I(LK_PX_DIV),
	TUNE_I(LK_DZ_START),	    TUNE_I(LK_DZ_FULL),
	TUNE_I(LK_CLICK_ON_US),	    TUNE_I(LK_RELEASE_ON_US),
	TUNE_I(LK_KEY_ON_US),	    TUNE_I(LK_KEY_RELEASE_ON_US),
	TUNE_I(LK_TONE_HZ),	    TUNE_I(LK_TICK_DB),
	TUNE_I(LK_TICK_DUR),	    TUNE_I(LK_KBD_ON_DB),
	TUNE_I(LK_KBD_OFF_DB),	    TUNE_I(LK_KBD_DUR),
	TUNE_I(LK_ZONE_HYST),	    TUNE_I(LK_TEX_COUNTS),
	TUNE_I(LK_TEX_DB),	    TUNE_I(LK_BUMP_DB),
	TUNE_I(LK_BUMP_DUR),	    TUNE_I(LK_TRIG_ON),
	TUNE_I(LK_TRIG_OFF),	    TUNE_F(LK_FOLLOW_SLOW),
	TUNE_F(LK_FOLLOW_FAST),	    TUNE_F(LK_FOLLOW_MIN),
	TUNE_F(LK_SPEED_ALPHA),	    TUNE_F(LK_LAUNCH_ALPHA),
	TUNE_F(LK_GLIDE_DECAY),	    TUNE_F(LK_GLIDE_DRAG),
	TUNE_F(LK_GLIDE_MAX),	    TUNE_F(LK_GHOST_DECAY),
	TUNE_F(LK_DEADZONE_PX),	    TUNE_F(LK_LIFT_GAIN),
	TUNE_F(LK_LIFT_MAX),	    TUNE_F(LK_LIFT_DECAY),
	TUNE_F(LK_LIFT_HOLD_PX),    TUNE_F(LK_SCROLL_REV),
	TUNE_F(LK_SCROLL_R_IN),	    TUNE_F(LK_SCROLL_R_OUT),
	TUNE_F(LK_SCROLL_OUTLIER),  TUNE_F(LK_SCROLL_R_HOLD),
	TUNE_F(LK_SCROLL_FLING),    TUNE_F(LK_SCROLL_DECAY),
	TUNE_F(LK_SCROLL_STOP),	    TUNE_F(LK_SCROLL_TICK),
	TUNE_F(LK_TICK_PX),	    TUNE_F(LK_GYRO_SENS_SLOW),
	TUNE_F(LK_GYRO_SENS_FAST),  TUNE_F(LK_GYRO_SLOW_DPS),
	TUNE_F(LK_GYRO_FAST_DPS),   TUNE_F(LK_GYRO_SMOOTH_DPS),
	TUNE_F(LK_GYRO_YAW_RELAX),  TUNE_F(LK_GYRO_STILL),
	TUNE_F(LK_GYRO_BIAS_RATE),  TUNE_F(LK_GYRO_YAW_SIGN),
	TUNE_F(LK_GYRO_PITCH_SIGN),
};

static bool devOverrides()
{
	if (memcmp(s_keymap, KEYMAP, sizeof s_keymap))
		return true;
	for (const auto &t : TUNABLES)
		if ((t.f ? *t.f : (float)*t.i) != t.shipped)
			return true;
	return false;
}

void lkTune(const char *a)
{
	while (*a == ' ')
		a++;
	char name[32];
	size_t n = strcspn(a, " ");
	if (n >= sizeof name)
		n = sizeof name - 1;
	memcpy(name, a, n);
	name[n] = 0;
	const char *value = a + n;
	while (*value == ' ')
		value++;
	for (const auto &t : TUNABLES) {
		if (n && strcmp(name, t.name))
			continue;
		if (n && *value) {
			double v = atof(value);
			if (t.f)
				*t.f = (float)v;
			else
				*t.i = (int)v;
			s_statusMs = 0;
		}
		if (t.f)
			Serial.printf("# %s %g\n", t.name, (double)*t.f);
		else
			Serial.printf("# %s %d\n", t.name, *t.i);
		if (n)
			return;
	}
	if (n)
		Serial.printf("# no tunable %s\n", name);
}
