# LizardKing keyboard layout

Status: **applied** (`KEYMAP` in `LizardKing/mode_lizardking.cpp`).

## How the keyboard works (recap)

- Both thumbs on the pads = keyboard layer. Each pad is a 3×3 grid, and the
  center is inert.
- Clicking one of a pad's 8 outer zones types a key. Which key depends on
  where the **other** thumb is resting. That gives 16 click zones × 9
  resting zones = 144 keys.
- "Home" means the other thumb rests in its center. "The outward page"
  means it rests in its outer side zone (W for the left thumb, E for the
  right), and so on for the other zones.
- The pages follow a keyboard: numbers and F-keys on the top pages (N and
  the top outer corners); letters across the middle, the rarer ones a reach
  outward, as a keyboard's letter rows run wide; punctuation in the spare
  zones of the number and outward pages; the control keys on the bottom
  outer corners; the bottom and inward pages free. Every key a keyboard
  has is somewhere, here or on a button (Enter, Esc, Space, Backspace,
  Tab, Delete and the modifiers; see docs/DESIGN.md): the numpad on the
  top inner corners, the keys that are neither F-keys nor typing on the
  bottom inner corners with Sleep and Power.

## Letters: placed by letter pairs

`tools/layout/LayoutOpt.cs` scores a layout on how often each English letter
pair occurs (Peter Norvig's counts from about 3 trillion letters) and the
effort of the thumb moves to type it:

- Both thumbs move at the same time, so a step costs the larger of the two
  moves.
- A move costs effort per zone crossed, by direction, from the thumb's
  point of view: up 1.0, outward (toward the hand) 1.2, inward (toward the
  controller's center) 1.4, down 1.8. A diagonal combines its two parts
  like a distance.
- Using the same thumb twice in a row costs extra, because it has to
  release and press again.
- Resting the other thumb on a page zone costs a little extra.

It reads the shipped letters from `KEYMAP` in the firmware, scores them,
and searches the same pages for the best layout it can find, so any change
to the letters can be checked against it (see Reproducing).

The extra costs are judgment calls, so the original search, which counted
every direction alike, was run under four weightings. The best layouts all
shared a shape that the current search, with the directional efforts and
the rarer letters on the outward pages, keeps:

1. **All vowels plus Y on one pad;** consonants on the other pad. English
   alternates consonants and vowels, so the thumbs mostly take turns, which
   is the cheapest pattern. It is also the easiest to remember: one vowel
   thumb, one consonant thumb.
2. **T and H go on each pad's outer side zone, on the outward pages.** With
   both thumbs parked outward, each thumb's outer zone is both a key and the
   page selector for the other. So T, H, TH and HT need no movement at all.
3. The other common consonants (S N R C L P G B F) are single clicks; the
   rare letters sit on the outward pages.

Applied layout (vowels on the left thumb; the mirror image scores the
same), the best the search finds but for Q, moved off the home page so the
vowel pad keeps two zones for `,` and `.` (0.02% of the score):

```
other thumb CENTER (single clicks)        thumbs outward (the outward pages)

   left pad        right pad               left pad        right pad
   Y  O  .         G  N  C                 \  ;  '         Z  D  W
   E  ·  U         L  ·  S                 H  ·  Q         V  ·  T
   A  I  ,         B  R  F                 X  K  /         J  M  P
```

- The common word "the": right pad E (T, left thumb outward) → left pad W
  (H, right thumb outward) → left pad W again (E: the right thumb returns
  to center, the left doesn't move).
- The outward pages are lopsided, 4 letters to 8; the vowel side's spare
  zones hold the rest of the punctuation, `; '` on top, `\` in the corner
  and `/` below.

Things the score does not capture, worth judging by feel:

- T is the second most common letter, and it needs the left thumb resting
  outward. The model says that's worth it because of TH, HT, TE and ST, but
  it is the boldest choice here.
- Punctuation isn't scored; letters are all the counts cover.

## Everything else: placed to be memorable

**Numbers: other thumb resting in its top zone (N),** as the number row
tops a keyboard. The row's punctuation runs along the top, where a keyboard
has it: `` ` `` in the corner before 1, `- =` after 0, the brackets
mirrored between. The digits run in a U below: 1 down the left pad's outer
side, round the bottom to 5 on its inner side, then 6 on the right pad's
inner side round to 0 on its outer side. Each digit is a zone from the
next, and 5 and 6 face each other across the pads.

```
   left pad        right pad
   `  ·  [         ]  -  =
   1  ·  5         6  ·  0
   2  3  4         7  8  9
```

These are the main keyboard's keys, so Shift does what it does on a
keyboard (`1` gives `!`, `[` gives `{`). Each symbol in the map appears
once as its main-keyboard key; the numpad, which duplicates some, has its
own pages.

**F-keys: other thumb resting in its top outer corner** (right thumb NE for
the left pad, left thumb NW for the right pad), the row above the numbers.
F1–F10 sit exactly where 1–0 are, and F11 and F12 take the two top
centers:

```
   left pad          right pad
   ·    F11 ·        ·    F12 ·
   F1   ·   F5       F6   ·   F10
   F2   F3  F4       F7   F8  F9
```

So F5 is "5, with the other thumb slid out to the corner".

**The bottom pages (other thumb resting in its bottom zone, S) and the
inward pages (left thumb E, right thumb W)** are free. Both sit beside
Sleep and Power and stay empty there.

**The numpad: other thumb resting in its top inner corner** (right thumb
NW for the left pad, left thumb NE for the right pad), next to the number
pages and laid out like them, for Alt codes and games that bind the
numpad. Its operators run along the top, as they top a numpad: `/ * - +
.` and Enter across both pads.

```
   left pad        right pad
   /  *  −         +  .  ⏎
   1  ·  5         6  ·  0
   2  3  4         7  8  9
```

These are real numpad keys, so Windows treats them as it treats a
keyboard's: with Num Lock off, or with Shift held while it's on, 3 is Page
Down, `.` is Delete, and so on.

**The keys that aren't F-keys or typing: thumbs drawn in and down** (left
thumb SE for the right pad, right thumb SW for the left pad), a corner the
resting thumb finds blind. The right pad's top row is a keyboard's Print
Screen, Scroll Lock and Pause; the left pad mirrors it with Insert and Num
Lock. Caps Lock and the AI key (✦, Windows' Copilot key) take the outer
edges, Caps Lock as on a keyboard. There is no Menu key: Shift + F10 does
the same.

Sleep (⏾) is the left pad's SE zone and Power (⏻) the right pad's SW zone,
the pages' paired spots. Every other key on these pages is two zones from
them, and the pages beside leave those zones empty, so reaching either by
mistake takes more than one misstep. They also act only when held for a
second: the key map fills their cell meanwhile, and a bump says they have
acted. Let go or slide off sooner and nothing happens. Unlike other held
keys, anything else cancels the hold too: another button, the other pad's
click, the other thumb changing page or lifting. Windows treats them as the PC's own buttons: Sleep sleeps, Power
does what the power button is set to do.

```
   left pad              right pad
   ·     NumLk  Ins      PrtSc  ScrLk  Pause
   Caps  ·      ·        ·      ·      ✦
   ·     ·      ⏾        ⏻      ·      ·
```

**Control keys: other thumb resting in its bottom outer corner** (left
thumb SW for the right pad, right thumb SE for the left pad). The right pad
is a nav cluster: the arrows by direction, with Home / PgUp / End / PgDn in
the corners. The left pad puts play/pause top center,
tracks either side, and the volume keys along the bottom row:

```
   left pad            right pad
   ·     Play  ·       Home  ▲  PgUp
   Prev  ·     Next    ◀     ·  ▶
   Mute  Vol−  Vol+    End   ▼  PgDn
```

The d-pad stays the gamepad's outside the keyboard, so these keys live
here rather than on it.

**Still open:** whether you want the vowels on the right thumb instead.

## Reproducing

Save https://norvig.com/ngrams/count_2l.txt as `build\layout\count_2l.txt`
(the site turns away scripted downloads), then from the repository root:

```powershell
tools\layout\build.cmd
build\layout\LayoutOpt.exe
```

It prints the shipped layout's score, the best layout its search finds on
the same pages, and how much more the shipped one costs. Options set the
weights (`--same 1.0 --page 0.3 --up 1.0 --down 1.8 --out 1.2 --in 1.4` by
default) and the search (`--seeds 6 --iters 150000`); each seed takes about
a third of a second. `build.cmd` uses the C# compiler that ships with
Windows' .NET Framework.
