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

It replaces E13's first painting (sun, title, progress bar).
[`loading_painting.py`](../poc/experiments/loading_painting.py) paints it, and
E13 runs it.

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
| Signature | bottom right corner: a white box sized to the date, its right edge at 98.5% across and its bottom at 99% down. The text is about 6% of the height (the concept had 3.3%; the user wanted a larger date) |

## Sequence and time budgets

Everything except the signature and the finale uses the Brush, so most
switches change only the colour (about 1.0 s) rather than the tool (about
1.3 s). Each element has a fixed time budget; the pen's speed adapts to it.

| # | Element | Tool · colour | Budget | Notes |
|---|---|---|---|---|
| 1 | Title | Brush · Indigo, Red, Purple, Dark red or Black (random) | 3.0 s | Traced from the font outline, jagged and shaky. Now and then (12%) the pen forgets to lift between letters |
| 2 | Sun | Brush · Orange | 1.8 s | A ring that overshoots and does not meet itself, scribbles as fill, 7–9 rays of uneven length, about a third drawn twice |
| 3 | Cloud | Brush · Black | 0.9 s | Lumpy outline with 3–5 bumps, **deliberately left open**: the gap is needed for the finale |
| 4 | Frame | Brush · Black | 0.6 s | Four crooked strokes that overshoot the corners |
| 5 | Fill | Brush · Lime, 1.6× width | 1.4 s × the share of slides filled | A zigzag bite of 1/N of the frame per ready slide, a little outside the lines. After every element the pen comes back and catches up |
| 6 | Birds | Brush · Black | 2.5 s | Two or three "m"s in different sizes |
| 7 | Waiting | Brush · Lime | | Nothing left to decorate: the pen waits at the frame and adds one bite per slide that becomes ready |
| 8 | Finale | Fill (bucket) · Light turquoise | about 2.5 s | Starts only at 100%. The pen hesitates in the cloud ("…hmm", 0.8 s), then clicks. The colour leaks through the gap and floods everything connected, but not the inside of closed letters |
| 8a | Letters | Fill (bucket) · 1 to 4 random colours | about 1.5–3.5 s | The bucket fills 1 to 4 of the title's letters, chosen at random, each in its own colour from the palette (not White, Black, the greys, the sky's Light turquoise or the title's colour). The user's addition. The painter reads the title's area from the screen once, to find letter bodies (not holes) that are still white |
| 9 | Signature | Text · Black, Comic Sans, Background fill white | 4.0 s (measured: about 9 s) | Today's date with the last two digits swapped, corrected with Backspace, in an opaque white box. The concept had it among the decorations, before the finale; it moved after it (E27, below) |

## Honest progress

1. The fill follows slides that are ready, never the clock.
2. After each decoration the pen goes back to the frame and catches up.
3. **Always the whole painting** (the user's call, overriding the concept):
   sun, cloud and birds come every time, however soon the slides are ready.
   The concept left them out when preparation was short (no sun under 9 s,
   no cloud or birds under 6 s), which in practice would have been nearly
   every show: a cached deck is ready at once, and a fresh 7-slide deck in a
   few seconds. The signature always comes last, after the finale.
4. With the decorations done, the pen waits at the frame until every slide
   is ready.
5. The first key press always jumps straight to slide 1; only decoration is
   lost.

The whole painting takes about 25 s when the slides are ready from the
start (the designer's simulation, with the short versions, gave 9.5–19.2 s
for 3–15 s of preparation).

## Gags

In the storyboard:

- The bucket leaks through the hole in the cloud and the whole sky turns
  light blue.
- The pen hesitates in front of the cloud before it clicks ("…hmm").
- A typo in the date, corrected with Backspace.
- The opaque text box: after the bucket, the signature stands in a white
  rectangle in the middle of the sky.
- After the sky, the bucket colours in a few of the title's letters.

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
- How many of the title's letters the bucket fills (1 to 4), which ones, and
  in which colours.
- A seed decides the shake, the ray angles and the number of cloud bumps.
- The date in the signature turns slide 0 into a stamp of the day.
- Ideas: a moon after 18:00, airbrush snow in December, and once in 20 runs a
  reserve gag instead of the birds.

## In real Paint

The concept was simulated in a browser. E27 checked its open points in Paint:

- **The signature's white box:** Paint's bucket floods every connected pixel
  of the same colour, so a white text box on the white canvas would flood
  too. **The signature is typed after the bucket**, a deviation from the
  sequence above; its box then needs no outline.
- **Brush width:** size + 1 px, as for the Pencil (E16). The concept's 5
  design px is size `round(5u × scale) − 1`.
- **The Text tool:** a **Background fill** check box gives the opaque box (in
  the secondary colour), next to the font and size boxes. The Text font is
  the one setting Paint keeps across sessions (E16), so the presenter must
  put it back, best when the show ends, out of the audience's sight.
- **The bucket on Brush strokes:** the cloud's gap leaks into the whole sky
  and closed shapes hold, **once the end point of each stroke is sent
  twice**: Paint drops the last segment of a stroke, and a ring drawn in one
  stroke leaked through it. A thin anti-aliased rim stays along the strokes.

Building it (E13's third round) answered the rest:

- **Keys while painting:** the keyboard hook is on during the painting, so
  the first key can stop it. The painter's own keys (Fill `B`, Text `T`,
  Backspace) carry the presenter's tag (E10); the typed date goes in as
  Unicode characters, which are not show keys.
- **The font size:** only the sizes in the size box's list can be chosen, by
  selecting the item; a value set through UIA or typed into the box is
  ignored. The date takes the largest listed size not above 6% of the
  canvas height: 48 pt on a 1283 px canvas.
- **Paint keeps the Text font and its size across sessions.** The painter
  writes both to a file before changing them and puts them back after the
  show; a run that dies half way is put right by the next one.
- **Committing the text:** a pen tap on the canvas outside the box, then
  another tool. A tool change alone dropped the typed text in some runs.
- **Timing:** with 7 slides and 12 s of preparation the painting takes 24 s
  (the concept's simulation gave 19.2 s for 15 s). The signature takes about
  9 s of it, mostly choosing the font, size, fill and colours through UIA:
  the next thing to speed up.
