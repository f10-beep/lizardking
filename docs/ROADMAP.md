# Roadmap

## Finishing the port

The first import kept the build green and the product behaviour unchanged.
What's left of OpenPuck's user-facing configurability, and the code that only
served it, goes next, one piece at a time, each followed by a simplification
pass.

- **Settings file cleanup.** `cfg.bin` stays: it is where persisted
  toggles (gyro on/off) and dev tunables set from the console (rumble
  strength and style, sleep power-off) live, and every install wipes it,
  so tuning never leaks into a release. Drop the fields nothing reads or
  sets any more: the per-type button remaps (`etypeForMode`, `g_type`,
  `gamepad_util`, always `ET_NONE`), the pad-to-stick mapping and the
  Steam Machine flag. Every install wipes the file, so its layout needn't
  stay compatible.
- **Steam mode down to pairing.** `puck_hid` still carries upstream's full
  Steam presentation and its lizard fallback (`mode_lizard`, `lizard_map`,
  `MODE_LIZARD`). Keep what Steam needs to pair and, if it works out, drop
  the rest.
- **Pairing without Steam.** Steam writes the bond to both the puck and
  the wired controller. Capturing what it sends the controller would let
  the Guide pair by itself and Steam mode go away entirely.
- **Diagnostics.** `fault_diag`, `rf_diag`, `haptics` and `serial_console`
  still record and expose data that only the removed WebUSB panel read
  (flight recorder vitals, capture rings, RF survey and replay tooling).
  Keep what the serial console or a crash post-mortem uses; drop the rest.
- **Watchdog hang capture.** `WDT_IRQHandler` lacked `extern "C"`, so it
  never replaced the vector and the hang capture never ran (upstream too).
  Fixed in the import; confirm on hardware that a watchdog reset still
  reports cleanly.
- **ColdBoot** (`pwr_switch`, compiled out by default): keep or drop.
- **USB ID.** Get a pid.codes ID before publishing; `1209:0001` is the
  test ID.
- **CI build.** CI checks formatting and the comment lint; add a firmware
  build.

## Product

- **Gyro aiming**, as mouse movement: active while the right thumb rests on
  the right stick (lift to ratchet), in the gamepad layer only. One binary
  on/off gesture, Guide + right stick click, persisted across power
  cycles, confirmed by two bumps for on and one for off, and shown in the
  Guide next to battery and signal. Try it in a shooter and in Elite
  (tilt to look) before settling.
- **Stick-touch glitch.** Touching a pad and the same side's stick drops to
  the gamepad layer: the controller seems to stop reporting the pad touch
  while the stick's capacitive sensor is touched (bits 0x00100000 right,
  0x01000000 left stick). Capture what it reports, then keep the layer.
  Gyro aiming depends on stick touch, so fix this first.
- **Lift-off protection.** Lightening the fast-swipe smoothing (follow 30%)
  gave stronger flicks but slightly weaker lift-off protection; retune.
- **Scroll momentum.** Tune the fling threshold and the coast by feel.
- **Clicks.** Keep lowering the click threshold until it misfires.
- **Keyboard.** Tune zones and texture.
- **Pad pressure in the Guide's key map:** a subtle meter on each pad with
  notches at the click thresholds.
- **Steam Input on.** Make the pads work with Steam Input enabled.
