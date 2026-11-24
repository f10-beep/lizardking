# LizardKing keyboard layout

Status: **iterating** on the rim layout (`KEYMAP` in
`LizardKing/mode_lizardking.cpp`): the pages are placed by feel and the
letters by the layout search, both still being tried on the pads.

## How the keyboard works (recap)

- Both thumbs on the pads = keyboard layer. Each pad's 8 keys sit along its
  rim: a thumb finds a corner by pressing into it and lands on a side
  directly or by sliding from a corner to the bump. The center holds no
  key.
- Clicking one of a pad's 8 rim zones types a key. Which key depends on
  where the **other** thumb is resting. That gives 16 click zones × 9
  resting zones = 144 keys.
- "Home" means the other thumb rests in its top inner corner (NE for the
  left thumb, NW for the right), "the second page" that it rests on its top
  side (N), and so on for the other zones.
- Every key a keyboard has is somewhere, here or on a button (Enter, Esc,
  Space, Backspace, Tab, Delete and the modifiers; see docs/DESIGN.md).

## Pages by resting spot

Easiest spot first, for the other thumb (left thumb / right thumb zone).
Letters and controls take the three easiest spots; the three number pages
sit together: the inner side, the bottom inner corner beside it on the
rim, and the center next to both, so each is a step from the others.

| Spot | Zone | Page |
|---|---|---|
| top inner corner | NE / NW | home letters |
| top side | N / N | the second page: rarer letters and punctuation |
| top outer corner | NW / NE | navigation (right pad), media (left pad) |
| inner side | E / W | number row |
| bottom inner corner | SE / SW | F-keys |
| bottom side | S / S | free |
| bottom outer corner | SW / SE | system keys, Power and Sleep |
| outer side | W / E | free |
| center | C | numpad |

## Letters: placed by letter pairs

`tools/layout/LayoutOpt.cs` scores a layout on how often each English letter
pair occurs (Peter Norvig's counts from about 3 trillion letters) and the
effort of the thumb moves to type it:

- Both thumbs move at the same time, so a step costs the larger of the two
  moves.
- A thumb that stays on its zone costs nothing. One that moves pays for
  landing, by how hard its zone is to find blind: 1 for the top inner
  corner up to 2 for the outer side, in the order of the table above. It
  also pays 0.25 per zone of distance covered.
- Using the same thumb twice in a row costs extra, because it has to
  release and press again.
- Resting the other thumb off home costs a little extra.

It reads the shipped letters from `KEYMAP` in the firmware, scores them,
and searches the same pages for the best layout it can find, keeping each
page's number of letters so the punctuation keeps its slots. Any change to
the letters can be checked against it (see Reproducing).

The weights are judgment calls, so the search was run under eight
weightings (landing costs spread half and twice as wide, travel from 0 to
0.5, the same-thumb and page costs halved and doubled). The rarer letters
move between them, but the shape holds:

1. **A thumb's own zone is also the other pad's page.** So the letters that
   pair most often sit where each one's thumb is the other's page, and need
   no movement at all.
2. **E and R take the home corners,** in every run: with both thumbs home,
   E, R, ER and RE are free.
3. **T is the consonant pad's top side on the home page,** in every run.
   That leaves the right thumb on its top side, which is the page H needs:
   **H is the vowel pad's top inner corner on the second page,** so TH is
   free.
4. **I and N take the top sides of the second page,** so with both thumbs
   up, IN and NI are free.
5. The vowels stay on the left thumb, which still mostly alternates with
   the right; a few rare consonants (K, G, P, Y) join them.

Applied layout, the best the search finds with the default weights but
for K, swapped with `.` so that `, .` run as on a keyboard (0.08% of the
score):

```
home                                       second page (other thumb up)

   left pad        right pad               left pad        right pad
   U  A  E         R  T  S                 G  I  H         L  N  C
   K  ·  O         D  ·  W                 \  ·  P         M  ·  Q
   ,  .  Y         F  B  X                 /  ;  '         V  J  Z
```

- The common word "the": right pad N (T, left thumb home) → left pad NE
  (H: the right thumb stays up, the left doesn't move) → left pad NE again
  (E: the right thumb returns home).
- The search leaves two home slots on the vowel pad and four on its second
  page; the punctuation fills them as a keyboard has it: `, .` side by
  side, `; '` side by side, and `\` above `/`.

Things the score does not capture, worth judging by feel:

- I and N, both common, are on the second page. The model says the free IN
  and NI pay for it.
- Punctuation isn't scored, so `,` and `.` have the vowel pad's bottom
  outer corner and bottom side, two of its harder zones.

## Everything else: placed to be memorable

**Numbers: other thumb on its inner side** (right thumb W for the left pad,
left thumb E for the right pad). The digits run in an arch over the top,
where a thumb reaches most easily: 1 up the left pad's outer side, round
the top to 5 on its inner side, then 6 on the right pad's inner side round
to 0 on its outer side. Each digit is a zone from the next, and 5 and 6
face each other across the pads. The row's punctuation runs along the
bottom in a keyboard's order: `` ` `` in the corner below 1, `- =` after
0, the brackets mirrored between.

```
   left pad        right pad
   2  3  4         7  8  9
   1  ·  5         6  ·  0
   `  ·  [         ]  -  =
```

These are the main keyboard's keys, so Shift does what it does on a
keyboard (`1` gives `!`, `[` gives `{`). Each symbol in the map appears
once as its main-keyboard key; the numpad, which duplicates some, has its
own pages.

**F-keys: other thumb in its bottom inner corner** (right thumb SW for the
left pad, left thumb SE for the right pad), beside the numbers on the rim.
F1–F10 sit exactly where 1–0 are, and F11 and F12 take the two bottom
centers:

```
   left pad          right pad
   F2   F3  F4       F7   F8  F9
   F1   ·   F5       F6   ·   F10
   ·    F11 ·        ·    F12 ·
```

So F5 is "5, with the other thumb slid down to the corner".

**The numpad: other thumb in its center,** next to both other number pages
and laid out like them, for Alt codes and games that bind the numpad. It
is the rarest page, and the center is the hardest spot to hold. Its
operators run along the bottom, under the digits: `/ * - + .` and Enter
across both pads.

```
   left pad        right pad
   2  3  4         7  8  9
   1  ·  5         6  ·  0
   /  *  −         +  .  ⏎
```

These are real numpad keys, so Windows treats them as it treats a
keyboard's: with Num Lock off, or with Shift held while it's on, 3 is Page
Down, `.` is Delete, and so on.

**Control keys: other thumb in its top outer corner** (left thumb NW for
the right pad, right thumb NE for the left pad). The right pad is a nav
cluster: the arrows by direction, with Home / PgUp / End / PgDn in the
corners. The left pad puts play/pause top center, tracks either side, and
the volume keys along the bottom row:

```
   left pad            right pad
   ·     Play  ·       Home  ▲  PgUp
   Prev  ·     Next    ◀     ·  ▶
   Mute  Vol−  Vol+    End   ▼  PgDn
```

The d-pad stays the gamepad's outside the keyboard, so these keys live
here rather than on it.

**The keys that aren't F-keys or typing: other thumb in its bottom outer
corner** (left thumb SW for the right pad, right thumb SE for the left
pad), the hardest corner to reach. The right pad's top row is a keyboard's
Print Screen, Scroll Lock and Pause; the left pad mirrors it with Insert
and Num Lock. The AI key (✦, Windows' Copilot key) and Caps Lock take the
inner sides. There is no Menu key: Shift + F10 does the same.

Power (⏻) is the right pad's bottom outer corner and Sleep (⏾) the left
pad's, so each needs both thumbs in their hardest corners. Every other key
on these pages is two zones from them, and the pages beside on the rim
(the bottom and outer sides) are free, so reaching either by mistake takes
more than one misstep. They also act only when held for a second: the key
map fills their cell meanwhile, and a bump says they have acted. Let go or
slide off sooner and nothing happens. Unlike other held keys, anything
else cancels the hold too: another button, the other pad's click, the
other thumb changing page or lifting. Windows treats them as the PC's own
buttons: Sleep sleeps, Power does what the power button is set to do.

```
   left pad              right pad
   ·     NumLk  Ins      PrtSc  ScrLk  Pause
   ·     ·      Caps     ✦      ·      ·
   ⏾     ·      ·        ·      ·      ⏻
```

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
weights (`--same 1.0 --page 0.3 --spread 1.0 --travel 0.25` by default)
and the search (`--seeds 6 --iters 150000`); each seed takes about a third
of a second. `build.cmd` uses the C# compiler that ships with Windows'
.NET Framework.
