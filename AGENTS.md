# AGENTS.md

## What this is

LizardKing is firmware for an nRF52840 puck (OpenPuck hardware) paired with
a Steam Controller 2, plus the LizardKing Guide (a Windows companion app). It
turns the controller into a complete PC input device: gamepad, mouse and
keyboard in fixed layers chosen by what your thumbs touch.

### Opinionated by design

LizardKing meets the user with a confident, finished design. **We never use
configuration to hand a design decision to the user.** If we can't decide how
something should behave, the design isn't done, and an option would only move
our unfinished work onto them.

The rule is about what the user is asked, not about locking the product down:

- **Decided, not offered.** One layout, one way to do each thing: no
  emulation modes, no profiles, no settings screens. When a change seems to
  need an option, find the right default instead.
- **Switches can be part of the design.** A few strictly binary switches
  belong to the product because the right answer depends on the moment, not
  on the person: the Guide, the key map and where it sits, gyro aiming. Each
  is a Guide chord, confirmed on the pads and shown in the Guide. Adding
  another needs a strong case.
- **Tuning is our tool, not theirs.** Constants, persisted state
  (`cfg.bin`) and console commands exist so we can find the right values
  and hold them. None of it is presented to the user as a choice, and every
  install starts from the shipped defaults.

The test for any change: does it ask the user to decide something we should
have decided? If so, decide it.

## How we work: working, then simple

The rule above all others: **working is halfway; simple is done.**

Get it working first, and prove it. Then simplify what you touched. An
issue comes before either: understand it and fix it, and only then simplify,
because simplifying around a problem you don't yet understand hides it.
Nothing is published (handed over as a commit, pushed, or flashed for use)
until it has had its last simplification pass.

A simplification pass means, on everything the change touched:

- delete dead code, unused parameters, stale state and leftover debug aids;
- collapse abstractions that have one user, and special cases the general
  path already covers;
- remove options, flags and branches the product no longer needs;
- prefer fewer, clearer names and less indirection;
- re-read the comments against the rules below;
- keep the behaviour identical, then re-verify (`make check`, `make build`,
  and the behaviour itself when it can be tested).

Report what the pass removed or changed. "Nothing to simplify" is a valid
result only after actually looking.

## Provenance and licence

The RF link, radio, bonding and USB groundwork come from
[OpenPuck](https://github.com/safijari/openpuck) and are AGPL-3.0, so this
whole project is AGPL-3.0. Keep the original copyright notices in files that
carry OpenPuck code, and keep `NOTICE` current when code is carried over or
substantially rewritten. Carry over only what this product needs; leave
everything that serves configurability or other emulation modes behind.

## Code style

- **Linux kernel style**, enforced by `.clang-format` (8-wide tabs, 80
  columns). Pinned to **clang-format 18**: other versions reformat
  differently.
- The Guide is C# 5 built with the .NET Framework's own `csc` (no SDK, no
  NuGet), so it builds on any Windows machine.
- New tools: C# when it costs about the same; Python is fine when it's
  clearly the practical choice.

## Comments

Comment to explain **why**, not **what**: the code already says what it does.
This codebase drives a reverse-engineered RF/HID protocol, so the comments
worth writing capture knowledge the code can't show on its own.

Write a comment only when it carries genuine nuance:

- the meaning of a magic value or protocol constant,
- a hardware / host-OS quirk or gotcha,
- a non-obvious constraint or consequence ("lower than 96 truncates the
  battery report → CRC fail"),
- why a non-obvious approach was chosen over the obvious one.

Delete a comment if it:

- restates the code (`i++; // increment i`),
- narrates history or changelog ("was 64, bumped to 96"),
- editorializes ("THE fix", "the magic part"),
- leaves debug / A-B-test breadcrumbs.

Keep the constraint, drop the story. Prefer no comment to an obvious one;
when tempted to annotate, first see if clearer code makes the comment
unnecessary.

**No long trailing comments.** clang-format can't relocate a trailing `//`
comment, so a long one either splits the statement or runs past 80 columns.
Put it on its own line(s) **above** the statement, with a blank line before:

```c
// 0xE7 selects the protocol-version handshake; 0xE3 is the steady-state poll
uint8_t g_connType = 0xE7;
```

Short trailing comments that fit within 80 columns are fine.

## Build and check

```sh
make build    # firmware and both UF2s (no hardware needed)
make flash    # installs the release UF2: wipes the pairing
make flash KEEP_BONDS=1   # the dev UF2: keeps the pairing
make check    # format-check + trailing-comment lint
make format   # apply kernel style
```

`make check` needs clang-format 18: on the PATH, or from PyPI
(`pip install clang-format==18.1.8`, which the Makefile finds by itself).
CI runs it on every push.

CI also builds the firmware on Windows (the Guide needs the .NET Framework's
`csc`) on every push to `main` and every pull request, and keeps the UF2 as
an artifact. To release, tag the commit with a plain version and push the
tag: `git tag 1.0.0 && git push origin 1.0.0`. CI then attaches
`LizardKing.uf2` to a GitHub release, and the firmware reports that version.

Generated headers are gitignored and never committed:
`LizardKing/git_version.h` (from `gen_version.sh`) and `LizardKing/guide_exe.h`
(the Guide exe embedded for the guide drive, built from `guide/` on Windows;
`make build LIZARD_DRIVE=0` builds without it).

## Flashing

Flashing interrupts the user's controller. Don't reflash for trivial or
cosmetic changes; batch them with a real one. Use `KEEP_BONDS=1` unless the
point is to test the install path: a release flash means pairing again in
Steam. `make flash` reaches the puck over the serial port (it asks a running
Guide to quit first), and an unpaired puck (Steam mode, no serial port)
needs a double-tap of reset first. Never upload over serial DFU (`arduino-cli
upload`): it records a firmware checksum that the first-boot stamp then
breaks (docs/DESIGN.md, Installing). Commit and push only when asked.
