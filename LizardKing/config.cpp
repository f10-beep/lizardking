#include "config.h"
#include "haptics.h"
#include "rf_link.h"
#include <Adafruit_TinyUSB.h>
#include <Adafruit_LittleFS.h>
#include <InternalFileSystem.h>
#include <string.h>
using namespace Adafruit_LittleFS_Namespace;

uint8_t g_usbMode = 0;
bool g_xbox = false;
uint32_t g_pollUs = POLL_US_DEFAULT;

#define CFG_FILE "/cfg.bin"
// Every install wipes the file system, so the layout needn't stay compatible:
// a file with another magic is ignored and rewritten with the defaults.
#define CFG_MAGIC 0x1C
struct Cfg {
	uint8_t magic;
	// host-rumble strength as percent/2, so 500% fits a byte
	uint8_t rumbScale2;
	// RUMBLE_STYLE_*
	uint8_t rumbleStyle;
	// power the controllers off on host sleep (see g_suspendOff), 0/1
	uint8_t suspendOff;
	uint8_t ext[CFG_EXT_STORAGE_BYTES];
};

static uint8_t g_cfgExt[CFG_EXT_STORAGE_BYTES];

uint8_t cfgExtRead(uint8_t index)
{
	return index < CFG_EXT_STORAGE_BYTES ? g_cfgExt[index] : 0xFFu;
}

void cfgExtWrite(uint8_t index, uint8_t value)
{
	if (index < CFG_EXT_STORAGE_BYTES)
		g_cfgExt[index] = value;
}

static bool s_dirty;

void saveCfg()
{
	s_dirty = true;
}

static void writeCfg()
{
	Cfg c = { CFG_MAGIC,
		  (uint8_t)(g_rumbleScale / 2),
		  g_rumbleStyle,
		  g_suspendOff,
		  {} };
	memcpy(c.ext, g_cfgExt, sizeof c.ext);
	InternalFS.remove(CFG_FILE);
	File f(InternalFS);
	if (f.open(CFG_FILE, FILE_O_WRITE)) {
		f.write((uint8_t *)&c, sizeof c);
		f.close();
	}
}

// A LittleFS write erases flash, which halts the CPU for hundreds of ms: long
// enough for the RF link to drop mid-play (and a held chord to fire twice).
// So writes wait until no controller is live or the host sleeps.
void cfgTask()
{
	if (!s_dirty || (anySlotLinkUp() && !USBDevice.suspended()))
		return;
	s_dirty = false;
	writeCfg();
}

void loadCfg()
{
	Cfg c;
	memset(g_cfgExt, 0xFF, sizeof g_cfgExt);
	File f(InternalFS);
	if (f.open(CFG_FILE, FILE_O_READ)) {
		bool ok = f.read((uint8_t *)&c, sizeof c) == (int)sizeof c &&
			  c.magic == CFG_MAGIC;
		f.close();
		if (ok) {
			memcpy(g_cfgExt, c.ext, sizeof g_cfgExt);
			uint16_t pct = (uint16_t)c.rumbScale2 * 2;
			if (pct >= RUMBLE_SCALE_MIN && pct <= RUMBLE_SCALE_MAX)
				g_rumbleScale = pct;
			if (c.rumbleStyle <= RUMBLE_STYLE_MAX)
				g_rumbleStyle = c.rumbleStyle;
			if (c.suspendOff <= 1)
				g_suspendOff = c.suspendOff;
			return;
		}
	}
	saveCfg();
}

// A UF2 install writes this block as the image has it (word 0 blank), and the
// first boot after it stamps word 0, so every install starts unpaired. The dev
// UF2 carries the block already stamped (Makefile, KEEP_BONDS), so the bonds
// survive. Stamping our own image is safe only because a UF2 install records
// no app CRC; a serial DFU upload records one, which the stamp would fail.
__attribute__((section(".fresh_install"), aligned(256),
	       used)) static const uint32_t g_freshInstall[64] = { 0xFFFFFFFFu };

void wipeOnFreshInstall()
{
	volatile const uint32_t *w = g_freshInstall;
	if (*w != 0xFFFFFFFFu)
		return;
	InternalFS.format();
	NRF_NVMC->CONFIG = NVMC_CONFIG_WEN_Wen << NVMC_CONFIG_WEN_Pos;
	*(volatile uint32_t *)w = 0;
	while (!NRF_NVMC->READY) {
	}
	NRF_NVMC->CONFIG = NVMC_CONFIG_WEN_Ren << NVMC_CONFIG_WEN_Pos;
}
