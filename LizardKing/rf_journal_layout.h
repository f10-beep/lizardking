// Fixed flash layout for the RF channel-history journal.
//
// Application images are capped immediately below this window (the linker
// refuses one that grows into it), so learned RF history survives installs.
#pragma once
#include <stdint.h>

#define RF_CHANNEL_JOURNAL_PAGE_BYTES 4096u
#define RF_CHANNEL_JOURNAL_PAGE_COUNT 2u
#ifndef OPK_RF_JOURNAL_BASE
#define OPK_RF_JOURNAL_BASE 0x86000UL
#endif
#define RF_CHANNEL_JOURNAL_BASE OPK_RF_JOURNAL_BASE
#define RF_CHANNEL_JOURNAL_END     \
	(RF_CHANNEL_JOURNAL_BASE + \
	 RF_CHANNEL_JOURNAL_PAGE_BYTES * RF_CHANNEL_JOURNAL_PAGE_COUNT)

static_assert((RF_CHANNEL_JOURNAL_BASE &
	       (RF_CHANNEL_JOURNAL_PAGE_BYTES - 1u)) == 0u,
	      "RF journal base must be page aligned");
