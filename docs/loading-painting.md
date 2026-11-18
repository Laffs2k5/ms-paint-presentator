# The loading painting: The sky leaks

The concept for the loading painting (the plan's presenter design), from the
user's designer, 2026-11-18. Summarised here in our own words, as the spec
the presenter implements; the designer's PDF and prototype stay with the user.
Name: *The sky leaks*.

## The story

The pen writes the title, draws a frame and fills it in step with the slides
that are really ready. While it waits it decorates (a sun, a cloud, birds),
but only if there is time. At the end it tries to fill the cloud with the
bucket. The cloud has a hole, and the whole sky turns light blue. Then it
signs, with a typo.

Today's painting (E13: sun, title, progress bar) is kept and built on.

## Composition

In fractions of the canvas. The design space is 1280 × 720 for 16:9 decks and
1280 × 610 for 2.1:1; `u` = design height / 720 scales stroke widths and
offsets.

| Element | Where |
|---|---|
| Title | 8–92% across, 22–58% down; word-wrapped, bold Comic Sans outline |
| Frame (progress) | 12–88% across, 75–86% down |
| Sun | centre at 13% or 87% across, 17% down; radius 7.5% of the height |
| Cloud | centre at 78% or 22% across, 14% down; 14% wide, 6.5% high |
| Sun and cloud | in opposite top corners; which side is random |
| Birds | three small "m"s near the top middle |
| Signature | bottom right, at 82% across, 95% down |

## Sequence and time budgets

Everything except the signature and the finale uses the Brush, so most
switches change only the colour (about 1.0 s) rather than the tool (about
1.3 s). Each element has a fixed time budget; the pen's speed adapts to it.

| # | Element | Tool · colour | Budget | Notes |
|---|---|---|---|---|
| 1 | Title | Brush · Indigo, Red, Purple, Dark red or Black (random) | 3.0 s (fast: 2.0 s) | Traced from the font outline, jagged and shaky. Now and then (12%) the pen forgets to lift between letters |
| 2 | Sun | Brush · Orange | 1.8 s | Only if preparation takes 9 s or more. A ring that overshoots and does not meet itself, scribbles as fill, 7–9 rays of uneven length, about a third drawn twice |
| 3 | Cloud | Brush · Black | 0.9 s | Not in fast mode. Lumpy outline with 3–5 bumps, **deliberately left open**: the gap is needed for the finale |
| 4 | Frame | Brush · Black | 0.6 s (fast: 0.4 s) | Four crooked strokes that overshoot the corners |
| 5 | Fill | Brush · Lime, 1.6× width | 1.4 s × the share of slides filled | A zigzag bite of 1/N of the frame per ready slide, a little outside the lines. After every element the pen comes back and catches up |
| 6 | Decorations | | | Only if time allows (below), in this order |
| 6a | Signature | Text · Black, Comic Sans | 4.0 s | Today's date with the last two digits swapped, corrected with Backspace. The text box is opaque white |
| 6b | Birds | Brush · Black | 2.5 s | Two or three "m"s in different sizes |
| 7 | Waiting | Brush · Lime | | Nothing left to decorate: the pen waits at the frame and adds one bite per slide that becomes ready |
| 8 | Finale | Fill (bucket) · Light turquoise | about 2.5 s | Starts only at 100%. The pen hesitates in the cloud ("…hmm", 0.8 s), then clicks. The colour leaks through the gap and floods everything connected, but not the inside of closed letters |

## Honest progress

1. The fill follows slides that are ready, never the clock.
2. After each decoration the pen goes back to the frame and catches up.
3. Sun and cloud come right after the title when preparation is long
   enough. The signature and the birds start only if the estimated remaining
   time (measured pace × slides left) covers their cost with at most 1 s to
   spare.
4. With the decorations used up, the pen waits at the frame.
5. Fast mode, under 6 s of preparation: a faster title, no sun, cloud or
   decorations; the bucket fills the sky directly. Under 9 s: no sun.
6. The first key press always jumps straight to slide 1; only decoration is
   lost.

The designer's simulation: 3 s of preparation (fast mode) gives a painting of
9.5 s; 8 s gives 12.0 s; 15 s gives 19.2 s. So the painting ends about 4–7 s
after the slides are ready.

## Gags

In the storyboard:

- The bucket leaks through the hole in the cloud and the whole sky turns
  light blue.
- The pen hesitates in front of the cloud before it clicks ("…hmm").
- A typo in the date, corrected with Backspace.
- The opaque text box: after the bucket, the signature stands in a white
  rectangle in the middle of the sky.

In reserve:

- Size 90 by accident: one huge blob, then Ctrl+Z.
- The eraser after the bucket: it erases with white and leaves a white hole
  in the blue sky.
- A speech bubble from the sun: "Is that … Paint?", answered "yes" in
  Comic Sans.
- A star shape dragged out crooked and far too big.
- An airbrush cloud that turns too dense because the pen lingers.

## Variation

- The title colour: Indigo, Red, Purple, Dark red or Black, all readable on
  both white and light blue.
- Sun and cloud swap sides.
- A seed decides the shake, the ray angles and the number of cloud bumps.
- How much decoration fits depends on the preparation time; quick runs come
  out simpler.
- The date in the signature turns slide 0 into a stamp of the day.
- Ideas: a moon after 18:00, airbrush snow in December, and once in 20 runs a
  reserve gag instead of the birds.

## Open questions for real Paint

The concept was simulated in a browser. To check in Paint (the screen is the
user's until they say otherwise):

- **The signature's white box:** Paint's bucket floods every connected pixel
  of the same colour, so a white text box that touches the white canvas would
  flood too. Either type the signature after the bucket, or give its box an
  outline (a white rectangle with a thin border) so the flood stops at it.
- **Brush width:** E16 measured the size slider for the Pencil only (width =
  size + 1 up to 20). The concept wants about 5 design px (`2 × 2.5u`).
- **The Text tool:** whether it has an opaque-background switch, and setting
  the font size. The Text font is the one setting Paint keeps across
  sessions (E16), so the presenter must put it back, best when the show
  ends, out of the audience's sight.
- **The bucket on Brush strokes:** that the cloud's gap leaks and the closed
  shapes hold, and how the anti-aliased edges look after the flood.
- **Keys while painting:** the keyboard hook must be on during the painting
  so the first key can stop it, which means the painter's own keys (Fill `B`,
  Backspace) must carry the presenter's tag or the hook swallows them (E10).
