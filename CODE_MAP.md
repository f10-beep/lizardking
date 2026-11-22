# Code map

nRF52840, Adafruit Arduino core, TinyUSB. The puck talks to Steam Controller 2
controllers over a bare-metal nRF52 RADIO protocol (no SoftDevice) and
presents itself to the PC over USB. The RF protocol is documented in
[docs/PROTOCOL.md](docs/PROTOCOL.md); the product in
[docs/DESIGN.md](docs/DESIGN.md).

## Execution contexts

Every piece of shared state lives in one of these:

| Context | Priority / stack | What runs there |
|---|---|---|
| loop task | low priority, ~4 KB | `setup()`, everything `loop()` calls, and all radio code (polled from `rfLinkTask` / `rfDiagTask`; there is no radio ISR) |
| usbd task | high priority, ~800 B | TinyUSB callbacks: HID get/set report, the XInput class driver, vendor and string descriptor requests. The stack is tiny: a `Serial.printf` here can overflow it |
| ISR | exception | `HardFault_Handler`, `WDT_IRQHandler`, the black-box timer (`fault_diag.cpp`) |

Producers shared between contexts run under PRIMASK; the per-slot HID reply
rings are single-producer (usbd) / single-consumer (loop).

## Pipeline

```
radio.cpp            nRF52 RADIO: channels, addresses, raw TX/RX
rf_link.cpp          the controller protocol: discovery, bonding, the poll
                     loop, decoding input report 0x45 into g_in[slot]
                     (triton.h), relaying host commands
   │
   ▼  g_active->onReport45(slot, …)          (controllers.h)
mode_lizardking.cpp  LizardKing: layers (gamepad / mouse / keyboard by pad
                     touch), trackball cursor, scroll, keyboard zones, the
                     Guide button and its chords, the Guide's serial protocol
   ├─ mode_xinput.cpp   XInput class driver, bond selection, rumble
   ├─ HID keyboard + mouse + consumer interface (in mode_lizardking.cpp)
   └─ guide_drive.cpp   the read-only guide drive (synthesized FAT12)
puck_hid.cpp         Steam mode, for pairing: the Valve puck presentation
   └─ mode_lizard.cpp / lizard_map.cpp   its keyboard/mouse fallback
usb_tx.cpp           every device→host report drains from the loop, never
                     off it
usb_app_drivers      custom class drivers and the vendor / string descriptor
                     hooks (linker-wrapped, see the Makefile)
```

## Around it

| File | What |
|---|---|
| `LizardKing.ino` | `setup()` picks the mode from the bonds (none: Steam mode, to pair), presents USB and arms the ~8 s watchdog; `loop()` pumps each subsystem |
| `config.*` | the boot's USB presentation, the poll rate, `cfg.bin` (rumble and sleep tunables) and the first-boot wipe |
| `bonds.*`, `identity.*` | the four controller bond slots; the unit serial |
| `haptics.*` | host→controller haptic and rumble relay |
| `fault_diag.*` | reset classification, hang capture, flight recorder |
| `serial_console.*` | the CDC console: the Guide's `~` protocol, haptic tuning, and a few tuning and diagnosis commands |
| `wake_hid.*`, `status_led.*` | host wake, LED |
| `guide/` | the LizardKing Guide (C#, Windows) |
| `tools/flash.ps1` | `make flash`: reboots the puck into its UF2 bootloader and copies the UF2 |
| `tools/layout/` | the keyboard layout search |
