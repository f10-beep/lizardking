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
- **USB ID.** Get a pid.codes ID before publishing; `1209:0001` is the
  test ID.
- **First CI run.** The build workflow has only been checked locally (a
  fresh-clone build); watch its first run on GitHub.

## Product

Launch feature (in the first release): gyro aiming.

- **Gyro aiming** (launch): built, with the right pad (docs/DESIGN.md,
  Gyro aiming). Felt right in Jumpspace once used to it; try another gyro
  shooter, then tune the curve and smoothing with a wider group, existing
  gyro players among them, before release (`make tune` sets them live).
- **Lift-off protection.** Lightening the fast-swipe smoothing (follow 30%)
  gave stronger flicks but slightly weaker lift-off protection; retune.
- **Scroll momentum.** Tune the fling threshold and the coast by feel.
- **Clicks.** Keep lowering the click threshold until it misfires.
- **Keyboard.** Try the rim layout by feel and settle it: the pages, the
  letters the search placed, where the punctuation goes (unscored), the
  corner width and the center. Then flash it.
- **The Guide saying why it can't draw.** When its windows can't get the
  GPU (an odd driver, composition refused) they stay blank and retry every
  2 s with no sign of it. Put the reason in the tray tooltip and menu, so
  a report says what failed.
- **The Guide's drawing on another PC.** Its windows reach the screen
  through DirectComposition at the realtime GPU class (docs/DESIGN.md,
  LizardKing Guide), measured so far on one AMD integrated GPU, Windows
  11 build 26300 (Insider) and Elite behind Firefox; there, a token with
  no privileges at all is granted realtime. On the next PC (NVIDIA or
  Intel, a release Windows build) re-run `make bench`, pick a game to
  test over, and measure the game with the Guide open over it, which
  uploads its whole picture about 60 times a second while the controller
  moves.
- **Keeping a tuned key map or constant through a reboot.** The console
  already changes both live (`~K`, `~T`); saving them to the puck's flash
  would let an owner keep a customization. Likely a popular request once
  LizardKing is out; not needed for our own tuning, so deferred.

Settled, so not to be reopened without a new reason: Steam Input works on
its default settings (its Guide chords take real effort to trigger); long
drags reach far with the pad and gyro; the pads' click thresholds already
show where clicks are; next / previous track send the standard usages
(play/pause works), so a report would be the first sign of trouble; a
controller turned on wired stays wired (the Guide says to unplug and turn
it back on), while one plugged in once on keeps LizardKing; the face
buttons are the desktop's (back, forward, Enter, Esc), and the back
buttons alone are the gamepad's A/B/X/Y.
