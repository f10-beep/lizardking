# AGENTS.md

## What this is

LizardKing is firmware for an nRF52840 puck (OpenPuck hardware) paired with
a Steam Controller 2, plus the LizardKing Guide (a Windows companion app). It
turns the controller into a complete PC input device: gamepad, mouse and
keyboard in fixed layers chosen by what your thumbs touch.

It is deliberately **opinionated**. The product makes the decisions at design
time so the user never has to:

- **No configuration.** No settings pages, no profiles, no web configurator,
  no tuning gestures. If a behaviour needs a knob, the design isn't finished.
- **One way to do each thing.** No emulation modes, no alternate layouts.
- **A toggle is not configuration**, but it must stay strictly binary and its
  state must be visible (haptic feedback and the Guide). The gyro on/off
  gesture is the only one; adding another needs a very good reason.

When a change would add an option, find the right default instead.

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
make build    # firmware compile (no hardware needed)
make flash PORT=COM12
make check    # format-check + trailing-comment lint
make format   # apply kernel style
```

`make check` needs clang-format 18 on the PATH; CI runs it on every push.

Generated headers are gitignored and never committed:
`LizardKing/git_version.h` (from `gen_version.sh`) and `LizardKing/guide_exe.h`
(the Guide exe embedded for the guide drive, built from `guide/` on Windows;
`make build LIZARD_DRIVE=0` builds without it).

## Flashing

Flashing interrupts the user's controller. Don't reflash for trivial or
cosmetic changes; batch them with a real one. Commit and push only when asked.
