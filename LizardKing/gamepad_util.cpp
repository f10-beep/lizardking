#include "gamepad_util.h"
#include "triton.h"
#include "config.h"
#include "haptics.h"
#include "bonds.h"

static bool g_psPadClickDown[NSLOT] = {};
static unsigned long g_psPadClickReplyMs[NSLOT] = {};

// Pick whichever of the two sources is deflected further from center, keeping its sign. -32768 has no
// positive twin in int16, so clamp its magnitude to 32767 before comparing -- otherwise the negation
// overflows and a full-left stick would lose to anything.
static inline int16_t strongerAxis(int16_t a, int16_t b)
{
	int32_t ma = (a == -32768) ? 32767 : (a < 0 ? -(int32_t)a : (int32_t)a);
	int32_t mb = (b == -32768) ? 32767 : (b < 0 ? -(int32_t)b : (int32_t)b);
	return (ma >= mb) ? a : b;
}

// Map Steam trackpad s16 coords into a 0..max axis (centered touch -> mid-range).
uint16_t padNormU16(int16_t v, uint16_t maxv)
{
	int32_t t = ((int32_t)v + 32768);
	if (t < 0)
		t = 0;
	if (t > 65535)
		t = 65535;
	return (uint16_t)((t * (int32_t)maxv) / 65535);
}
void touchPackPoint(uint8_t *base, int finger, bool touch, uint16_t x,
		    uint16_t y)
{
	uint8_t *f = base + finger * 4;
	if (!touch) {
		f[0] = 0x80;
		f[1] = 0;
		f[2] = 0;
		f[3] = 0;
		return;
	}
	f[0] = (uint8_t)(finger & 0x7F);
	f[1] = (uint8_t)(x & 0xFF);
	f[2] = (uint8_t)(((x >> 8) & 0x0F) | ((y & 0x0F) << 4));
	f[3] = (uint8_t)((y >> 4) & 0xFF);
}

struct TouchContactState {
	bool down;
	uint8_t id;
	uint8_t source;
	uint16_t x;
	uint16_t y;
};

static TouchContactState g_touchState[NSLOT][2] = {};

static void touchPackStatefulPoint(uint8_t stateSlot, uint8_t *base, int finger,
				   bool touch, uint8_t source, uint16_t x,
				   uint16_t y)
{
	if (stateSlot >= NSLOT || finger < 0 || finger > 1) {
		touchPackPoint(base, finger, touch, x, y);
		return;
	}
	TouchContactState &st = g_touchState[stateSlot][finger];
	if (touch) {
		if (!st.down || st.source != source) {
			st.id = (uint8_t)((st.id + 1u) & 0x7Fu);
			st.source = source;
		}
		st.down = true;
		st.x = x;
		st.y = y;
	} else if (st.down) {
		st.down = false;
	}
	uint8_t *f = base + finger * 4;
	f[0] = (uint8_t)(st.id | (st.down ? 0x00u : 0x80u));
	f[1] = (uint8_t)(st.x & 0xFF);
	f[2] = (uint8_t)(((st.x >> 8) & 0x0F) | ((st.y & 0x0F) << 4));
	f[3] = (uint8_t)((st.y >> 4) & 0xFF);
}

// Map g_back[] paddle code -> Steam button flags (same codes as codeToXB / codeToSwitch).
static void psOrBackCode(uint32_t *b, uint8_t c)
{
	switch (c) {
	case 1:
		*b |= TB_A;
		break;
	case 2:
		*b |= TB_B;
		break;
	case 3:
		*b |= TB_X;
		break;
	case 4:
		*b |= TB_Y;
		break;
	case 5:
		*b |= TB_LB;
		break;
	case 6:
		*b |= TB_RB;
		break;
	case 7:
		*b |= TB_L3;
		break;
	case 8:
		*b |= TB_R3;
		break;
	case 9:
		*b |= TB_VIEW;
		break;
	case 10:
		*b |= TB_MENU;
		break;
	case 11:
		*b |= TB_STEAM;
		break;
	case 12:
		*b |= TB_DUP;
		break;
	case 13:
		*b |= TB_DDN;
		break;
	case 14:
		*b |= TB_DLF;
		break;
	case 15:
		*b |= TB_DRT;
		break;
	case 16:
		*b |= TB_TOUCH;
		break;
	case 17:
		*b |= TB_MUTE;
		break;
	case 19:
		*b |= TB_L2; // left trigger (L2)
		break;
	case 20:
		*b |= TB_R2; // right trigger (R2)
		break;
	default:
		break;
	}
}
// DualSense / DS4 buttons[1]: L1..R3, Create(Share), Options(Start). The analog trigger values come from
// the per-slot `g_in[slot]` (pass lt/rt explicitly so a single shoulder byte never leaks across slots).
uint8_t psShouldersByte(uint32_t b, uint8_t lt, uint8_t rt)
{
	return ((b & TB_LB) ? 0x01 : 0) | ((b & TB_RB) ? 0x02 : 0) |
	       ((lt > SW_TRIG_ON || (b & 0x8000000u)) ? 0x04 : 0) |
	       ((rt > SW_TRIG_ON || (b & 0x800000u)) ? 0x08 : 0) |
	       // 0x10 = Create/Share, 0x20 = Options; TB_MENU/TB_VIEW are the
	       // Select/Start-side buttons respectively (see triton.h)
	       ((b & TB_MENU) ? 0x10 : 0) | ((b & TB_VIEW) ? 0x20 : 0) |
	       ((b & TB_L3) ? 0x40 : 0) | ((b & TB_R3) ? 0x80 : 0);
}
