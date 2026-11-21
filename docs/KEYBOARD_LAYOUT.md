# LizardKing keyboard layout

Status: **applied** (`KEYMAP` in `LizardKing/mode_eliz.cpp`).

## How the keyboard works (recap)

- Both thumbs on the pads = keyboard layer. Each pad is a 3×3 grid, and the
  center is inert.
- Clicking one of a pad's 8 outer zones types a key. Which key depends on
  where the **other** thumb is resting. That gives 16 click zones × 9
  resting zones = 144 keys.
- "Home" means the other thumb rests in its center. "Page N" means the
  other thumb rests in its top zone, and so on for the other zones.

## Letters: placed by letter pairs

`tools/layout/LayoutOpt.cs` scores a layout on how often each English letter
pair occurs (Peter Norvig's counts from about 3 trillion letters) and how
far the thumbs must move to type it:

- Both thumbs move at the same time, so a step costs the longer of the two
  moves.
- Using the same thumb twice in a row costs extra, because it has to
  release and press again.
- Resting the other thumb on a page zone costs a little extra.

The two extra costs are judgment calls, so the search was run under four
weightings. The best layouts all share the same shape:

1. **All vowels plus Y on one pad,** in its upper zones; consonants on the
   other pad. English alternates consonants and vowels, so the thumbs mostly
   take turns, which is the cheapest pattern. It is also the easiest to
   remember: one vowel thumb, one consonant thumb.
2. **T and H go on each pad's top zone, on the "other thumb in N" page.**
   With both thumbs parked in their top zones, each thumb's top zone is both
   a key and the page selector for the other. So T, H, TH and HT need no
   movement at all.
3. The other common consonants (S N R C L P G B) are single clicks; the rare
   letters sit on page N.

Applied layout (vowels on the left thumb; the mirror image scores the
same):

```
other thumb CENTER (single clicks)        other thumb in N (page N)

   left pad        right pad               left pad        right pad
   I  E  U         C  S  P                 X  H  Q         W  T  F
   A  ·  O         N  ·  R                 K  ·  '         D  ·  M
   ,  Y  .         G  L  B                 ;  /  -         J  V  Z
```

- About **21–23% less thumb travel than v0**, depending on the weighting.
- The free corners of the vowel pad hold `,` and `.` as single clicks, next
  to the letters they usually follow. Page N's free zones hold
  `' ; / -`.
- The common word "the": right N (T, left thumb up) → left N (H, right
  thumb up) → left N again (E, after the right thumb returns to center).

Things the score does not capture, worth judging by feel:

- T is the second most common letter, and it needs the left thumb resting
  up. The model says that's worth it because of TH, HT, TE and ST, but it
  is the boldest choice here.
- The search could give the vowel pad its own page N row. In every run it
  left those zones mostly for rare letters, so they are available for more
  punctuation.

## Everything else: placed to be memorable

**Numbers: other thumb resting in its bottom zone (S).** The top five zones
of each pad, read like a book (NW, N, NE, W, E):

```
   left pad        right pad
   1  2  3         6  7  8
   4  ·  5         9  ·  0
   −  +  =         *  /  .
```

The bottom three zones hold numpad keys (`. / * + -`), which duplicate
symbols elsewhere exactly as a keyboard's numpad does and ignore Shift.
Everywhere else each symbol appears once, as its main-keyboard key, so
Shift does what it does on a keyboard (`/` gives `?`, `-` gives `_`). `=`
has no numpad key, so it is the main `=` (Shift gives `+`). The numpad `.`
types Delete if Num Lock is off.

**F-keys: other thumb resting in its bottom outer corner** (right thumb SE
for the left pad, left thumb SW for the right pad). F1–F10 sit exactly where
1–0 are, and F11 and F12 take the two bottom-center zones:

```
   left pad        right pad
   F1 F2 F3        F6 F7 F8
   F4  · F5        F9  · F10
   ·  F11 ·        ·  F12 ·
```

So F5 is "5, with the other thumb slid out to the corner".

**Brackets: thumbs spread outward.** With the right thumb resting E,
the left pad's W zone is `[` and its N zone is `` ` ``. With the left thumb
resting W, the right pad's E zone is `]` and its N zone is `\`. Shift gives
`{ ~ } |`.

Each bracket's zone is the other bracket's page selector, so with both
thumbs parked outward `[` and `]` alternate with no movement, the same
pairing that makes T and H free on page N.

Navigation needs no keyboard page: the d-pad is the arrow keys, and the
face buttons are PgUp / PgDn / Home / End in the mouse layer.

**Still open:** whether you want the vowels on the right thumb instead.

## Reproducing

```powershell
tools\layout\build.cmd
build\layout\LayoutOpt.exe count_2l.txt --seeds 6 --iters 150000 --same 1.0 --page 0.3
```

`build.cmd` uses the C# compiler that ships with Windows' .NET Framework.
`count_2l.txt` is from https://norvig.com/ngrams/count_2l.txt. Each seed
takes about half a second; the random sequence differs from the original
search, but the best layouts come out with the same shape and score
(about 77% of the v0 layout's cost).
