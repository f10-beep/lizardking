# LizardKing

Firmware for an nRF52840 puck that turns a Steam Controller 2 into a complete
PC input device, plus the LizardKing Guide, a Windows companion that teaches
the layout.

What your thumbs touch picks the layer:

| Thumbs on the pads | Layer |
|---|---|
| none | Xbox gamepad |
| one | mouse (a virtual trackball, with scrolling) |
| both | keyboard |

It is opinionated on purpose: it arrives with one confident design, one
layout and one way to do each thing, and never hands you a setting to decide
what we should have decided ourselves. It behaves the same in games, on the
desktop, at the Windows login screen and in admin prompts.

- Design and behaviour: [docs/DESIGN.md](docs/DESIGN.md)
- Keyboard layout: [docs/KEYBOARD_LAYOUT.md](docs/KEYBOARD_LAYOUT.md)
- What's next: [docs/ROADMAP.md](docs/ROADMAP.md)
- Working on the code: [AGENTS.md](AGENTS.md), [CODE_MAP.md](CODE_MAP.md)

## Hardware

An nRF52840 board running the Adafruit bootloader (the same boards
[OpenPuck](https://github.com/safijari/openpuck) supports), paired with a
Steam Controller 2.

## Install

1. Download `LizardKing.uf2` from the latest release.
2. Double-tap the board's reset button: a drive appears. Copy the file onto
   it. The board restarts unpaired.
3. Plug the puck and the controller into the PC and, with Steam running,
   pair as you would with Valve's puck. Pair every controller you want now.
4. Unplug the puck and plug it back in: it starts as LizardKing.
5. Hold the controller's Guide (Steam) button for half a second: the guide
   drive appears. Start `LizardKing Guide.exe` from it to learn the layout.

Lost or replaced a controller? Install again: every install starts unpaired.

## Build

Needs [arduino-cli](https://arduino.github.io/arduino-cli/) with the Adafruit
nRF52 core. On Windows the build also embeds the Guide, compiled with the
.NET Framework's own C# compiler.

```sh
make build                # build/out/LizardKing.uf2 and LizardKing-dev.uf2
make flash                # install LizardKing.uf2 on the puck
make flash KEEP_BONDS=1   # the dev UF2: keeps the puck's pairing
```

## Credits and licence

LizardKing builds on [OpenPuck](https://github.com/safijari/openpuck), whose
reverse-engineered RF link, bonding and USB groundwork it carries over; see
[NOTICE](NOTICE). Licensed under the AGPL-3.0, as OpenPuck is.
