# Presenter.Slides

Decks come from other tools ([research 05 §6](../../docs/research/05-presenter-architecture.md#6-decks-come-from-other-tools)).
This library recognises a deck as its tool left it, and renders its slides
to PNG files of one exact pixel size, in the background, with progress,
through a cache (§7.3). It ports the POC's measured behaviour (E12, E13,
E22, E24, E25).

```csharp
var deck = Decks.Inspect(path);                       // kind, aspect, slide count
var size = SlideGeometry.SlideSize(viewport, deck.Aspect);
await using var render = SlideRender.Start(deck, size, new RenderOptions(cacheDir));
render.SlideReady += n => /* slide n is on disk at render.SlidePath(n) */;
var already = render.Done;                            // subscribe first, then read Done
await render.WaitForSlideAsync(1);                    // enough to start the show
await render.Completion;                              // all slides, or a SlideRenderException
```

## Decks

| Kind | Recognised by | Shape and count (`Inspect`) | Rendered by |
|---|---|---|---|
| PowerPoint | `.pptx` (and `.pptm`, `.ppsx`, …), `.ppt`, `.pps` | `.pptx`: `ppt/presentation.xml`, hidden slides not counted, no PowerPoint started. `.ppt`: PowerPoint, hidden, briefly | PowerPoint through late-bound COM, `Slide.Export` |
| PDF | `.pdf` | page 1's shape, the page count (Windows.Data.Pdf) | Windows.Data.Pdf |
| Marp | `.md` with `marp: true` in the front matter | the theme's design canvas (built-in themes: 1280×720, `size: 4:3` 960×720; a custom theme's CSS next to the deck); slides as Marp splits them | the deck's own marp-cli, else the presenter's |
| Slidev | a folder with `slides.md`, a `slides.md` (or `.md` with Slidev-only headmatter), or a `package.json` depending on `@slidev/cli` | `canvasWidth` (980) and `aspectRatio` (16/9); slides as Slidev's parser splits them, hidden ones left out | the project's own Slidev, else the presenter's |

marp-cli runs in the deck's folder, so the deck's own `.marprc` or
`marp.config.*` (themes, HTML) applies, with `--allow-local-files` so the
deck's local images show, as they do in Marp's own preview.

No deck needs Paint-specific markup. Animations and builds are not
supported: every slide is shown in its final state (Slidev without
`--with-clicks`; `Slide.Export` draws animated objects in place, E25).

## Sizes: never stretched

Each renderer works from the deck's own canvas at a scale that fits it into
the slide size (Marp `--image-scale`, Slidev `--scale`, PowerPoint
`Slide.Export` at the deck's exact shape, PDF at the exact width). Rounding
leaves a pixel too many or too few (E12), so every image is then centred on
the exact size: missing rows or columns are padded with the slide's own
bottom-left pixel colour, extra ones cropped. A deck whose shape differs
from the size is padded (pillar- or letterboxed), never stretched. Images
already at the size are moved, not re-encoded; others are re-encoded with
Windows' own PNG codec (no image library ships), a few at a time.

PDF sizes are in DIPs, not pixels (E24): page 1 is rendered once to measure
the factor (1.8 or 2 on the POC laptop), then each page is asked for at
width ÷ factor, nudged a DIP until the width is exact.

## Progress: `Total` and `Done`

- `Done`: slides 1..Done are on disk, always in order. `SlideReady` is raised
  for each, on a worker thread. A cache hit is complete when `Start` returns
  and raises no events.
- `Total` is known at `Start` for PDF, `.pptx` and (through `Inspect`) `.ppt`.
  Marp and Slidev start with the count parsed from the Markdown; the render
  corrects it to the number of images made (raised as soon as more appear,
  lowered at the end). `TotalChanged` tells each change.
- When the slides arrive: marp-cli writes all images at the end of its run
  (about 2 to 4 s for a short deck); Slidev too, after its dev server is up
  (about 12 s cold); PowerPoint and PDF deliver slide by slide (50 to 150 ms
  each). One-slide-at-a-time rendering for Marp and Slidev (E22) is not here
  yet.
- `Completion` comes after the tool has finished, not with the last slide:
  for Marp and Slidev when Node and Edge have exited (about 0.5 s); for
  PowerPoint once a PowerPoint the session started has quit and exited,
  which took 2.4 s alone and up to 10 s (the session's wait) under load.
  Start the show on `SlideReady` or `WaitForSlideAsync`, not on `Completion`.

## Cache

A folder per key under `RenderOptions.CacheDir`. The key is SHA-256 over the
deck's files (the deck; for Marp also its theme CSS, config and
package.json/lock; for Slidev the entry and the project's package.json, lock
and Vite/Uno config), the renderer and its version (marp-cli's or Slidev's
package version, PowerPoint's registered version, the Windows build for PDF)
and the size. Images, components and imported Markdown are not in the key:
change the deck file (or clear the cache) after changing only those. A
`complete` marker makes a folder an entry; its time is the entry's last use,
and entries beyond `KeepEntries` (20) are removed least recently used. Only
folders named like keys are ever deleted. A render holds a lock file in its
folder; a second render of the same key at the same time uses a sibling
folder.

## Processes: nothing of the user's is touched

- **Node renderers** (Marp, Slidev) start `node <the tool's script>` directly
  (not npm's `.cmd` shims, so arguments never pass through cmd.exe), without a
  window, inside a Job Object with kill-on-close, assigned at creation (no
  race). Edge and everything else they start is in the job too: when the
  tool exits, anything left is ended; cancelling or disposing a render ends
  them; and if the presenter dies, Windows ends them. Only processes we
  started are ever stopped, never anything by name.
- **Edge** runs headless with a throwaway profile: TEMP and TMP point into the
  render's work folder inside the cache entry, where marp-cli and Playwright
  create their profiles. The folder is deleted after the render.
- **PowerPoint** runs through COM on an STA thread of its own, always without
  a window, with alerts off for the session (restored afterwards). The deck is
  opened read-only from a private copy, so closing it can never close a
  window the user has on the same file. If PowerPoint was already running, the
  user's instance is used and never quit. An instance the session started is
  quit, but only if nobody has opened anything in it or made it visible
  meanwhile; it is never killed.

## Node tools

The deck's own `node_modules` (at or above the deck) come first. Otherwise
`RenderOptions.NodeToolsDir` (default `%LOCALAPPDATA%\PaintPresentator\node-tools`):

```cmd
npm install --prefix "%LOCALAPPDATA%\PaintPresentator\node-tools" @marp-team/marp-cli
npm install --prefix "%LOCALAPPDATA%\PaintPresentator\node-tools" @slidev/cli @slidev/theme-default playwright-chromium
```

with `PLAYWRIGHT_SKIP_BROWSER_DOWNLOAD=1` (Edge is used). A missing tool
faults `Completion` with that command in the message.

## Tests

`dotnet test tests/Presenter.Slides.Tests`. Unit tests and the PDF renders
need nothing installed. The integration tests use the Node tools in
`PRESENTER_NODE_TOOLS`, else the POC's `%TEMP%\e12-node`
([e12_render.py](../../poc/experiments/e12_render.py) says how to make it),
Edge, and PowerPoint; each skips cleanly when its tool is missing. All decks
are synthetic, written by the tests.
