# Roadmap

## Finishing the port

The first import kept the build green and the product behaviour unchanged.
What's left of OpenPuck's user-facing configurability, and the code that only
served it, goes next, one piece at a time, each followed by a simplification
pass.

- **Steam mode down to pairing** (low priority). `puck_hid` still carries
  upstream's full Steam presentation and its lizard fallback (`mode_lizard`,
  `lizard_map`, `MODE_LIZARD`). Trimming it is complicated and risky and
  buys little but flash space; leave it unless it gets in the way.
- **Pairing without Steam.** Steam writes the bond to both the puck and
  the wired controller. Capturing what it sends the controller would let
  the Guide pair by itself and Steam mode go away entirely.
- **Watchdog hang capture.** `WDT_IRQHandler` lacked `extern "C"`, so it
  never replaced the vector and the hang capture never ran (upstream too).
  Fixed in the import; confirm on hardware that a watchdog reset still
  reports cleanly.
- **USB ID.** Get a pid.codes ID before publishing; `1209:0001` is the
  test ID.
- **First CI run.** The build workflow has only been checked locally (a
  fresh-clone build); watch its first run on GitHub.

## Product

Launch feature (in the first release): gyro aiming.

- **Gyro aiming** (launch): built, with the right pad (docs/DESIGN.md,
  Gyro aiming). Tune the sensitivity curve and smoothing by hand in games
  before release.
- **Lift-off protection.** Lightening the fast-swipe smoothing (follow 30%)
  gave stronger flicks but slightly weaker lift-off protection; retune.
- **Scroll momentum.** Tune the fling threshold and the coast by feel.
- **Clicks.** Keep lowering the click threshold until it misfires.
- **Keyboard.** Tune zones and texture.
- **Pad pressure in the Guide's key map:** a subtle meter on each pad with
  notches at the click thresholds.
- **Steam Input on.** Test games with Steam Input on and off: the pad
  should pass through like any Xbox 360 pad, and the pads and gyro keep
  driving the mouse. Steam's desktop layout doubling the sticks as a mouse
  is possible in theory but never showed up with heavy keyboard-and-mouse
  mappings on a Steam Controller and an Xbox Elite.
- **Long drags.** With the two-pad drag gone, a drag longer than one pad
  swipe leans on the gyro (lifting the thumb lets go of the click). Check
  it's enough in practice.
- **Media next / previous.** Play/pause works; next and previous track
  (the standard consumer usages) did nothing in YouTube, even with a
  queue. Compare with a keyboard's media keys to see whose problem it is.
- **LizardKing when wired.** A controller turned on while plugged into the
  PC by USB connects there directly, as a Steam Controller, and the puck
  never hears it: LizardKing is gone until the cable comes out. The trap
  is charging from the PC: turn the controller off (Guide + Back) and on
  again, and it's suddenly a Steam Controller. At least the Guide could
  notice the wired controller (USB `28DE:1302`) and say to unplug it;
  better, make LizardKing work wired.
- **Keeping a tuned key map or constant through a reboot.** The console
  already changes both live (`~K`, `~T`); saving them to the puck's flash
  would let an owner keep a customization. Likely a popular request once
  LizardKing is out; not needed for our own tuning, so deferred.
