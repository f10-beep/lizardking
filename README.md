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

It is opinionated on purpose: one layout, no settings, no profiles. It
behaves the same in games, on the desktop, at the Windows login screen and in
admin prompts.

- Design and behaviour: [docs/DESIGN.md](docs/DESIGN.md)
- Keyboard layout: [docs/KEYBOARD_LAYOUT.md](docs/KEYBOARD_LAYOUT.md)
- What's next: [docs/ROADMAP.md](docs/ROADMAP.md)
- Working on the code: [AGENTS.md](AGENTS.md), [CODE_MAP.md](CODE_MAP.md)

## Hardware

An nRF52840 board running the Adafruit bootloader (the same boards
[OpenPuck](https://github.com/safijari/openpuck) supports), paired with a
Steam Controller 2.

## Build and flash

Needs [arduino-cli](https://arduino.github.io/arduino-cli/) with the Adafruit
nRF52 core. On Windows the build also embeds the Guide, compiled with the
.NET Framework's own C# compiler.

```sh
make build
make flash PORT=COM12
```

## Pairing

Pairing goes through Steam. Hold the four back buttons and press **A**: the
puck reboots into Steam mode. With the controller and the puck both plugged
into the same PC and Steam running, pair as you would with Valve's puck.
Back four + **B** (or a replug) returns to LizardKing.

## Credits and licence

LizardKing builds on [OpenPuck](https://github.com/safijari/openpuck), whose
reverse-engineered RF link, bonding and USB groundwork it carries over; see
[NOTICE](NOTICE). Licensed under the AGPL-3.0, as OpenPuck is.
