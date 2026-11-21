# LizardKing build targets.

# Pinned to clang-format 18: output differs between versions.
CLANG_FORMAT ?= $(shell command -v clang-format-18 || command -v clang-format)

FORMAT_FILES := $(shell find LizardKing \
	\( -name '*.c' -o -name '*.cpp' -o -name '*.h' -o -name '*.ino' \) \
	-not -name 'git_version.h' -not -name 'guide_exe.h' \
	-not -path 'LizardKing/src/*')

FQBN ?= adafruit:nrf52:feather52840
SKETCH = LizardKing
BUILD_PATH ?= build/cache
OUTPUT_DIR ?= build/out
EXTRA_FLAGS ?=

# TinyUSB values that differ from the Adafruit core defaults:
#   CFG_TUD_HID: Steam mode's four slot HIDs plus the wake mouse.
#   CFG_TUD_TASK_QUEUE_SZ: loop()-context sends must never block on a full
#     usbd event queue, or the watchdog resets the puck.
CFG_TUD_HID ?= 6
CFG_TUD_TASK_QUEUE_SZ ?= 512
RF_JOURNAL_BASE ?= 0x86000

USB_FLAGS = -DNRF52840_XXAA {build.flags.usb} -DCFG_TUD_HID=$(CFG_TUD_HID) \
	-DCFG_TUD_TASK_QUEUE_SZ=$(CFG_TUD_TASK_QUEUE_SZ) \
	-DOPK_RF_JOURNAL_BASE=$(RF_JOURNAL_BASE) $(EXTRA_FLAGS)
LINK_FLAGS = -Wl,--wrap=tud_vendor_control_xfer_cb \
	-Wl,--wrap=tud_descriptor_string_cb \
	-Wl,--section-start=.rf_journal_guard=$(RF_JOURNAL_BASE)

# The guide drive carries the Windows Guide. On Windows the build compiles it
# with the .NET Framework's own csc and embeds it; elsewhere, or with
# LIZARD_DRIVE=0, the firmware builds without the drive.
LIZARD_DRIVE ?= 1
GUIDE_H = $(SKETCH)/guide_exe.h
CSC ?= /c/Windows/Microsoft.NET/Framework64/v4.0.30319/csc.exe

PORT ?=

.PHONY: need-clang-format format format-check check build guide-header \n	flash deploy

need-clang-format:
	@[ -n "$(CLANG_FORMAT)" ] || { echo "clang-format 18 not found"; exit 1; }

format: need-clang-format
	$(CLANG_FORMAT) -i $(FORMAT_FILES)

format-check: need-clang-format
	$(CLANG_FORMAT) --dry-run --Werror $(FORMAT_FILES)

check: format-check
	python3 tools/check-trailing-comments.py $(FORMAT_FILES)

guide-header:
ifeq ($(LIZARD_DRIVE)$(OS),1Windows_NT)
	mkdir -p build/guide
	"$(CSC)" -nologo -target:winexe -optimize+ \
		'-out:build\guide\LizardKingGuide.exe' \
		-r:System.Windows.Forms.dll -r:System.Drawing.dll \
		'guide\LizardKingGuide.cs'
	cd build/guide && xxd -i LizardKingGuide.exe > guide_exe.h.tmp
	sed -e 's/^unsigned/static const unsigned/' build/guide/guide_exe.h.tmp > $(GUIDE_H)
else
	rm -f $(GUIDE_H)
endif

build: guide-header
	./gen_version.sh
	arduino-cli compile -b $(FQBN) --build-path $(BUILD_PATH) \
		--output-dir $(OUTPUT_DIR) \
		--build-property "build.extra_flags=$(USB_FLAGS)" \
		--build-property "compiler.c.elf.extra_flags=$(LINK_FLAGS)" \
		$(SKETCH)

# make flash PORT=COM12
flash:
	@[ -n "$(PORT)" ] || { echo "usage: make flash PORT=<port>"; exit 1; }
	arduino-cli upload -b $(FQBN) -p "$(PORT)" --input-dir $(OUTPUT_DIR) $(SKETCH)

deploy: build flash
