---
marp: true
theme: presentator
---

# Paint Presentator

Slides, live in Microsoft Paint

<!-- Open with the joke: yes, this really is Paint. Point at the ribbon. -->

---

# Why

- Paint is on every Windows PC
- The ribbon and title bar stay on screen
- Everyone can see it really is Paint

<!-- The chrome is the point: without it, it is just another window. -->

---

# How a slide gets there

1. Rendered at start, at the size this screen allows
2. Put on the clipboard as a PNG
3. Ctrl+V, then `S` to commit the paste

<!-- One paste per slide. Paint never notices the clipboard by itself. -->

---

# What the proof of concept measured

| Step | Time |
|---|---|
| Key press to slide on screen | about 110 ms |
| Keyboard hook | 0.1 ms |
| Rendering, per slide | 0.05 to 0.26 s |

<!-- Numbers from the POC results on the author's laptop. -->

---

# The loading painting

- Drawn live, with Paint's own brushes
- The progress bar is honest
- It is meant to look a bit crappy

<!-- Mention that nobody drew it: a synthetic pen did. -->

---

# Keys

- Right, Down, Space, Enter: next
- Left, Up, Backspace: back
- B: black screen, W: white screen
- A number and Enter: jump to that slide
- Esc: end

<!-- The same keys as a PowerPoint slide show. -->

---

# Thank you

Questions?

<!-- Press Esc to end the show; Paint closes without saving. -->
