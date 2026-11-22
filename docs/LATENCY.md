# Latency

A code-level analysis: what the puck adds between the controller's report and
the PC receiving it, and the delays the design adds on purpose. It is not a
measurement. End-to-end numbers need a rig such as Gamepadla's Prometheus 82
(a solenoid that presses a button and times the USB report); for reference,
OpenPuck's Xbox mode, which uses the same RF loop and the same XInput driver as
LizardKing's gamepad, measured 5.44 ms for sticks and 6.72 ms for buttons on
that rig, controller included.

## The path of one report

| Stage | Cost | Where |
|---|---|---|
| Wait for the next RF poll | 0–4 ms, 2 ms on average | the puck polls each controller every 4 ms (250 Hz, `POLL_US_DEFAULT`), as Valve's puck does; the controller answers each poll with its newest report |
| Poll and reply on air | a few hundred µs | 2 Mbit/s; the reply window is 1.2 ms (`g_rxWin`) but a reply ends it early |
| Decode and LizardKing's mapping | microseconds | runs on the reply, in the same loop pass (`onReport45` → `lkProcess`) |
| Hand to USB | ≤ 1 ms | the report is queued and drained by the next USB pump or frame |
| Host poll | ≤ 1 ms | every endpoint is 1 ms: the XInput pad (`bInterval` 1) and the keyboard, mouse and media keys (`setPollInterval(1)`) |

Puck-added latency: about 3 ms on average, about 5.5 ms at worst, before the
controller's own sampling. The loop polls all connected controllers in the
same 4 ms cycle, so a second controller does not halve the rate.

What can stretch it:

- **Haptics.** Each relayed rumble or haptic frame is an extra transmission
  that precedes a poll and uses its reply window; heavy rumble can cost polls.
- **A weak link.** A lost reply means the input arrives one poll later; a slot
  that was replying gets one retransmit within the same cycle when it fits.
- **Flash writes.** Saving bonds or settings stalls the loop for milliseconds;
  this happens only on pairing and on console tuning, never in play.

`SS` on the serial console shows the live poll rate, fresh reports per second
and lost replies.

## Delays by design

These trade a few milliseconds for intent:

| Delay | Value | Why |
|---|---|---|
| Layer change | 30 ms (`LK_SWITCH_MS`) | a brushed pad doesn't flicker the layer; a held button keeps the layer it was pressed in, and pad clicks use the undebounced layer, so a quick tap still clicks |
| Cursor smoothing | none below ~250 px/s; easing to 30% of the remaining gap per report above ~2,000 px/s | slow movement is exactly 1:1; fast swipes are smoothed and land where 1:1 would put them |
| Pad position | every 2nd–3rd report (8–12 ms) | the controller refreshes pad positions at that rate; the trackball measures speed per new position |
| Scroll wheel | at most one report per 16 ms (`LK_WHEEL_GAP_MS`) | some apps stutter on a wheel report every 4 ms; a large step or the wheel stopping goes out at once |
| Guide button | on release; held 80 ms | a tap can only be told from a medium press when it ends |

Buttons, keys, clicks and the trigger keys (Space, Backspace) go out the moment
their threshold is crossed; nothing else waits.
