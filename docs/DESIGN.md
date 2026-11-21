# LizardKing design

The product behaviour, implemented mostly in `LizardKing/mode_eliz.cpp`.

Goal: one fixed, predictable layout that behaves the same everywhere: in
games, on the desktop, at the Windows login screen and in admin (UAC)
prompts. There is no Steam handoff and nothing changes by context. Which
trackpads are touched is the only thing that picks the layer.

## USB

One composite device, pid.codes test ID `1209:0001`:

| Interface | What | Why |
|-----------|------|-----|
| 0 | Xbox 360 XInput pad (interface 0 of the genuine pad) | the most widely supported gamepad |
| 1 | HID keyboard + mouse + media keys | needs no driver, so it works at login and in UAC prompts |
| 2–3 | CDC serial | console, plus the Arduino 1200-baud reset so `arduino-cli upload` / `make flash PORT=COMx` work |
| 4 | Mass storage: read-only guide drive | carries the Windows Guide to any PC (see Guide drive) |

- Windows' Xbox driver (`xusb22.inf`) only binds a composite interface
  through the compatible ID `USB\MS_COMP_XUSB10`, which interface 0
  advertises with MS OS 1.0 descriptors (string `0xEE` + extended compat ID).
- There is no WebUSB interface: it would raise `bcdUSB` to 2.1, and Windows
  would then read MS OS 2.0 instead of the 1.0 descriptor above.
- A published build needs its own USB ID from pid.codes.

## Boot and mode switching

- The puck always boots into LizardKing.
- Back four + **A** reboots into Steam mode, which exists only for pairing:
  Steam pairs a controller through the puck's own HID interface. Back four
  + **B**, or the next reboot or replug, returns to LizardKing.
- A **tap of all four back buttons** (a clean squeeze: all four down
  within 150 ms of the first, from none held, in one layer; then all up,
  nothing else pressed in between) cycles the Guide's mode when it is
  running, and otherwise shows or hides the guide drive; a bump on both
  pads confirms.
  It fires on release, so it never collides with the mode chords.
  Back four + **Start** asks the Guide to install itself; Start doesn't
  reach the PC while the back buttons are held. Best done with the thumbs
  off the pads: in the mouse and keyboard layers the back buttons are
  Shift / Ctrl / Win / Alt.
- In those layers all four together would be Windows' Office key (it opens
  the Microsoft 365 / Copilot app), so the first back button's modifier
  waits up to 150 ms for the rest. All four: the gesture, and no modifier
  reaches Windows. Anything else pressed, a back button let go, a layer
  change, or the time running out sends the held modifiers at once, so
  Shift + key still works at speed. They keep the layer they were pressed
  in: Ctrl + Shift pressed with a thumb on a pad stay Ctrl + Shift after
  it lifts, until let go. The gamepad layer gets no delay.
- Steam Input must be off for games that should see the Xbox pad directly
  (per game: Properties → Controller → Disable Steam Input).

## Layers

| Pads touched | Layer |
|--------------|-------|
| none | gamepad |
| one | mouse |
| both | keyboard |

- A layer change takes effect after 30 ms, so a brushed pad doesn't
  flicker the layer.
- **A held button keeps the meaning it had when pressed** until released.
  Sticks and triggers in use follow the same rule. Pad clicks use the
  undebounced layer, so a quick tap still counts as a mouse click.
- Sticks and triggers do nothing in the mouse and keyboard layers yet.

| Button | Gamepad layer | Mouse / keyboard layers |
|--------|---------------|-------------------------|
| R4 / L4 / R5 / L5 | A / B / X / Y | Win / Shift / Alt / Ctrl |
| A / B / X / Y | A / B / X / Y | mouse: PgDn / End / Home / PgUp |
| D-pad | D-pad | mouse: arrow keys |
| LB / RB | LB / RB | Tab / Enter |
| LT / RT (light pull) | LT / RT | Backspace / Space |
| View / Menu / Steam | Start / Back / Guide | mouse: Menu key / Esc |
| L3 / R3 | L3 / R3 | mouse: middle click / Back button |
| Left stick | left stick | mouse: up/down volume, left/right prev/next track |
| QAM | Print Screen | mouse: play/pause |
| Right pad click | — | left click |
| Left pad click | — | right click |

The back buttons carry the primary functions because they are the easiest
to reach. In the desktop layers they are the modifiers, so Shift and Ctrl
can be held with the left hand while the right thumb drives the cursor.

## Keyboard layer (prototype)

- Active while both thumbs touch the pads. Entering plays a dull tone on
  both pads (−12 dB); leaving plays a softer one (−18 dB).
- Each pad is a 3×3 grid of equal thirds. A thumb has to go 1,300 pad
  counts past a zone's edge to leave it, so it doesn't flicker on borders.
  - **Center:** inert. No key, no click pulse, no texture.
  - **Side zones (N/E/S/W):** a texture tick (−24 dB) per 2,500 counts
    of travel.
  - **Every zone change:** a bump (−12 dB), felt at the actual hysteresis
    switch point.
  - **Outer edge:** the same bump on entering a band 26,000 counts from
    center, because the sensor can lose a thumb still on the pad.
  - **Corners:** no texture.
- Sliding with a click held:
  - **Center to an outer zone:** types that zone's key.
  - **Held key into the center:** releases the key, with its release pulse.
  - **Held key into another key zone:** releases the key and types nothing
    until the thumb returns to the center.
- Clicking one of a pad's 8 outer zones types the key for that zone
  combined with the zone the other thumb rests in (8 × 9 per pad, 144 in
  all). Keys repeat while the click is held, using the OS key repeat.
- Layout: `docs/KEYBOARD_LAYOUT.md`. Vowels on the left thumb, consonants
  on the right; numbers with the other thumb resting down; F-keys with it in
  the bottom outer corner; brackets with the thumbs spread outward.
- Clicking both center zones together moves the key map overlay between
  two thirds and one third of the screen height (a bump on press and on
  release; both clicks stay inert until released). A single center click
  still does nothing.
- Key clicks pulse for twice as long as mouse clicks (3,000 / 1,400 µs);
  0x81 has no gain setting.
- Index fingers (mouse and keyboard layers): RT = Space, LT = Backspace
  (a light pull: on at 24/255, off at 12, with a click pulse on that side),
  RB = Enter, LB = Tab. Back buttons stay Shift / Ctrl / Alt / Win.

## LizardKing Guide

`guide/LizardKingGuide.cs`: a single-file C# program for the .NET
Framework 4.x that ships with Windows 10/11, so it needs nothing installed.
`make build` compiles it with the framework's own `csc` (or run
`guide/build.cmd`) into `build/guide/LizardKingGuide.exe`, about 45 KB.
The name is the start of a rebrand; the firmware, USB device and repo are
still LizardKing.

Three modes, cycled by a tap of the four back buttons, each announced by a
brief word in the middle of the screen:

- **Guide:** a large click-through window. No pad and one pad on the left;
  both pads on the right, with the live key map under it. Each panel draws
  the controller with every button labelled for that layer; the layer the
  thumbs select is outlined and the others fade. The outlined panel
  responds live: pressed buttons fill and their labels light up, stick caps
  move in their rings, triggers fill as they are pulled, and a dot follows
  each thumb on its pad. The faded panels stay still, and the key map shows
  its keys only while both thumbs are down. "Back 4: switch mode" sits
  in the corner, and until the Guide is installed "Back 4 + Start to
  install" sits top center.
  While it is up the Guide is a practice space: the controller's buttons,
  wheel and keys go to the Guide only, never to the apps behind it (the
  puck sends the PC "nothing pressed"); only the cursor still moves, so it
  can be moved off whatever it covers. A key, click or wheel turn from
  another keyboard or mouse puts the Guide away (Key map mode); the
  controller's own buttons, Esc included, never do. "Back 4: switch mode"
  is a solid button in the corner, so the way out is hard to miss.
  Switching layers, the panel entered lights up at once and the one left
  fades back over 80 ms, which takes the edge off quick switches.
- **Key map:** only the keyboard overlay, in a small titled panel, shown
  while both thumbs are on the pads: both pads' 3x3 grids for wherever the
  other thumb rests, each thumb's zone highlighted. Small faded glyphs
  around each key show what it becomes if the other thumb steps one zone
  that way; new glyphs grow out of their hints (60 ms); the zone a thumb
  leaves fades back over 80 ms while the one entered lights at once;
  letters show lower
  case and Shift shows shifted forms; a typed key's cell flashes and fades
  over 75 ms. Both pad centers clicked together move it between one and
  two thirds of the screen height; its title bar says so. Each pad's grid
  has rounded outer corners, like the pad itself.
- **Hidden:** nothing on screen.

How it works:

- The controller drawing is built in code from measurements of the real
  controller (outline from a dozen landmarks joined by Bezier curves and
  mirrored; buttons, sticks and pads at measured positions). Bumpers and
  triggers are drawn lifted off the shoulders; back buttons, on the
  underside, dashed and pulled out from under the pads.
- It reads the puck's serial port. The key table comes from the firmware
  (`~m`), so it always matches what's flashed. `KS layer zoneR zoneL
  shift` arrives whenever the layer, a thumb's zone or Shift changes; `KK
  pad zone` for every typed key; `KG` on a back 4 tap; `KI` on back 4 +
  Start; `KT connected battery charge signal` about once a second; `KB
  buttons lt rt lx ly rx ry lpx lpy rpx rpy` for the live
  drawing (buttons in hex, triggers 0..31, sticks and pads -32..32, sent
  on a change at most every 8 ms). The button labels themselves are
  written into the Guide, which repaints at most about 60 times a
  second.
- It finds the puck by its USB ID (`1209:0001`) in the registry and talks
  to the port with plain Win32 calls; .NET's `SerialPort` can crash the
  process from its own thread when the device is unplugged.
- Serial lines are handled the moment they arrive; the frame timer runs
  only while an animation or flash is on screen, with the Windows timer
  resolution raised to 1 ms.
- The Guide and the key map share one title bar (same code, same look):
  title, then a status chip with the controller's battery (a bolt while
  charging, amber at 15% and below) and signal bars, or "Controller
  asleep"; a middle slot (the install prompt until installed, or the key
  map's "Both centers: move"); and the solid "Back 4: switch mode" button
  on the right. The tray tooltip gives the status in words.
- Practice mode is held by a heartbeat: every 250 ms the Guide's window
  thread asks for the input (`~g`), and the puck keeps it from the PC only
  while those keep coming (at most a second apart) and the port is open,
  so a Guide that hangs, crashes or closes hands the controller back on
  its own. Leaving Guide mode sends `~h` to hand it back at once.
- A tray icon shows the connection and offers the three modes, Move key
  map, Install or Start when I sign in and Uninstall, and Exit.
- One instance at a time: a newly started copy (an update, a dev build)
  asks the running one to quit and takes over the serial port. `--demo`
  cycles fake states without a puck and runs alongside a real copy;
  `--mode guide|keymap|hidden` picks the starting mode.
- Only one program can hold the serial port, so quit the Guide (tray >
  Exit) before flashing over serial.

## Guide drive

The puck carries the Guide so it can be opened on any PC, without working
around any of Windows' safeguards:

- The puck is also a USB drive (interface 4) that reports "no disk", like a
  card reader without a card; Windows hides empty drives by default.
- A tap of the four back buttons, with no Guide running, shows a read-only
  volume, `LIZARDKING`, with `LizardKing Guide.exe` and a short
  `README.txt`. The same tap hides it again, and unplugging the puck resets
  it to hidden. No PC is ever shown the drive uninvited.
- Nothing runs by itself and nothing is installed unless asked. Started
  from the drive, the Guide copies itself to a temporary folder and runs
  from there (the drive can be hidden while it is open), in Guide mode.
  Back 4 + Start, or the tray icon, asks to install for this Windows user:
  a copy in `%LOCALAPPDATA%\Programs\LizardKing` and an ordinary startup
  entry in `HKCU\...\Run`, visible in Settings > Apps > Startup. Nothing
  needs admin rights. Uninstall from the tray icon removes both.
- The volume is synthesized on the fly (`LizardKing/eliz_drive.cpp`): a FAT12
  boot sector, FAT and root directory computed from the README and the exe
  embedded at build time as `LizardKing/guide_exe.h` (generated with `xxd`,
  gitignored). Writes are refused.
- `make build LIZARD_DRIVE=0` leaves the drive out entirely, as do builds
  on machines without the .NET Framework (CI). `~d` on the console shows or
  hides it.

The letter layout proposal is in `docs/KEYBOARD_LAYOUT.md`.

## Cursor: a virtual trackball

- **While touching,** the cursor chases the thumb's absolute (1:1)
  position, closing a fraction of the gap each report:
  - all of it (exactly 1:1) at thumb speeds below about 250 px/s;
  - easing to 30% per report above about 2,000 px/s.

  Fast swipes are smoothed, but once the thumb slows the cursor lands where
  1:1 would put it, so it never drifts.
- **Thumb speed** (what sets the smoothing) is measured per *new* pad
  position. The pad refreshes the position only every 2nd–3rd report and
  repeats it in between, so speed = travel since the last new position ÷
  the real time since then. Repeats are skipped; no new position for 16 ms
  means the thumb is still. Taken per report, the repeats read as stops.
- **On lift,** the ball takes the cursor's smoothed velocity and glides with
  light friction (×0.9835 per report, minus 0.006 px). The remaining gap is
  dropped.
- **Touching a moving ball stops it.** Smoothing then starts from the ball's
  speed, so a tap's jitter cannot fling it.
- **Lift dead zone:** a thumb rolling off drops pressure fast. Each count
  per report of drop opens 0.1 px of dead zone (up to 40 px), which
  shrinks by 20% per report, so a thumb pressing back down is followed 1:1
  again. While it is open the launch velocity updates more slowly (half as
  fast at 2 px), so a settled lift launches nothing.
- **Flick tuning, reverted to this baseline on 2026-11-20 to re-test one at
  a time:** paying out the lagged gap after lift (felt good), a 25% faster
  base speed (cost precision), launching from the thumb's velocity, a
  velocity dead zone instead of the launch freeze, pressure-weighted
  launch, and launching at the 40 ms peak (strong flicks, but broke
  lift-off protection). Earlier dead ends: rewinding the last reports
  (hard step, could miss), smoothing on falling pressure (loosened 1:1),
  speed-dependent glide friction (not worth the complexity).
- **Entering the keyboard layer** brakes the ball like a resting finger
  (×0.88 per report) instead of stopping it dead.
- **Pressure dead zone:** the cursor ignores thumb travel within a radius
  set by pressure: 10 px at zero pressure (a feather touch, about to lift),
  shrinking to 0 by 400, then growing back to 10 px at 2,200 (below the
  click). It stays at 10 px for the whole click, so presses, releases and
  light lift-offs land exactly where aimed. Past the radius the cursor is
  pulled along at its edge, so drags work.
- **Drag with both pads:** while a pad click is held as a mouse button,
  touching the other pad does not open the keyboard; both pads move the
  cursor, so one thumb holds the button and the other steers.
- **Circular scroll (left pad):** one turn = 24 notches, clockwise scrolls
  down. Starting a scroll takes turning past a dead zone: a half turn at
  20% of the pad radius, shrinking to 0 at 65%, so a click near the center
  never starts one and the outer ring starts at once. Until it starts, the
  wheel follows the thumb, so a scroll begins where the thumb reaches the
  outer ring rather than replaying the way out from the center. Once
  started, the scroll follows the thumb anywhere, inward too, until it
  lifts; only the innermost 10%, where the angle is noise, pauses it. A
  texture tick (−24 dB) every ⅔ notch (36 per turn).
- **Wheel reports at most every 16 ms:** fast scrolling goes out as fewer,
  larger steps rather than a report every 4 ms, which some apps stutter
  on. A step too big for one report, or what is left when the wheel
  stops, goes out at once.
- **Scroll momentum** is built like the trackball's: while the thumb
  turns the wheel, its speed is measured per new pad position over the
  real time since the previous one (repeated positions aren't stops; 16 ms
  without one is) and smoothed (×0.35 per sample, less while the pressure
  falls as the thumb rolls off). A single reading over 3× the current
  speed is capped. Lifting at more than 0.12 notches per report lets the
  wheel coast, slowing ×0.996 per report until under 0.008; a thumb back
  on the left pad catches it, and slow scrolling stops dead.
- **Smooth scrolling:** the mouse exposes the HID Resolution Multiplier
  (1..120), so Windows reads the wheel in 1/120 notches.

What did not work, so it isn't tried again:

- Velocity damping by pressure: it killed swipe momentum.
- Slowing the follow rate by pressure: the cursor crept while clicks were
  held, and precision did not improve.
- Pressure-based grip on the ball: about 1 in 6 light swipes read zero
  pressure.
- Lift-off freezes, touch-down settling, precision (acceleration) scaling
  and rules for carrying momentum over: each misread intent or fought the
  others.

## Clicks

- The firmware decides pad clicks from pressure: **on at 3,200, off at
  2,300**, and lighter while both thumbs are on the pads (typing): on at
  2,400, off at 1,700. The controller's own click needs about 8,900, which
  tires the thumb.
- Pressure readings on report 0x42/0x45 are the s16 after each pad's x/y
  (`s16off` 20 for left, 26 for right; 0x47: 22 and 28). Measured: 0
  untouched, about 150–2,000 for a light touch, 16,000–18,000 for a full
  squeeze. Some light swipes read 0 throughout.

## Haptics

- **Click:** a sharp 0x81 HAPTIC_PULSE, 1,500 µs on press and 700 µs on
  release. Pulse side 0 = right pad.
- **Movement:** a faint 0x83 HAPTIC_LFO_TONE (−24 dB, 200 Hz, 20 units) per
  24 px of cursor travel, touching or gliding, and per scroll notch. Tone
  side 0 = **left** pad, the reverse of 0x81.
- **Keyboard layer on:** a full-strength 0x83 tone (0 dB, 200 Hz, 30
  units) on both pads, deliberately dull so it never reads as a click.
- 0x81 on-time changes only length; 0x83 gain sets strength.
- The controller's own pad ticks are switched off in this mode (`id9 = 0`):
  they are as strong as a click.

## Serial console

`~` commands, for tuning:

| Command | Sends |
|---------|-------|
| `~p side on_us off_us repeat` | 0x81 HAPTIC_PULSE |
| `~c side command gain_db` | 0x82 HAPTIC_COMMAND |
| `~t side gain_db freq dur lfo depth` | 0x83 HAPTIC_LFO_TONE |
| `~s side script gain_db` | 0x85 HAPTIC_SCRIPT |
| `~k setting value` | SET_SETTINGS_VALUES |
| `~r` | toggles a one-line-per-report trace of the trackball |
| `~m` | dumps the keyboard key table (`KM` lines) |
| `~d` | shows / hides the guide drive |
| `~g` | Guide practice mode: hold the host's input for another second |
| `~h` | Guide practice mode over: hand the input back now |

## Next

See [ROADMAP.md](ROADMAP.md).
