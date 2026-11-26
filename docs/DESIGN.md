# LizardKing design

The product behaviour, implemented mostly in `LizardKing/mode_lizardking.cpp`.

Goal: one fixed, predictable layout that behaves the same everywhere: in
games, on the desktop, at the Windows login screen and in admin (UAC)
prompts. There is no Steam handoff and nothing changes by context. What the
thumbs touch is the only thing that picks the layer.

Every decision here is made for the user, never handed to them as a setting
(see "Opinionated by design" in `AGENTS.md`).

## USB

One composite device, pid.codes test ID `1209:0001`:

| Interface | What | Why |
|-----------|------|-----|
| 0 | Xbox 360 XInput pad (interface 0 of the genuine pad) | the most widely supported gamepad |
| 1 | HID keyboard + mouse + media keys + Sleep / Power | needs no driver, so it works at login and in UAC prompts |
| 2–3 | CDC serial | the Guide's link and the console (`~b` reboots into the UF2 bootloader for `make flash`) |
| 4 | Mass storage: read-only guide drive | carries the Windows Guide to any PC (see Guide drive) |

- Windows' Xbox driver (`xusb22.inf`) only binds a composite interface
  through the compatible ID `USB\MS_COMP_XUSB10`, which interface 0
  advertises with MS OS 1.0 descriptors (string `0xEE` + extended compat ID).
- There is no WebUSB interface: it would raise `bcdUSB` to 2.1, and Windows
  would then read MS OS 2.0 instead of the 1.0 descriptor above.
- A published build needs its own USB ID from pid.codes.

## Installing, pairing and boot

- **One install path: copy a UF2 onto the bootloader drive.** Every
  install, including reinstalling the same file, starts unpaired: the image
  carries a 256-byte block (`.fresh_install`) that the install writes blank,
  and the first boot after it wipes the internal file system (bonds and
  settings) and stamps the block. `make flash` installs the same way: `~b`
  reboots the puck into its UF2 bootloader and the file is copied onto the
  drive. `make flash KEEP_BONDS=1` copies a dev UF2 that carries the block
  already stamped, so the bonds survive.
- The bootloader refuses UF2 writes to the file system itself (the 40 KB
  below it is reserved for app data), hence the stamp. Stamping the image
  works because a UF2 install records no app checksum. A serial DFU upload
  (`arduino-cli upload`) records one, which the stamp then fails, leaving
  the puck in its bootloader until the next UF2 install: don't use it.
- **The bonds decide the boot.** With no bond the puck boots into Steam
  mode, which exists only for pairing: plug the puck and the controller
  into the PC and pair in Steam, as with Valve's puck. Pair every
  controller in the same session; replug the puck and, with a bond, it
  boots into LizardKing.
- **The LED shows the link.** The puck's user LED glows faintly while it
  has the controller (with a puck in each room, the one in use) and
  flashes bright when it wakes the PC. A bright blinking blue LED on
  SuperMini boards is the charger's, with no battery attached; firmware
  can't reach it.
- **Lost the controller? Reinstall.** Recovery is the install path; there
  is no other mode switch.
- Steam Input must be off for games that should see the Xbox pad directly
  (per game: Properties → Controller → Disable Steam Input).

## The Guide button

The controller's Steam button is the Guide button, and it sits a level
above the rest: it reaches LizardKing before Windows.

Only a press with no thumb on a pad, from start to finish, opens anything.
With a thumb on a pad the Guide button is a chord about to happen, and a
thumb landing mid-press is one on its way, where opening Big Picture or the
Guide by accident is easy. Opening the Guide is a hands-off moment
anyway: you stop to read.

- **Tap** (released within 400 ms, nothing else pressed), no pad touched:
  Windows gets a normal Xbox Guide press, so Game Bar and the Steam
  overlay still open, just on release.
- **Medium press** (held past 400 ms, alone), no pad touched: LizardKing's
  own, and it acts the moment the hold passes 400 ms, so the Guide or the
  drive appearing is the sign it worked; holding on changes nothing. With
  the Guide running it opens the Guide; without it, it shows or hides the
  guide drive. Never forwarded.
- **While the Guide is up,** any Guide press that isn't a chord, short or
  long, in any layer, puts it away.
- With a thumb on a pad, and the Guide closed, a Guide press that isn't a
  chord does nothing.
- **Chords** (Guide held, then another button): LizardKing's only commands.
  Neither button reaches Windows.

| Chord | Does | Needs the Guide running |
|-------|------|-------------------------|
| Guide + left pad click | key map large / small / off | yes |
| Guide + right pad click | key map up / down | yes |
| Guide + Back | the controller off, as Steam's "turn off controller" | no |
| Guide + Start | install the Guide | yes |

- A switch confirms on the pads: two bumps for on, one for off. Moving the
  key map and asking to install bump once. A chord that needs the Guide,
  pressed without it, bumps once and does nothing.
- The back buttons are plain buttons: no gestures, no delay. In the
  keyboard layer all four together send Ctrl + Shift + Alt + Win, which
  Windows reads as the Office key.

## Layers

Two layers, and every hand position means one thing:

| Thumbs on | Means |
|-----------|-------|
| both pads | keyboard |
| anything else | controller: an Xbox gamepad whose pads are the mouse |

- **The pads are always the mouse** outside the keyboard: the right pad
  moves the pointer, with the gyro (see Gyro aiming), and its click is the
  left button; the left pad scrolls and its click is the middle button.
  Right click is QAM, between the pads, so the right thumb reaches both
  main buttons.
- **The buttons are always the gamepad's** outside the keyboard, so a
  button never changes meaning mid-game, except the face buttons: they sit
  beside the pointer and are always the desktop's. X and Y are back and
  forward (M4, M5); A and B are Enter and Esc, confirm and cancel as in
  every gamepad menu, so a press out of habit does what it meant. The
  gamepad's A/B/X/Y are the back buttons.
- **Both pads: keyboard.** The back buttons become the modifiers, the
  triggers Space and Backspace, the bumpers Tab and Delete, and the pads
  type (see Keyboard layer).
- **Modifiers reach the mouse by being held.** A held button keeps the
  meaning it had when pressed until released, so Ctrl or Shift pressed with
  both thumbs down stays held when the left thumb lifts: Ctrl-click,
  Shift-drag. Alt-Tab works within the keyboard layer.
- A click held into the keyboard stays held, as any held button does, so
  a drag can take a modifier midway: land the left thumb, press it on a
  back button, lift the left thumb and carry on dragging. Long drags lean
  on the gyro, since lifting the right thumb lets go of the click.
- A layer change takes effect after 30 ms, so a brushed pad doesn't
  flicker the layer. Entering the keyboard plays a dull tone on both pads,
  leaving it a softer one.
- Sticks and triggers in use keep their meaning across a layer change, as
  held buttons do. Pad clicks use the undebounced layer, so a quick tap
  still counts as a mouse click.

| Button | Controller layer | Keyboard layer |
|--------|---------------|----------------|
| R4 / L4 / R5 / L5 | A / B / X / Y | Win / Shift / Alt / Ctrl |
| A / B | Enter / Esc | — |
| X / Y | back / forward (M4 / M5) | — |
| D-pad | D-pad | — |
| LB / RB | LB / RB | Tab / Delete |
| LT / RT | LT / RT | Backspace / Space (a light pull) |
| View / Menu | Start / Back | — |
| Steam | the Guide button (see above) | chords only |
| QAM | right click (M2) | — |
| Right pad | pointer, with gyro | types |
| Right pad click | left click (M1) | types |
| Left pad | scroll | types |
| Left pad click | middle click (M3) | types |

The arrows, the page keys and the media keys are key map keys (see
Keyboard layer). Typing reaches Enter by lifting the right thumb to A, or
as the key map's numpad Enter.

The back buttons carry the primary functions because they are the easiest
to reach. In the keyboard layer they are the modifiers, so Shift and Ctrl
can be held with the left hand while a thumb lifts to drive the cursor.

## Gyro aiming

- **Gyro is part of the right pad:** while the thumb is on it, outside the
  keyboard, the trackball and the gyro both move the pointer, as on the
  original Steam Controller. The pad covers big moves, the gyro fine aim;
  lifting the thumb pauses both, so the aim ratchets. A game that wants
  WASD gets it from Steam Input on the gamepad's left stick, and still
  takes our mouse.
- No switch: the thumb on the pad is the switch. Gyro's dead zones keep
  the pointer still while the controller is merely held.
- Not tied to the right stick's touch, as many gyro setups are: that suits
  games where the stick and the gyro aim the same thing, and the right pad
  is a better aim than a stick.
- Turning is measured about gravity ("player space", as in JoyShockMapper),
  so it feels the same however the controller is tilted in the hands;
  pitch is the controller's own tilt.
- Sensitivity rises with speed: 8 cursor counts per degree up to 15°/s,
  for careful aim, easing to 20 at 75°/s and above, so a fast swing still
  turns far. Below 5°/s the input blends into the average of the last 8
  reports (32 ms), fading out as speed rises: hand tremor is steadied, and
  deliberate movement isn't lagged.
- Gyro shares the trackball's pressure and lift dead zones (the same radius
  at the same moment), because pressing, clicking and rolling off the pad
  tilt the controller too. It takes none of the trackball's smoothing or
  momentum: lifting the thumb stops it dead, which is the ratchet.
- The controller's gyro reads 16.4 counts per degree per second and drifts
  a few counts: whenever it isn't aiming and sits still, the bias follows
  it, so the cursor doesn't creep.
- The puck asks the controller for raw IMU samples on every connect in
  LizardKing (Steam can leave them switched off, and the setting persists
  in the controller).

## Keyboard layer (prototype)

- Active while both thumbs touch the pads. Entering plays a dull tone on
  both pads (−12 dB); leaving plays a softer one (−18 dB).
- A pad's keys are the 8 places along its rim, found blind. A thumb finds
  a corner by pressing into it, because two walls stop it there. It lands
  on a side directly, since the side is long along the rim, or slides
  along the rim from a corner to the bump. The center has no wall on
  either axis to find it by, so it holds no key and only the rarest page.
  - Zones are angles from the pad's center: each corner is a 30° window on
    its diagonal, each side the 60° between. Closer in than 12,000 pad
    counts (on the larger axis) is the inert center: no key, no pulse.
  - A thumb has to go 1,300 pad counts past a border to leave a zone, so
    it doesn't flicker on borders.
  - **Every zone change:** a bump (−12 dB), felt at the actual hysteresis
    switch point. It is the one cue a sliding thumb counts.
  - Easiest to find first: the top inner corner, the top side, the top
    outer corner, the inner side, the bottom inner corner, the bottom
    side, the bottom outer corner, the outer side, and last the center.
- A click types the key for its zone combined with the zone the other
  thumb rests in (8 × 9 per pad, 144 in all). Keys repeat while the click
  is held, using the OS key repeat. Sliding off the zone releases the key,
  and nothing more types until the click lifts.
- A click in the center, or in a zone with no key for where the other
  thumb rests, is dead: no pulse and nothing typed, so the thumb feels
  that nothing is there.
- Layout: `docs/KEYBOARD_LAYOUT.md`. Vowels on the left thumb, consonants
  on the right, E and R on the home corners. Home is the other thumb in its top inner corner, the
  rarer letters on its top side, and navigation (right pad) and media
  (left pad) in its top outer corner. The three number pages sit
  together: numbers on its inner side, F-keys in its bottom inner corner,
  the numpad in the center. The remaining keys, with Sleep and Power, are
  in its bottom outer corner, and the bottom and outer sides are free.
- Key clicks pulse for twice as long as mouse clicks (3,000 / 1,400 µs);
  0x81 has no gain setting.
- Index fingers (keyboard layer): RT = Space, LT = Backspace
  (a light pull: on at 24/255, off at 12, with a click pulse on that side),
  RB = Delete, LB = Tab. Back buttons stay Shift / Ctrl / Alt / Win.

## LizardKing Guide

`guide/LizardKingGuide.cs` and `guide/Gpu.cs`: a C# program for the .NET
Framework 4.x that ships with Windows 10/11, so it needs nothing installed.
`make build` compiles it with the framework's own `csc` (or run
`guide/build.cmd`) into `build/guide/LizardKingGuide.exe`, about 75 KB.

Look: lizard greens (olive-tinted darks, an anole-green accent, a lime
highlight; low battery stays amber), with a faint pattern of rounded scales
inside the Guide's panels only, generated in code and kept a couple of
levels off the surface so it never competes with text; window backgrounds,
keys and chips stay plain. The tray icon is the controller's own outline in
green with the pads cut in dark, so it reads as a face, wearing a gold
crown; it still reads at 16 px.

The Guide teaches the chords; the chords are the only interface. Nothing on
screen is clickable: every window is click-through. Two independent
switches, both on the Guide button:

- **The Guide** (medium press on, any Guide press off): a large window.
  The gamepad on the left; the keyboard on the right, with the live
  key map under it. Each panel draws the controller with every button
  labelled for that layer; the layer the thumbs select is outlined and the
  others fade. The outlined panel responds live: pressed buttons fill and
  their labels light up, stick caps move in their rings, and a dot follows
  each thumb on its pad; a pad lights only while it is clicked. The Guide
  shows what the PC gets, not the raw input: in the gamepad panel each pad
  is a surface that moves exactly as far as the mouse it makes (dots
  rolling with the pointer on the right, ribs turning with the wheel on the
  left), and the thumb is only a faint ring. So the trackball's glide, its
  pressure and lift dead zones, and the gyro all show as the one motion
  they are. Fast motion would strobe (a cell's step in one frame reads as
  no step), so each mark is blurred by its step since the last frame, as a
  camera's shutter would, and an inner zone at 1:1 is ringed by a band
  zoomed out 4x, which still reads when the inner one is a blur. A surface
  is lit while its thumb is on the pad, not while it moves: the output
  comes in bursts too quick to light by. A held button stays lit in the panel of the layer
  it was pressed in, as it keeps acting there, even after the thumbs move
  on. Triggers fill as they are pulled in the controller panel; in the
  desktop panels they are keys (Space, Backspace) and fill on or off at the
  firmware's thresholds. The key map shows its keys only while both thumbs
  are down; a zone with no key stays blank, like the inert centers. A cheat sheet lists the Guide button's gestures and chords;
  each says what it will do now ("hide key map", "key map up"), and lights
  up when it is used. The Guide button is labelled in the controller panel
  only, the one layer where it does more than chords.
  While it is up the Guide is a practice space: nothing from the
  controller reaches the apps behind it, cursor included (the puck sends
  the PC "nothing pressed"). The Guide parks the cursor in a screen corner
  while it is up and puts it back when it goes away. A key, click or wheel
  turn from another keyboard or mouse, software-made ones like Steam
  Input's included, also puts it away, as a safety net; so do the
  controller disconnecting (the Guide button can't reach it then) and a
  click on the tray icon. A controller turned on while plugged into the PC
  (to charge, say) skips the puck and talks to the PC as a wired Steam
  Controller: when the Guide program sees that (the puck hearing no
  controller, and the paired one, by the serial in its bond, on USB as
  `28DE:1302`) it says "Unplug the controller and turn it back on" for six
  seconds, once each time. Guide + Back turns the controller off only once
  both are released: a Guide button still down would wake it straight back
  up, wired.
  It stays light over a game: no animations, the window edge, panels and
  texture drawn once and copied under each repaint, and input repainting
  only the panel it changes, at most 60 times a second.
- **The key map** (Guide + left pad click: large, small, off): the
  keyboard overlay alone. Large is a titled panel,
  shown while the Guide is down and both thumbs are
  on the pads: both pads' 3x3 grids for wherever the other thumb rests,
  each thumb's zone highlighted. Small faded glyphs around each key show
  what it becomes if the other thumb steps one zone that way; letters show
  lower case and Shift shows shifted forms. It repaints only when a thumb
  changes zone or Shift changes; a typed key's confirmation is the
  controller's click. Small, the expert's, is each zone's own key alone in cells with
  half the padding, and the battery over the signal between the pads (no
  percentage): no hints, title or legend. Guide + right pad click moves it
  between one and two thirds of the screen height. Both key map chords, used with the
  Guide closed, are announced with a brief word in the middle of the screen
  ("Key map small", "Key map top"). Each pad's grid has rounded outer
  corners, like the pad itself.

The puck owns both switches and the key map position (in RAM, so a reboot
starts with the key map on and low); the Guide follows what the puck
reports.

How it works:

- The controller drawing is built in code from measurements of the real
  controller (outline from a dozen landmarks joined by Bezier curves and
  mirrored; buttons, sticks and pads at measured positions). Bumpers and
  triggers are drawn lifted off the shoulders; back buttons, on the
  underside, dashed and pulled out from under the pads.
- It reads the puck's serial port. The key table comes from the firmware
  (`~m`), so it always matches what's flashed. `KS layer zoneR zoneL
  shift` arrives whenever the layer, a thumb's zone or Shift changes; `KG`
  on a Guide on/off press; `KI` on
  Guide + Start; `KT connected battery charge signal keymap low dev
  serial` about
  once a second and at once when a switch changes; `KB
  buttons lt rt lx ly rx ry lpx lpy rpx rpy own0 own1 mx my mw` for the
  live drawing (buttons in hex, triggers 0..31, sticks and pads -32..32,
  the held buttons each layer owns in hex, the mouse output's travel
  wrapping at 16 bits, sent on a change at most every
  8 ms). The button labels themselves are
  written into the Guide, which repaints at most about 60 times a
  second.
- It finds the puck by its USB ID (`1209:0001`) in the registry and talks
  to the port with plain Win32 calls; .NET's `SerialPort` can crash the
  process from its own thread when the device is unplugged.
- Serial lines are handled the moment they arrive, and only the newest
  state and input wait for the window thread, never a queue of them: a
  window thread held up catches up in one step instead of replaying the
  past. A window redraws once whatever has changed: what changes while
  it draws joins the next picture, so the Guide following the controller
  draws about 60 times a second, as fast as drawing goes, with no timer
  of its own.
- Every window draws with GDI+ into a bitmap and hands it to the
  compositor through its own Direct3D swap chain and DirectComposition
  visual (`guide/Gpu.cs`), never through the window's GDI surface.
  Windows copies a GDI surface to the compositor on the GPU at no
  priority of the app's, and with a game holding an integrated GPU behind
  a browser (Elite behind Firefox) a key map change took 1-10 s to show,
  or never did. The swap chain's copy is the Guide's own GPU work, asked
  at the realtime GPU scheduling class (high where that's refused): over
  the same game a zone change reaches the screen in about 50 ms; what it
  puts ahead of the game is one copy per change. Measured with PresentMon
  over fullscreen Elite, a key map changing every 110 ms cost the game
  about 1 fps of 59 and left it in independent flip. A GPU lost to a
  driver reset is let go and reopened within about 2 s, the windows blank
  meanwhile. Without a hardware GPU (a VM, the basic display driver) it
  draws on Windows' software one. Opacity is the compositor's, and the
  rounded corners are transparent pixels. A window that's down draws
  nothing, though the puck streams input and status all the while.
- `make bench` checks that with no puck and no one typing, the Guide's own
  windows on this PC read back off the screen: an overlay shows at its
  opacity and lets clicks through, and the key map keeps up with zone
  changes. Run it with what was slow in front.
- The Guide and the key map share one title bar (same code, same look):
  title, then a status chip with the controller's battery (a bolt while
  charging, amber at 15% and below) and signal bars, or "Controller
  asleep"; and a middle slot with the install chord until installed.
- Practice mode is held by a heartbeat: every 250 ms the Guide's window
  thread asks for the input (`~g`), and the puck keeps it from the PC only
  while those keep coming (at most a second apart) and the port is open,
  so a Guide that hangs, crashes or closes hands the controller back on
  its own. Putting the Guide away sends `~h` to hand it back at once.
- The tray icon is a signpost, not a second interface: its tooltip gives
  the status ("LizardKing: 72%, strong signal"), and clicking it shows a
  hint card above the tray for five seconds: the top of the controller
  with the Guide button lit and an arrow pointing at it, and "Hold the
  Guide button on the controller for more info." ("on the controller"
  tells a newcomer where to look). Like the toast, it is click-through. A
  Windows notification can't carry a picture without registering the app
  with the Start menu, and doubled the app's name in its header. Its menu holds only what has no chord:
  Uninstall (while the per-user copy exists) and Quit.
- One instance at a time: a newly started copy (an update, a dev build)
  asks the running one to quit and takes over the serial port. `--demo`
  cycles fake states without a puck and runs alongside a real copy.

## Guide drive

The puck carries the Guide so it can be opened on any PC, without working
around any of Windows' safeguards:

- The puck is also a USB drive (interface 4) that reports "no disk", like a
  card reader without a card; Windows hides empty drives by default.
- A medium press of the Guide button, with no Guide running, shows a
  read-only volume, `LIZARDKING`, with `LizardKing Guide.exe` and a short
  `README.txt`. The same press hides it again; the Guide connecting or the
  puck being unplugged hides it too. No PC is ever shown the drive
  uninvited.
- Nothing runs by itself and nothing starts at sign-in unless asked.
  Started from the drive, the Guide copies itself to
  `%LOCALAPPDATA%\Programs\LizardKing` (replacing an older copy, which it
  asks to quit first) and runs from there, with the Guide up. Guide +
  Start asks to install for this Windows user, which only adds an ordinary
  startup entry in `HKCU\...\Run`, visible in Settings > Apps > Startup.
  One path on purpose: Windows keeps an entry in Settings' list of tray
  icons for every path an icon ever came from. Nothing needs admin rights.
  Uninstall from the tray icon removes the folder and the entry.
- The volume is synthesized on the fly (`LizardKing/guide_drive.cpp`): a FAT12
  boot sector, FAT and root directory computed from the README and the exe
  embedded at build time as `LizardKing/guide_exe.h` (generated with `xxd`,
  gitignored). Writes are refused.
- `make build LIZARD_DRIVE=0` leaves the drive out entirely, as do builds
  on machines without the .NET Framework (CI). `~d` on the console shows or
  hides it.

The letter layout proposal is in `docs/KEYBOARD_LAYOUT.md`.

## Cursor: a virtual trackball

- **Speed:** a swipe across the whole pad moves the pointer 630 counts,
  the speed of Steam Input's desktop default (measured with
  `tools/mousecount`), and a released flick glides about as far as Steam's.
  Steam has no acceleration, so neither do we: the pointer's travel is the
  thumb's, at any speed. The controller's own mouse, with Steam closed, is
  slower (510).
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
  a time:** paying out the lagged gap after lift (felt good), launching
  from the thumb's velocity, a velocity dead zone instead of the launch freeze, pressure-weighted
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
  24 px of cursor travel and per 2/3 scroll notch, touching or gliding. Tone
  side 0 = **left** pad, the reverse of 0x81.
- **Keyboard layer on / off:** a 0x83 tone (200 Hz, 30 units) on both
  pads, −12 dB on and −18 dB off, deliberately dull so it never reads as a
  click.
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
| `~y` | toggles a trace of the IMU: `Y us gx gy gz ax ay az` per fresh report, raw |
| `~w` | toggles a trace of the scroll wheel: `W` per report, and `WS units hires=0/1` per wheel report sent (1/120 notches while the host enabled the multiplier) |
| `~m` | dumps the keyboard key table (`KM` lines) |
| `~K pad zone k0..k7` | replaces a key table page, in hex as `KM` prints it, until reboot |
| `~T [name [value]]` | lists the feel constants, or shows or sets one by its source name, until reboot |
| `~b` | reboots into the UF2 bootloader (`make flash`) |
| `~g` | Guide practice mode: hold the host's input for another second |
| `~h` | Guide practice mode over: hand the input back now |

`~K` and `~T` let the key map and the feel constants (the `static int` /
`static float` LK_ values in `mode_lizardking.cpp`) be tried without a
reflash; `make keymap` pushes the source's key map and `make tune SET="name
value"` sets a constant (`tools/tune.ps1`). Nothing is saved: a reboot
restores what ships, and while anything differs the Guide's title reads
"dev overrides". A value that feels right goes into the source.

Plain commands, for tuning and diagnosis:

| Command | Does |
|---------|------|
| `SS` | one line: poll rate, fresh reports per second, lost replies |
| `PR<hz>` | RF poll rate for this session, 100–1000 Hz |
| `RS<pct>` | host-rumble strength, persisted |
| `RY<n>` | host-rumble style, persisted |
| `TR` | a test buzz at the current style and strength |
| `SO` | power the controllers off on host sleep, on or off, persisted |
| `UP` | reprints the boot line, with why the puck last reset (`watchdog` is a hang it recovered from) |

## Next

See [ROADMAP.md](ROADMAP.md).
