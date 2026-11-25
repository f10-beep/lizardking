# One controller, several pucks

Notes from the first look (November 2026), for picking it up again. The
goal: one controller used with PCs in different rooms, a puck in each,
without carrying a puck around.

## What we know

Measured on two nice!nano boards and one Steam Controller 2:

- **A bond is two IDs plus the controller's serial**
  (docs/PROTOCOL.md, Bond record). The two IDs travel in clear in every
  discovery beacon; they are how the controller recognises its puck. The
  serial is only a label (the Guide uses it to spot the controller on a
  cable).
- **A copied bond just works.** A second puck holding the same 24 bytes
  took the controller with no re-pairing, both ways. Each puck derives its
  own session address from the bond and its chip ID, so two pucks never
  collide on the air.
- **The controller keeps one bond.** Pairing a puck in Steam writes new
  IDs to the controller and the puck; every other puck is cut off (it
  searched forever near the old puck). Re-pairing can never add a puck.
- **Which puck wins is not settled.** 4 ft apart, turning the controller
  on next to one: 2 of 2 went to the nearer; then about 15 tries mostly
  stayed with the main puck; another 15 mostly alternated; later it stayed
  with the last puck through 5 power cycles until it lay on the other
  puck. Our best guess: the controller first resumes its last session
  (the puck it left keeps beaconing on its session channel and address),
  and searches the shared channel only when that fails.
- **A puck goes silent for 2.5 s after its controller powers off**
  (rf_link.cpp, the post-disconnect cooldown), so a quick off/on can hand
  the other puck a head start. It didn't explain the results above.
- **A searching controller ignores Guide + Back:** the chord is the
  puck's, and no puck is listening.
- **Steam mode has no serial port**, and the mode is picked once in
  `setup()` from the bond count. The USB presentation can be swapped in
  place (`usbPresent()` detaches and reattaches), no reboot needed.
- **Two pucks on one PC confuse the Guide:** it stays on the first port
  that opens, even the puck without the controller. Harmless with a puck
  per room.

## Copying a bond today (dev only)

`BD` on a paired puck's console prints each bond as a line for
`LizardKing/seed_bond.h` (gitignored: it holds a controller's bond). Build
and install the release UF2 on the new puck; on its first boot it wipes,
finds no bond and loads the seed. Then `make build` again without the file.

## Ideas, with their trade-offs

Choosing the puck:

- **Let the controller choose** (what happens today). No code. Probably
  right for rooms: the last puck keeps the controller while it's in range,
  and the near puck takes it once the old one is out of reach. Weak spot:
  two pucks in range of each other, where the choice looked arbitrary.
- **Pucks arbitrate by signal strength.** On a new connection each puck
  broadcasts its ID and the controller's strength on the discovery
  channel; the weaker one drops the controller and stays quiet a few
  seconds, so it reconnects to the stronger. Deterministic where pucks
  hear each other, falls back to today's behaviour where they don't.
  Costs a frame type the controller must ignore (untested) and a second
  of reconnect when it first chose the far puck.
- **A release chord.** The puck with the controller lets go and ignores it
  for a few seconds, so it lands on another. Simple and deterministic for
  two pucks; with more, repeated presses cycle through them (a quiet
  window long enough for the next press). A puck that gave the controller
  away waits a moment before answering at the next power-on, so the last
  used wins. Costs a chord and knowing where it landed (a toast, a buzz).
- **Targeted handoff** ("give it to C" broadcast between pucks). Picks a
  specific PC, but needs a list of PCs on screen and a frame the
  controller ignores. Not worth it for two rooms.
- **Re-pairing on a cable at the PC you want.** Plugging in at a PC would
  make it the controller's (the Guide sees the controller on USB, the puck
  swaps to Steam mode in place, Steam pairs, the puck swaps back on the
  first new bond or a timeout). Deterministic and exclusive, works for any
  number of PCs, and fits the one-bond controller. But it needs a cable at
  every desk, Steam running, and a Steam-mode trip on every switch; set
  aside for the copy-and-choose approach above.
- **Flashing the controller** to remember several pucks: Valve's firmware,
  overwritten by Steam's updates, and a brick risk. Ruled out.

Copying the bond to a new puck:

- **Over the air, on a Guide chord.** The puck with the controller
  broadcasts its bond for about 30 s; an unpaired puck nearby (on any USB
  power) takes it and reboots into LizardKing. Puck-only, nothing new
  revealed (the IDs are already in every beacon). Costs a chord and
  radio code, and an unpaired puck must listen while in Steam mode.
- **Through the Guide** (both pucks on one PC, the Guide copies over
  serial). Easy, but the first essential feature to need the Guide.
- **Pairing without Steam** (roadmap): the Guide writes a bond to the
  wired controller itself, once we capture what Steam sends. Removes Steam
  mode, but is the larger reverse-engineering job.
- **Pairing each puck in Steam.** Disproved: one bond per controller.
- **A puck serving a copy of its own firmware** on its drive. Dropped:
  only a developer would use it, and the release UF2 already does it.

## Next time

1. **Two rooms** (the upstairs PC): walk between rooms turning the
   controller off and on; also walk away until the link drops, to see
   whether it moves by itself.
2. **Log the connects** on both pucks (`# LC ... CONNECT` lines are
   already printed; the Guide must be closed to read the port). The time
   from power-on to connect should tell a resumed session from a fresh
   search, which tests the "last puck first" guess.
3. **Add the controller's signal strength** (`g_linkRssi`) to the connect
   line, to see how far apart the pucks' views of it are.
4. Before building any arbitration or over-the-air copy: check that the
   controller ignores an unknown frame type on the discovery channel, and
   that an unpaired puck can hear another puck's frames there.
