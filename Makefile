# LizardKing build targets.

# Windows installs often name it python, not python3
PYTHON ?= $(shell command -v python3 || command -v python)

# Pinned to clang-format 18: output differs between versions. Falls back to
# the PyPI build (pip install clang-format==18.1.8), whose folder is often
# not on the PATH.
CLANG_FORMAT ?= $(shell command -v clang-format-18 || command -v clang-format || \
	$(PYTHON) -c "import clang_format, os; print(os.path.join(os.path.dirname(clang_format.__file__), 'data', 'bin', 'clang-format').replace(os.sep, '/'))" 2>/dev/null)

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
# the first-boot stamp's block (wipeOnFreshInstall), just below the journal;
# ld refuses a link whose code grows into it
FRESH_INSTALL_BASE = 0x85F00

USB_FLAGS = -DNRF52840_XXAA {build.flags.usb} -DCFG_TUD_HID=$(CFG_TUD_HID) \
	-DCFG_TUD_TASK_QUEUE_SZ=$(CFG_TUD_TASK_QUEUE_SZ) \
	-DOPK_RF_JOURNAL_BASE=$(RF_JOURNAL_BASE) $(EXTRA_FLAGS)
LINK_FLAGS = -Wl,--wrap=tud_vendor_control_xfer_cb \
	-Wl,--wrap=tud_descriptor_string_cb \
	-Wl,--section-start=.rf_journal_guard=$(RF_JOURNAL_BASE) \
	-Wl,--section-start=.fresh_install=$(FRESH_INSTALL_BASE)

# The guide drive carries the Windows Guide. On Windows the build compiles it
# with the .NET Framework's own csc and embeds it; elsewhere, or with
# LIZARD_DRIVE=0, the firmware builds without the drive.
LIZARD_DRIVE ?= 1
GUIDE_H = $(SKETCH)/guide_exe.h
CSC ?= /c/Windows/Microsoft.NET/Framework64/v4.0.30319/csc.exe

# The install file is a UF2. The firmware wipes the pairing on its first boot
# after an install (wipeOnFreshInstall); the dev UF2 carries that block already
# stamped, and keeps the pairing.
ARDUINO_DATA := $(shell p="$$(arduino-cli config get directories.data)"; \
	command -v cygpath >/dev/null && cygpath -m "$$p" || echo "$$p")
UF2CONV = $(firstword $(wildcard \
	$(ARDUINO_DATA)/packages/adafruit/hardware/nrf52/*/tools/uf2conv/uf2conv.py))
OBJCOPY = $(firstword $(wildcard \
	$(ARDUINO_DATA)/packages/adafruit/tools/arm-none-eabi-gcc/*/bin/arm-none-eabi-objcopy*))
HID_H = $(firstword $(wildcard \
	$(ARDUINO_DATA)/packages/adafruit/hardware/nrf52/*/libraries/Adafruit_TinyUSB_Arduino/src/class/hid/hid.h))
UF2_FAMILY = 0xADA52840
HEX =$(OUTPUT_DIR)/$(SKETCH).ino.hex
ELF = $(OUTPUT_DIR)/$(SKETCH).ino.elf
UF2_RELEASE = $(OUTPUT_DIR)/$(SKETCH).uf2
UF2_DEV = $(OUTPUT_DIR)/$(SKETCH)-dev.uf2

.PHONY: need-clang-format format format-check check build guide-header \
	flash deploy keymap tune

need-clang-format:
	@[ -n "$(CLANG_FORMAT)" ] || { echo "clang-format 18 not found"; exit 1; }

format: need-clang-format
	$(CLANG_FORMAT) -i $(FORMAT_FILES)

format-check: need-clang-format
	$(CLANG_FORMAT) --dry-run --Werror $(FORMAT_FILES)

check: format-check
	$(PYTHON) tools/check-trailing-comments.py $(FORMAT_FILES)

guide-header:
ifeq ($(LIZARD_DRIVE)$(OS),1Windows_NT)
	mkdir -p build/guide
	powershell -NoProfile -ExecutionPolicy Bypass -File tools/quit-guide.ps1
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
	# arduino-cli never drops a deleted or renamed source from its copy of
	# the sketch, and would link it in; the core stays cached
	rm -rf $(BUILD_PATH)/sketch
	arduino-cli compile -b $(FQBN) --build-path $(BUILD_PATH) \
		--output-dir $(OUTPUT_DIR) \
		--build-property "build.extra_flags=$(USB_FLAGS)" \
		--build-property "compiler.c.elf.extra_flags=$(LINK_FLAGS)" \
		$(SKETCH)
	# copied first: the Microsoft Store Python can't read the Arduino data dir
	cp "$(UF2CONV)" build/uf2conv.py
	$(PYTHON) build/uf2conv.py -f $(UF2_FAMILY) -c -o $(UF2_RELEASE) $(HEX)
	head -c 256 /dev/zero > build/stamped.bin
	"$(OBJCOPY)" -O ihex --update-section .fresh_install=build/stamped.bin \
		$(ELF) build/dev.hex
	$(PYTHON) build/uf2conv.py -f $(UF2_FAMILY) -c -o $(UF2_DEV) build/dev.hex

# make flash KEEP_BONDS=1 keeps the puck's bonds (dev only)
flash:
	powershell -NoProfile -ExecutionPolicy Bypass -File tools/flash.ps1 \
		"$(if $(filter 1,$(KEEP_BONDS)),$(UF2_DEV),$(UF2_RELEASE))"

deploy: build flash

# Tuning a running puck without a reflash, until it reboots: the key map as
# the source has it now, or a feel constant (SET="name value"; no SET lists
# them all)
keymap:
	powershell -NoProfile -ExecutionPolicy Bypass -File tools/tune.ps1 \
		-Keymap -HidHeader "$(HID_H)"

tune:
	powershell -NoProfile -ExecutionPolicy Bypass -File tools/tune.ps1 \
		-Lines "~T $(SET)"
