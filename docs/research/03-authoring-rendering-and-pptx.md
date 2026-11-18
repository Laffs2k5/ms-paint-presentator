# Research: authoring, rendering and PPTX support

> Status: research notes, 2026-11-18. Windows 11 only. Versions as published on
> 2026-11-18: marp-cli 4.5.1, @slidev/cli 53.1.0, decktape 3.17.0, reveal.js 6.0.2,
> Playwright 1.65.1, python-pptx 1.0.2, CefSharp.OffScreen.NETCore 156.0.300,
> Microsoft.Web.WebView2 1.0.4314.35. **[verify]** = not tested; check on the laptop.

## TL;DR

- **Build first, then present.** Never render while the audience waits. A *build*
  step turns any source (Markdown/HTML deck, PPTX) into a **deck bundle**: one image
  per slide *and per build step*, at exactly the Paint canvas size, plus a
  `manifest.json` with notes and transitions. At show time the presenter holds all
  frames as ready-made DIBs in memory. A clicker press does three things: swap the
  DIB inside our live `IDataObject`, trigger the paste, and update the presenter view.
  The latency is then essentially Paint's paste/composite time. pdfpc works the
  same way.
- **The bundle format is the contract.** The presenter knows nothing about Slidev,
  Marp or PowerPoint, and adapters produce bundles. That keeps the Windows-specific
  runtime small, and the authoring side can run anywhere: Linux CI, WSL, a dev
  container.
- **Authoring recommendation:**
  - **Primary: [Slidev](https://sli.dev)** (Markdown + Vue). It is the only tool that
    natively exports one PNG **per click step**:
    `slidev export --format png --with-clicks`.
  - **Simple option: [Marp](https://marp.app)** (pure Markdown, very low friction).
    Image export has no per-fragment frames, so use one slide per build.
  - **reveal.js / Quarto** through a small Playwright driver (`Reveal.next()` + screenshot).
- **PPTX: supported as an import adapter, not the native format.**
  - **Tier 1:** PowerPoint COM (`Slide.Export(path, "PNG", w, h)` + notes from
    the notes page). Builds come from PPspliT-style slide splitting.
  - **Tier 2:** LibreOffice → PDF → rasterise (static only, fidelity varies).
  - Notes are always extracted with python-pptx or Open XML (no Office needed).
- **Live content** (clock, demo, video) is the exception. Use a warm offscreen
  browser: **CefSharp.OffScreen** (raw BGRA + dirty rects; maps straight to a DIB)
  in a .NET host, or **CDP `Page.startScreencast`** from Node/Python. Avoid
  WebView2: it has no offscreen mode and only PNG/JPEG capture.

---

## 1. Architecture sketch

```
            ┌──────────────── build (any OS, CI-able) ────────────────┐
 deck.md ─▶ │ adapter: slidev | marp | reveal(playwright) | pptx(COM)  │ ─▶ bundle/
 deck.pptx  └──────────────────────────────────────────────────────────┘    manifest.json
                                                                             frames/0001-0.png …
            ┌──────────────── present (Windows 11) ───────────────────┐
 bundle/ ─▶ │ loader → DIB cache (current ±2 decoded)                  │
            │ input: WH_KEYBOARD_LL (clicker keys)                     │
            │ display: live IDataObject (CF_DIB) + paste trigger ──────┼─▶ MS Paint
            │ transitions: generated in-process from cached frames     │
            │ presenter view: notes/next/timer (no-activate window     │
            │                 or localhost web page on phone)          │
            └──────────────────────────────────────────────────────────┘
```

### Bundle format (draft)

```jsonc
// bundle/manifest.json
{
  "version": 1,
  "title": "Terraform at scale",
  "canvas": { "width": 1280, "height": 720 },   // Paint canvas size, pixels
  "source": { "adapter": "slidev", "file": "slides.md", "built": "2026-11-18T10:00:00Z" },
  "slides": [
    {
      "id": "intro",
      "notes": "Say hi. Mention Doom.",
      "transition": { "type": "wipe-left", "frames": 8 },  // optional; host generates frames
      "steps": [                                           // build steps / clicks
        { "frame": "frames/0001-0.png" },
        { "frame": "frames/0001-1.png" }
      ]
    },
    {
      "id": "demo",
      "live": { "kind": "url", "url": "http://localhost:3030/5", "fps": 15 } // optional live slide
    }
  ]
}
```

Design notes:
- Frames are stored as PNG on disk (small, diff-able, previewable). Decode them to
  BGR DIB bytes at load time. Prefetch ±2 steps and keep the rest compressed.
- `canvas` = the size we paste. Paint's view zoom scales it up for free and with
  crisp nearest-neighbour edges, so authoring at 1280×720 and showing at 150% on
  1080p is a valid choice. Choose after measuring paste cost (POC E6).
- Optional "diff paste" idea **[verify]**: for a build step, paste only the bounding
  box of changed pixels. Fewer pixels make it faster, but it depends on controlling
  *where* Paint places a paste, which DoomPaint never needed. Paint pastes at the
  top-left of the visible canvas, so this is probably not controllable without a
  selection-move. Low priority.

## 2. Authoring options compared

| Tool | Per-slide PNG | Per-build/click PNG | Resolution | Notes extraction | PPTX export | Fit |
|---|---|---|---|---|---|---|
| **Slidev** ([export](https://sli.dev/guide/exporting), [CLI](https://sli.dev/builtin/cli)) | `slidev export --format png` → `NN.png` | **Yes:** `--with-clicks` → `NN-<clicks>.png`. Live: URL `?clicks=N` | `--scale <n>` (Playwright `deviceScaleFactor`). Base from `canvasWidth`/`aspectRatio` | Notes are the trailing HTML comment per slide. `slidev export-notes` gives PDF only, so parse with `@slidev/parser` **[verify]** | `--format pptx` (images + notes), `--format pptx-editable` | **Best for builds** |
| **Marp** ([marp-cli](https://github.com/marp-team/marp-cli)) | `marp --images png` → `deck.001.png` | **No.** Fragment lists only animate in the HTML template, so use one slide per step | `--image-scale` (1280×720 × scale) | `marp --notes` (txt), or `marpit.render().comments` | `--pptx` (images + notes), `--pptx-editable` (experimental, needs LibreOffice, no notes) | **Simplest** |
| **reveal.js** ([API](https://revealjs.com/api/)) | Playwright driver | **Yes:** `Reveal.next()` steps fragments, `Reveal.getIndices()`, `Reveal.getSlideNotes()` | viewport × `deviceScaleFactor` | `Reveal.getSlideNotes()` | none | Flexible, more glue |
| reveal.js + **decktape** ([README](https://github.com/astefanutti/decktape)) | `--screenshots --screenshots-format png --size 1920x1080` | `--fragments` **[verify]** | `--size`, `--screenshots-size` | — | — | Ready-made fallback |
| **Quarto revealjs** ([docs](https://quarto.org/docs/presentations/revealjs/presenting.html)) | same as reveal.js | same | same | `::: {.notes}` | `format: pptx` (native, but looks different) | If you already use Quarto |

All of these use headless Chromium (Playwright or Puppeteer). On Windows they can
use the preinstalled **Edge** (`--browser auto` in Marp, `--executable-path` in
Slidev, `channel: 'msedge'` in Playwright). The build step can equally run in
WSL or CI on Linux, since a bundle is just PNGs + JSON.

### Recommendation
- **Slidev as the primary adapter.** Click-step export is exactly what a slide
  player needs, Markdown-first authoring fits a terraform/bash person, and hot reload
  works while writing.
- **Marp as the low-ceremony adapter** for talks without builds.
- Keep a **generic "folder of PNGs + notes.md" adapter** for anything else, such as
  Excalidraw exports or hand-drawn slides made in Paint itself.

## 3. Rendering: pre-render versus live

### 3.1 Pre-render (default)

- Adapter CLIs (above) render once at build time. No latency at show time.
- Transitions are generated in the host from cached frames: crossfade, wipe, push,
  and a "paint bucket" or pixel-dissolve gimmick. Keep them short (6–12 frames) and
  possibly at reduced resolution, so they fit Paint's paste budget. Paint's undo
  stack grows one step per pasted frame, so transitions add undo steps.

### 3.2 Live rendering (only for live slides)

| Option | Output | Pros | Cons |
|---|---|---|---|
| **CefSharp.OffScreen** (.NET) | `Paint` event: **raw BGRA**, top-left origin, plus `dirtyRects` ([cef_render_handler.h](https://github.com/chromiumembedded/cef/blob/master/include/cef_render_handler.h)). `windowless_frame_rate` 1–60 | No encode/decode. Maps 1:1 to a 32-bit DIB (negative `biHeight` for top-down). Dirty rects say when to paste | ~100+ MB CEF runtime. Call `Cef.Shutdown()` |
| **CDP `Page.startScreencast`** | base64 JPEG/PNG frames, `maxWidth`/`maxHeight`/`everyNthFrame`/`maxFramesInFlight`, ack with `screencastFrameAck` ([protocol](https://github.com/ChromeDevTools/devtools-protocol/blob/master/json/browser_protocol.json)) | Works from Node/Python with Playwright/Puppeteer. Frames only on change | JPEG/PNG decode per frame. No official fps figure (one write-up: ~19 fps against a 25 target) |
| Playwright `page.screenshot` | PNG/JPEG/WebP (`animations: "disabled"`, `clip`) | Simple | No raw format. No rigorous latency benchmark found; anecdotes say ~200 ms for a warm 1280×800 PNG **[verify]** |
| CDP `HeadlessExperimental.beginFrame` | deterministic frame + screenshot | Frame-exact animation capture | Headless shell only. Overkill |
| **WebView2** `CapturePreviewAsync` | PNG/JPEG only ([docs](https://learn.microsoft.com/dotnet/api/microsoft.web.webview2.core.corewebview2.capturepreviewasync)) | Edge runtime preinstalled | **No offscreen/windowless mode** ([feedback #20](https://github.com/MicrosoftEdge/WebView2Feedback/issues/20), still open). Hidden = not rendered. **Avoid** |
| Windows.Graphics.Capture | BGRA8 D3D11 frames | Captures any window or visual (a demo app or terminal) | Minimised or cloaked windows produce no frames (community reports). Fine for capturing a *visible* demo window |

**Video clips:** decode with ffmpeg/PyAV/Media Foundation straight to BGR frames at
the canvas size, then stream through the same `IDataObject` at ≤25 fps. Play audio
separately.

### 3.3 Latency budget for "snappy"

```
clicker keydown → LL hook (≈0 ms) → swap DIB pointer (≈0 ms) → paste trigger (≤1 ms)
→ Paint OleGetClipboard→GetData RPC (copy N bytes) → Paint composite (∝ pixels)
→ DWM present (≤16 ms)
```

The unknown is Paint's paste/composite time per pixel count. DoomPaint suggests
~30 ms for 640×400. Linear extrapolation gives **~100 ms for 1280×720** and
**~240 ms for 1920×1080** **[verify]** (POC E6). If 1080p is too slow, paste 1280×720
or 960×540 and let Paint's zoom upscale.

## 4. PPTX support

| Tier | How | Builds/animations | Fidelity | Requirements |
|---|---|---|---|---|
| **1. PowerPoint COM** | `Slide.Export(file, "PNG", w, h)` ([docs](https://learn.microsoft.com/office/vba/api/powerpoint.slide.export)) or `Presentation.Export(path, "png", w, h)`. Notes: loop `NotesPage.Shapes.Placeholders` for `PlaceholderFormat.Type = ppPlaceholderBody (2)` | **(a)** Split slides per click, PPspliT-style ([PPspliT](https://github.com/maxonthegit/PPspliT)): read `Slide.TimeLine.MainSequence` effects (`.Shape`, `.Exit`, `.Timing.TriggerType`) and hide shapes not yet shown. **(b)** Live: windowed slideshow + `SlideShowView.GotoClick(n)` + `PrintWindow` capture | Perfect (it *is* PowerPoint) | Office installed. Interactive session only, since unattended Office automation is unsupported ([MS](https://learn.microsoft.com/office/client-developer/integration/considerations-unattended-automation-office-microsoft-365-for-unattended-rpa)). Export cap 100 MP |
| **2. LibreOffice headless** | `soffice --headless --convert-to pdf deck.pptx` → `pdftoppm -png -scale-to-x 1280 -scale-to-y 720` or **pypdfium2** (raw pixels) or `Windows.Data.Pdf`. Note: `--convert-to png` only exports slide 1 | None | Fonts, SmartArt, charts and effects can differ | LibreOffice. Runs on Linux CI too |
| 3. Commercial | Aspose.Slides (renders, *and* plays animations with `PresentationPlayer` frame ticks), Syncfusion PresentationRenderer (free community licence if eligible) | Aspose: yes | Good | Licence |
| Notes only | python-pptx 1.0.2 / Open XML SDK | — | — | None |

Not worth it: copying slides from PowerPoint into Paint through the clipboard. The
formats include internal PowerPoint formats plus PNG, which is undocumented for
whole slides. Our own DIB pipeline is more controllable.

**Recommendation:** the `pptx` adapter tries PowerPoint COM first, and uses
PPspliT-style splitting if you want builds (start without builds: one frame per
slide). It falls back to LibreOffice → PDF → pypdfium2 with a fidelity warning.
Notes always come from python-pptx. A PPTX *export* of our own decks is already
available from Slidev/Marp, which is handy as an "emergency PowerPoint fallback" on
stage.

## 5. Presenter view and input

- **Input:** a `WH_KEYBOARD_LL` hook captures the clicker keys (PageDown/PageUp,
  arrows, Space, B/. for black, F5/Esc) and swallows them, so Paint never sees them.
  Proven in DoomPaint.
- **Presenter view options:**
  1. A second-monitor window with `WS_EX_NOACTIVATE`, so it never steals focus from
     Paint. This is required if the paste trigger needs Paint in the foreground.
  2. A **localhost web page** (phone or tablet on the same network, or a second
     screen) fed over WebSocket, in the style of presenterm's notes-over-UDP pattern
     ([presenterm](https://mfontanini.github.io/presenterm/features/speaker-notes.html)).
     It decouples cleanly from Paint focus.
- If the background paste trigger (`WM_COMMAND`, doc 02 §3.2) works, the focus
  constraint disappears and option 1 becomes a normal window.

## 6. Prior art worth copying

- **pdfpc** ([repo](https://github.com/pdfpc/pdfpc)): pre-rendered in-memory slide
  cache, automatic build grouping, notes sidecar, presenter console. This is the
  model for our runtime.
- **presenterm** (Rust): separate notes process over localhost UDP, hot reload.
- **PPspliT:** the reference for flattening PowerPoint animations into static steps.
- **Slidev / decktape export:** headless browser → step through states → screenshot.
  That *is* our build step.

## 7. Language and stack options (to decide after the POC)

| Option | Display core | Build adapters | Pros | Cons |
|---|---|---|---|---|
| **Python** (pywin32, pywinauto, Pillow) | Reuse DoomPaint's MIT `clipserve.py`/`paint_out.py` almost as-is | Node CLIs (Slidev/Marp) + Playwright-Python | Fastest POC, since the code exists | Runtime packaging on a corporate laptop (Python + venv) |
| **C# / .NET 8+** | `ComTypes.IDataObject` or a WinForms `DataObject` subclass, FlaUI for UIA | Node CLIs, Playwright .NET, PowerPoint interop, CefSharp | First-class COM/OLE/UIA/WGC. Single-file exe | More upfront code |
| **PowerShell 5.1 + `Add-Type` C#** | Inline C# implementing `IDataObject` | Node CLIs | **Zero install** on a locked-down laptop | Awkward for a long-lived app. Fine for experiments |
| Rust (`windows` crate) | Hand-rolled COM | Node CLIs | Single tiny exe | Most COM boilerplate |

Build orchestration fits a bash/Make (or `just`) workflow:
`build` = adapter → bundle, `present` = run on Windows. A "plan/apply" feel:
`mpp build deck.md` → `bundle/`, `mpp present bundle/`.

## Sources

- Slidev: <https://sli.dev/guide/exporting>, <https://sli.dev/builtin/cli>, <https://github.com/slidevjs/slidev/blob/main/packages/slidev/node/cli.ts>
- Marp CLI: <https://github.com/marp-team/marp-cli>. Marpit fragments: <https://raw.githubusercontent.com/marp-team/marpit/main/docs/fragmented-list.md>
- reveal.js API: <https://revealjs.com/api/>, PDF export: <https://revealjs.com/pdf-export/>
- decktape: <https://github.com/astefanutti/decktape>
- Quarto: <https://quarto.org/docs/presentations/revealjs/presenting.html>, <https://quarto.org/docs/presentations/powerpoint.html>
- Playwright screenshot options: <https://github.com/microsoft/playwright/blob/main/docs/src/api/params.md>. Screencast: <https://playwright.dev/docs/api/class-screencast>
- CDP protocol: <https://github.com/ChromeDevTools/devtools-protocol/blob/master/json/browser_protocol.json>
- WebView2 CapturePreviewAsync: <https://learn.microsoft.com/dotnet/api/microsoft.web.webview2.core.corewebview2.capturepreviewasync>. Windowless request: <https://github.com/MicrosoftEdge/WebView2Feedback/issues/20>
- Windows.Graphics.Capture: <https://learn.microsoft.com/windows/apps/develop/media-authoring-processing/screen-capture>
- CEF render handler: <https://github.com/chromiumembedded/cef/blob/master/include/cef_render_handler.h>. CefSharp: <https://github.com/cefsharp/CefSharp/wiki/General-Usage>
- PowerPoint Slide.Export: <https://learn.microsoft.com/office/vba/api/powerpoint.slide.export>. GotoClick: <https://learn.microsoft.com/office/vba/api/powerpoint.slideshowview.gotoclick>. Export resolution: <https://learn.microsoft.com/office/troubleshoot/powerpoint/change-export-slide-resolution>
- Unattended Office automation: <https://learn.microsoft.com/office/client-developer/integration/considerations-unattended-automation-office-microsoft-365-for-unattended-rpa>
- PPspliT: <https://github.com/maxonthegit/PPspliT>
- LibreOffice convert filters: <https://help.libreoffice.org/latest/en-US/text/shared/guide/convertfilters.html>
- Aspose.Slides animations: <https://docs.aspose.com/slides/net/convert-powerpoint-to-video>. Syncfusion: <https://help.syncfusion.com/file-formats/presentation/convert-powerpoint-to-image-in-console-application>
- pdfpc: <https://github.com/pdfpc/pdfpc>. presenterm: <https://mfontanini.github.io/presenterm/features/speaker-notes.html>
