// eliz_drive.cpp -- LizardKing's read-only guide drive. See eliz_drive.h.
//
// The volume is synthesized on the fly (no flash file system): a FAT12 boot
// sector, FAT and root directory computed from two files that live in the
// firmware image, the README below and the Guide's exe embedded at build time
// as guide_exe.h.
#include <Arduino.h>
#include <string.h>
#include "Adafruit_TinyUSB.h"
#include "eliz_drive.h"

#if __has_include("guide_exe.h")
#include "guide_exe.h"
#define ELIZ_HAVE_TRAINER 1
#else
#define ELIZ_HAVE_TRAINER 0
#endif

#if ELIZ_HAVE_TRAINER

static const char README[] =
	"LizardKing Guide\r\n"
	"================\r\n"
	"\r\n"
	"Open LizardKing Guide.exe to see what every button does on this\r\n"
	"controller. It changes with what your thumbs are touching.\r\n"
	"\r\n"
	"Nothing is installed unless you ask: while the Guide is open,\r\n"
	"hold the Guide (Steam) button and press Start to install it.\r\n"
	"\r\n"
	"Windows may say the program is from an unknown publisher: it is\r\n"
	"not code-signed. Leave your security settings as they are.\r\n"
	"\r\n"
	"The drive hides itself once the Guide is running.\r\n";

// 1 MB FAT12 volume, one 512-byte sector per cluster
enum {
	SECTOR = 512,
	TOTAL_SECTORS = 2048,
	NUM_FATS = 2,
	FAT_SECTORS = 6,
	ROOT_ENTRIES = 64,
	ROOT_SECTORS = ROOT_ENTRIES * 32 / SECTOR,
	FAT_START = 1,
	ROOT_START = FAT_START + NUM_FATS * FAT_SECTORS,
	DATA_START = ROOT_START + ROOT_SECTORS,
};

// 2026-11-20 12:00, FAT packed date / time
#define FAT_DATE ((2026 - 1980) << 9 | 11 << 5 | 20)
#define FAT_TIME (12 << 11)

struct DriveFile {
	const char *longName;
	const char shortName[12];
	const uint8_t *data;
	uint32_t size;
	uint16_t firstCluster, clusters;
};

static DriveFile s_files[] = {
	{ "README.txt", "README  TXT", (const uint8_t *)README,
	  sizeof README - 1, 0, 0 },
	{ "LizardKing Guide.exe", "LIZARD~1EXE", LizardKingGuide_exe,
	  LizardKingGuide_exe_len, 0, 0 },
};
static const int NFILES = sizeof s_files / sizeof s_files[0];

static uint8_t s_root[ROOT_SECTORS * SECTOR];
static Adafruit_USBD_MSC s_msc;
static bool s_shown, s_wasMounted;

static void put16(uint8_t *p, uint16_t v)
{
	p[0] = v;
	p[1] = v >> 8;
}

static void put32(uint8_t *p, uint32_t v)
{
	put16(p, v);
	put16(p + 2, v >> 16);
}

static uint8_t lfnChecksum(const char *shortName)
{
	uint8_t sum = 0;
	for (int i = 0; i < 11; i++)
		sum = ((sum & 1) << 7) + (sum >> 1) + (uint8_t)shortName[i];
	return sum;
}

// Long names (VFAT): entries hold 13 UTF-16 characters each, stored last
// part first, then the 8.3 entry they belong to.
static uint8_t *putLongName(uint8_t *e, const DriveFile &f)
{
	static const uint8_t SLOT[13] = { 1,  3,  5,  7,  9,  14, 16,
					  18, 20, 22, 24, 28, 30 };
	int len = strlen(f.longName);
	int parts = (len + 1 + 12) / 13;
	if (len % 13 == 0)
		parts = len / 13;
	uint8_t sum = lfnChecksum(f.shortName);
	for (int part = parts; part >= 1; part--, e += 32) {
		e[0] = part | (part == parts ? 0x40 : 0);
		e[11] = 0x0F;
		e[13] = sum;
		for (int i = 0; i < 13; i++) {
			int c = (part - 1) * 13 + i;
			uint16_t ch = c < len  ? (uint8_t)f.longName[c] :
				      c == len ? 0x0000 :
						 0xFFFF;
			put16(e + SLOT[i], ch);
		}
	}
	return e;
}

static void putShortEntry(uint8_t *e, const char *name, uint8_t attr,
			  uint16_t cluster, uint32_t size)
{
	memcpy(e, name, 11);
	e[11] = attr;
	put16(e + 14, FAT_TIME);
	put16(e + 16, FAT_DATE);
	put16(e + 18, FAT_DATE);
	put16(e + 22, FAT_TIME);
	put16(e + 24, FAT_DATE);
	put16(e + 26, cluster);
	put32(e + 28, size);
}

static void buildRoot()
{
	uint8_t *e = s_root;
	// volume label, read-only files
	putShortEntry(e, "LIZARDKING ", 0x08, 0, 0);
	e += 32;
	uint16_t cluster = 2;
	for (int i = 0; i < NFILES; i++) {
		DriveFile &f = s_files[i];
		f.firstCluster = cluster;
		f.clusters = (f.size + SECTOR - 1) / SECTOR;
		cluster += f.clusters;
		e = putLongName(e, f);
		putShortEntry(e, f.shortName, 0x01, f.firstCluster, f.size);
		e += 32;
	}
}

static void bootSector(uint8_t *b)
{
	static const uint8_t JUMP[3] = { 0xEB, 0x3C, 0x90 };
	memcpy(b, JUMP, 3);
	memcpy(b + 3, "MSWIN4.1", 8);
	put16(b + 11, SECTOR);
	b[13] = 1;
	put16(b + 14, FAT_START);
	b[16] = NUM_FATS;
	put16(b + 17, ROOT_ENTRIES);
	put16(b + 19, TOTAL_SECTORS);
	b[21] = 0xF8;
	put16(b + 22, FAT_SECTORS);
	put16(b + 24, 1);
	put16(b + 26, 1);
	b[36] = 0x80;
	b[38] = 0x29;
	put32(b + 39, 0x4C5A5043);
	memcpy(b + 43, "LIZARDKING FAT12   ", 19);
	b[510] = 0x55;
	b[511] = 0xAA;
}

// FAT12 entry n: each file is one contiguous cluster chain
static uint16_t fatEntry(uint32_t n)
{
	if (n < 2)
		return n == 0 ? 0xFF8 : 0xFFF;
	for (int i = 0; i < NFILES; i++) {
		const DriveFile &f = s_files[i];
		uint32_t last = f.firstCluster + f.clusters - 1;
		if (n >= f.firstCluster && n <= last)
			return n == last ? 0xFFF : n + 1;
	}
	return 0;
}

// two 12-bit entries share three bytes
static void fatSector(uint8_t *b, uint32_t index)
{
	for (uint32_t i = 0; i < SECTOR; i++) {
		uint32_t at = index * SECTOR + i, k = at / 3;
		uint16_t e0 = fatEntry(2 * k), e1 = fatEntry(2 * k + 1);
		switch (at % 3) {
		case 0:
			b[i] = e0;
			break;
		case 1:
			b[i] = (e0 >> 8) | (e1 << 4);
			break;
		default:
			b[i] = e1 >> 4;
			break;
		}
	}
}

static void dataSector(uint8_t *b, uint32_t cluster)
{
	for (int i = 0; i < NFILES; i++) {
		const DriveFile &f = s_files[i];
		if (cluster < f.firstCluster ||
		    cluster >= f.firstCluster + f.clusters)
			continue;
		uint32_t at = (cluster - f.firstCluster) * SECTOR;
		uint32_t n = f.size - at < SECTOR ? f.size - at : SECTOR;
		memcpy(b, f.data + at, n);
		return;
	}
}

static int32_t readSectors(uint32_t lba, void *buffer, uint32_t bufsize)
{
	uint8_t *b = (uint8_t *)buffer;
	for (uint32_t done = 0; done + SECTOR <= bufsize;
	     done += SECTOR, lba++, b += SECTOR) {
		memset(b, 0, SECTOR);
		if (lba == 0)
			bootSector(b);
		else if (lba < ROOT_START)
			fatSector(b, (lba - FAT_START) % FAT_SECTORS);
		else if (lba < DATA_START)
			memcpy(b, s_root + (lba - ROOT_START) * SECTOR, SECTOR);
		else
			dataSector(b, lba - DATA_START + 2);
	}
	return bufsize;
}

static int32_t writeSectors(uint32_t lba, uint8_t *buffer, uint32_t bufsize)
{
	return -1;
}

static bool writable()
{
	return false;
}

static bool ready()
{
	return s_shown;
}

void elizDriveBegin()
{
	buildRoot();
	s_msc.setID("LizPuck", "Guide drive", "1.0");
	s_msc.setCapacity(TOTAL_SECTORS, SECTOR);
	s_msc.setReadWriteCallback(readSectors, writeSectors, nullptr);
	s_msc.setWritableCallback(writable);
	s_msc.setReadyCallback(ready);
	s_msc.begin();
}

void elizDriveTask()
{
	bool mounted = TinyUSBDevice.mounted();
	// its only job is starting the Guide, so the Guide connecting hides it
	if ((mounted && !s_wasMounted) || Serial)
		s_shown = false;
	s_wasMounted = mounted;
}

bool elizDriveToggle()
{
	s_shown = !s_shown;
	return s_shown;
}

#else

void elizDriveBegin()
{
}

void elizDriveTask()
{
}

bool elizDriveToggle()
{
	return false;
}

#endif
