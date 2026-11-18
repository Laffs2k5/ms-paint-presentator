# Research: streaming pictures/video into MS Paint

> Status: research notes, gathered 2026-11-18. Purpose: prior art and technical
> groundwork for **ms-paint-presentator**, a tool to run presentations in
> Microsoft Paint instead of PowerPoint.

## TL;DR

- **Episode:** _Scott & Mark Learn To… Rethink Authorship_ (published **2026-08-12**),
  at about **26:00–29:10**. Mark describes Doom-in-Paint: a "choppy" first version at
  30–40 fps that used "the clunky paste operation", and a second version where the
  AI found the **OLE interface** so the frame is rendered "right into the clipboard"
  and Paint is just told it's there. He reports 60–70 fps, capped at Doom's 35. See §1.
- The thing Mark Russinovich built is **[DoomPaint](https://github.com/markrussinovich/DoomPaint)**
  (MIT, July 2026). Doom (ViZDoom engine, headless) renders frames, and every frame
  is **put on the Windows clipboard as a `CF_DIB` and pasted into Paint with a
  synthetic Ctrl+V**. It reaches **~33 pastes/s at 640×400** and Doom's full
  **35 fps at 320×200**. A synthetic regression test hit **54 pastes/s**.
- The **slow vs fast** split you remembered is about how the clipboard is fed:
  - **Slow (naive): `OpenClipboard` → `EmptyClipboard` → `SetClipboardData` on every
    frame**, later with delayed rendering (`WM_RENDERFORMAT`). Paint reads the
    clipboard asynchronously. Emptying it for the next frame frees the bytes while
    Paint is still reading, and Paint shows a modal **"Can't complete operation"**
    error. You then need settle timers and a dialog-killer watchdog, so throughput
    stays low (a 5–15 fps estimate in the original plan).
  - **Fast: one OLE `IDataObject` published once with `OleSetClipboard`**, whose
    `GetData` hands Paint the *newest* frame at read time. The clipboard is never
    rewritten during play, so the race cannot happen. Each `GetData` call also tells
    you Paint consumed the frame, which paces the loop. The commit message says this
    *"roughly tripled paint-side throughput"*.
  - There is also a secondary slow/fast split in **how the paste is triggered**: a
    synthetic **Ctrl+V** (fast, preferred) vs **Edit > Paste through UI Automation**
    (slow, opens the menu every frame, used only as a fallback when a Paint build
    drops synthetic keys).
- **Paint's cost per frame scales with pixel count**, not with the number of pastes.
  Resolution is the biggest performance lever.
- Every paste is an **undo step**, so Ctrl+Z steps backwards through frames. That is
  directly useful for slide navigation.
- Apart from DoomPaint and its forks, prior art is thin. The rest is mouse-driven
  "draw the image with the pencil tool" bots (very slow) and a Paint MCP server
  that draws primitives. Nothing presentation-oriented exists, so this repo has no
  competition.

## 1. The podcast episode

**Episode: _Scott & Mark Learn To… Rethink Authorship_**, published **2026-08-12**
(38 min). The Paint segment runs from about **26:00 to 29:10**, followed by a short
on-screen demo attempt and then Mark's prompt-generated "Death Star" Doom level
(30:14 onward).

- Audio (RSS enclosure): <https://traffic.megaphone.fm/DSNIU3386929488.mp3>
- YouTube: <https://www.youtube.com/watch?v=0c0evW57Klk>
- Feed: <https://feeds.megaphone.fm/DSNIU3534204567>

No official transcript is published, and YouTube blocked caption download from this
environment, so the segment was transcribed locally with Whisper (`base.en`) to
write the paraphrase below. The transcription itself is third-party content and is
not kept in this repo.

What Mark says, paraphrased with timestamps:

- **[26:00–26:35]** He asked the AI "what has Doom not run in?" Existing Doom-in-Paint
  attempts "are not good". One literally draws each frame with brush and pen,
  **about 10 minutes per frame**, and the video is sped up. Nobody had used Paint
  "natively as a display". With some guidance, he had Doom playing in Paint
  within about an hour.
- **[26:44–27:23] Mechanism:** an off-screen buffer is rendered "onto the clipboard"
  and pasted into Paint, which Scott calls "double buffering in Paint".
- **[27:33–27:46] The slow version:** "the first version would be kind of choppy… depending
  on your machine, get 30, 40 frames a second", and slower again at 2× resolution.
- **[27:49–28:12] The fast version:** he told the AI "it's really choppy and uneven,
  look at ways to speed up the frame rate". It came back with **the OLE interface**:
  *"Paint actually listens to this. And so we can avoid the clunky paste operation and
  just render right into the clipboard and then just let paint know it's there."*
- **[28:13–28:30] Result:** *"now the frame rate can be 60, 70 frames per second"*, capped
  at Doom's native **35 fps**. *"It plays buttery smooth"* and it's async: Doom just
  updates the clipboard 35 times a second and Paint grabs the latest.
- **[28:34–29:05]** Scott compares it to OLE embedding, as when you pasted an Excel
  chart into Word and Excel notified Word when the chart changed. Mark: "you're in
  the middle of a paste… **this paste is one that never completes. It just gets
  updated**."
- **[29:09–30:10]** Scott asks if it can be rotated while running. Mark tries to show it,
  but the game "grabs back control" (the keyboard hook) and the demo doesn't work.

**How the podcast matches the published code.** The podcast simplifies a little. The
code in the repo (last commit 2026-07-20) **still sends a synthetic Ctrl+V for every
frame**. The OLE change is what Paint *reads from* on each paste. Paint does not
subscribe for change notifications: `clipserve.py` returns
`OLE_E_ADVISENOTSUPPORTED` for `DAdvise`. The real wins are:

1. no `EmptyClipboard`/`SetClipboardData` churn and no error dialogs,
2. `publish()` no longer blocks on clipboard work ("roughly tripled paint-side
   throughput", commit `b1c1a30`), and
3. `GetData` serves the freshest frame at read time.

So "the paste that never completes" is the right mental model for the *data*, but a
paste gesture is still triggered for each frame. The 60–70 fps figure is consistent
with the 54 pastes/s measured in the repo's synthetic regression test.

## 2. DoomPaint: how it works

Repo: <https://github.com/markrussinovich/DoomPaint> by Mark Russinovich.
Created 2026-07-18, last commit 2026-07-20, MIT. Python 3 + pywin32 + pywinauto +
Pillow + numpy. Press coverage started around 2026-08-03 (see Sources). The commit
trailers show the project was AI-assisted (Claude).

### 2.1 Architecture

```
┌────────────┐ numpy RGB  ┌──────────────┐  OLE IDataObject (CF_DIB)  ┌──────────┐
│ ViZDoom    │──────────▶ │ paint_out.py │ ── synthetic Ctrl+V ─────▶ │ MS Paint │
│ (headless, │            │ clipserve.py │ ◀── GetData() = "consumed" │  canvas  │
│  35 Hz)    │ ◀───────── │ keys.py      │ ◀── WH_KEYBOARD_LL hook ── │ (focused)│
└────────────┘   input    └──────────────┘                            └──────────┘
```

Files of interest, all under `mspaintdoom/`:

| File | Role |
|---|---|
| `clipserve.py` | Clipboard owner: one `IDataObject` on a dedicated STA thread with a message pump |
| `paint_out.py` | Finding/launching Paint, focus guard, `KeyPaster` and `MenuPaster`, canvas priming, zoom fitting, error-dialog watchdog |
| `sendinput.py` | `SendInput` wrapper for the Ctrl+V chord |
| `keys.py` | Low-level keyboard hook that swallows game keys while Paint is foreground |
| `capture.py` | Captures the Paint window, used by self-tests to verify a paste landed |
| `exp_ole_clip.py` | Experiment that proved the OLE approach. It logs every `QueryGetData`/`GetData` that Paint makes |

### 2.2 The slow path: naive clipboard rewrite

The first, unpublished version used the obvious Win32 sequence for every frame. The
first public commit had already moved to Win32 delayed rendering (`WM_RENDERFORMAT`).
See [02 – OLE deep dive](02-ole-and-paint-integration-surface.md#11-three-generations-of-clipboard-feeding).

```
OpenClipboard(hwnd)
EmptyClipboard()
SetClipboardData(CF_DIB, hGlobal)   # later: delayed rendering via WM_RENDERFORMAT
CloseClipboard()
SendInput(Ctrl+V)
```

The problem, from the README and commit `b1c1a30`:

> Paint processes each paste asynchronously, reading the image off the clipboard on
> its own schedule. Rewriting the clipboard while that read is still in flight makes
> the read fail, and Paint responds with a modal **"Can't complete operation"** error
> that silently kills the paste. [...] Fixed settle timers can't close that race
> (paste latency varies wildly with frame size).
>
> `WM_RENDERFORMAT` says when a paste starts reading, nothing says when it finishes.

Workarounds that were tried before the rewrite (commit `d8693a0`): settle timers and
a watchdog thread that finds and dismisses the error dialog "in a blink". These
helped but did not fix the race, and they cap throughput.

### 2.3 The fast path: a live OLE data object, published once

```
OleInitialize()                      # on a dedicated STA thread with a message pump
OleSetClipboard(myDataObject)        # ONCE per session
loop:
    myDataObject.frame = newest_frame   # in-place update, no clipboard API call
    SendInput(Ctrl+V)
    wait until Paint calls myDataObject.GetData(CF_DIB)   # = "frame consumed"
on exit:
    OleFlushClipboard()              # leave real bytes so the last frame stays pasteable
```

Key properties:

- **No clipboard churn.** The data object is reference-counted, so a read that is
  still in flight stays valid even as the next frame arrives. The error dialog
  becomes impossible.
- **Render on demand.** `publish(render_fn=...)` registers a thunk, and the DIB is
  produced inside `GetData` when Paint reads it. The displayed frame is therefore the
  freshest one, which lowers latency.
- **Built-in back-pressure.** `GetData` doubles as a "consumed" signal. The loop waits
  for (a) Paint to have read the previous frame and (b) the engine to have produced
  a new one. The effective rate is `min(35 Hz, what this machine's Paint can
  composite)`, with no constant to tune.
- **Clipboard theft handling.** An `AddClipboardFormatListener` / `WM_CLIPBOARDUPDATE`
  listener on a message-only window flags when another app takes the clipboard.
  Ownership is reclaimed on the *next publish*, not on a timer, so a copy the user
  makes while paused is not stolen back.
- **Clean shutdown.** `OleFlushClipboard` runs through a synchronous `SendMessage` to
  the STA thread. If the process exited while Paint still pointed at the dead data
  object, the error dialog would appear again on quit.
- Only `CF_DIB` (format 8) is offered. `exp_ole_clip.py` logs which formats Paint
  asks for, which is worth re-running on our target Paint build.

### 2.4 Triggering the paste

| Method | Speed | Notes |
|---|---|---|
| `SendInput` Ctrl+V chord | Fast | Opens no menu. **Some Paint builds silently drop synthetic Ctrl+V**, so a self-test at startup decides. Releases a held Shift first, because Ctrl+Shift+V is not paste. Leaves Ctrl alone if the user is holding it. |
| UI Automation: expand `Edit` menu item, invoke `Paste` | Slow | Rock solid, but opens the Edit menu every frame and can swallow keystrokes. Fallback after 8 consecutive missed `GetData` signals. |

Both are guarded by a **focus check**: never inject keys unless Paint is the
foreground window. If the user alt-tabs, pause.

### 2.5 Canvas handling tricks

- **Paste = floating selection; the next paste commits it.** No Esc or click is needed
  to commit. Esc was dangerous because, with Ctrl held, it becomes Ctrl+Esc and
  opens the Start menu. The last frame stays floating on exit.
- **Every frame is an undo step**, so Ctrl+Z rewinds. File > Save gives a real PNG of
  the current frame.
- **Exact-size canvas without a resize dialog:** drag the canvas's bottom-right
  resize handle down to 1×1 (pywinauto finds it in the `MSPaintView` window), then
  let Paint auto-expand the canvas on the first paste to exactly the frame size.
- **Zoom is free scaling.** Paint's view zoom is display-only and nearest-neighbour
  at integer ratios. Pasting 640×400 at 200% zoom looks identical to pasting
  1280×800 and costs a quarter of the pixels. DoomPaint uses "Fit to window", then
  steps down with Ctrl+PageDown to the nearest whole-pixel ratio.
- UIA anchors found in the Win11 Paint tree: `CropButton` (enabled ⇔ a selection
  exists, used to detect "paste landed"), `ZoomSliderControl`, `scrollViewer`, the
  `Edit` → `Paste` menu items, and the `MSPaintView` window class.

### 2.6 Input

A `WH_KEYBOARD_LL` low-level keyboard hook captures and **swallows** game keys while
Paint is foreground, so Paint never sees them. Without it, arrow keys nudge the
floating selection or dismiss menus, and frames stall for seconds. It falls back to
`GetAsyncKeyState` polling. It also beats PowerToys-style hooks to Ctrl. F12 is a
panic quit.

For presentations this is the clicker/remote problem. Most presenter remotes send
PageUp/PageDown, arrow keys or B (black screen), and we need to swallow them in the
same way.

### 2.7 Measured performance (from commits/README)

| Resolution | Rate | Context |
|---|---|---|
| 640×400 (default) | ~33 pastes/s | 25 s live-game soak, autofit at 150% |
| 1280×800 display (640×400 at 2× zoom) | ~33 pastes/s | `--scale 2` |
| synthetic 640×400 test | 54 pastes/s | regression run, 80 frames, 0 dialogs |
| 320×200 | 35 fps (engine-capped) | Doom's native tic rate |
| original plan estimate (naive clipboard) | 5–15 fps | 30–100 ms per clipboard + paste round trip |

Caveats: this is one machine. Paint cost is dominated by pixel count, so a 1920×1080
slide is about 8× the pixels of 640×400. Expect it to be much slower per frame, which
is fine for static slides but matters for video and animated transitions. A useful
trick is to paste at a lower resolution and let Paint's zoom upscale.

### 2.8 Known gotchas (relevant to us)

- Synthetic Ctrl+V reliability varies by Paint build and even from run to run. Always
  self-test.
- Paint must be **foreground**, which conflicts with presenter view on a second
  screen and needs design work.
- The undo history grows by one step per frame. Memory use is unknown for long runs
  at high resolution.
- Clipboard managers such as Win+V history, Ditto or remote-desktop clipboard sync
  may read or race the clipboard. Check this with RDP or Teams screen sharing.
- Remote desktop and VMs may not pass Ctrl through. DoomPaint binds fire to F as
  well.

## 3. Other prior art

| Project | Approach | Relevance |
|---|---|---|
| [gmarav/DoomCLI](https://github.com/gmarav/DoomCLI) | Fork of DoomPaint that renders to a text terminal instead of Paint | Shows the engine/display split is easy to swap |
| [ghuntley/mcp-server-microsoft-paint](https://github.com/ghuntley/mcp-server-microsoft-paint) ([blog](https://ghuntley.com/mcp/)) | Rust MCP server (JSON-RPC over stdio) that drives Paint to draw lines, pixels and shapes, select tools and set colours. Uses UI Automation (`uiautomation-rs`) plus input simulation | The "Paint as an API" idea. Too slow for frames, but could let an LLM annotate a slide live |
| [VACEfron/AutoPaint](https://github.com/VACEfron/AutoPaint), [sirbread/pythcasso](https://github.com/sirbread/pythcasso), [lemonyte/picture-painter](https://github.com/lemonyte/picture-painter), automation9417/mspaint-draw-img ([article](https://dev.to/kayyolo/pythoncontrol-ms-paint-to-draw-a-pixel-style-logo-automatically-50d4)) | Move the real mouse and draw the image pixel by pixel or stroke by stroke with Paint's own tools | **Slowest possible method**, seconds to minutes per image, and it hijacks the cursor. As a *deliberate effect* (a slide that "draws itself") it is fun. DoomPaint's `PLAN.md` lists this as a stretch "art mode" |
| Raymond Chen, *[The Old New Thing, 2025-10-14](https://devblogs.microsoft.com/oldnewthing/20251014-00/?p=111681)* ([PC Gamer write-up](https://www.pcgamer.com/software/windows/windows-used-to-secretly-use-green-screens-to-render-videos-which-is-how-you-could-trick-ms-paint-into-becoming-a-video-player)) | Windows 98-era video players used DirectDraw **overlays with a colour key**. Painting with the key colour in Paint showed the live video "inside" Paint | Historical curiosity only. **Doesn't work on modern Windows** because DWM composition replaced overlays |
| The brush-and-pen Doom-in-Paint that Mark mentions on the podcast (26:13; not identified) | Draws each frame with Paint's brush and pen, ~10 minutes per frame, then speeds up the video | Same family as method E below |
| Earlier "Doom in Paint" stunts (mentioned by [Windows Latest](https://www.windowslatest.com/2026/08/05/i-played-doom-inside-ms-paint-on-windows-11-by-hijacking-the-clipboard-and-its-shockingly-good/), no specific repo found) | Run the game in another window and mirror or screen-capture it into Paint | Not "real" Paint edits |

GitHub searches that returned nothing relevant: `mspaint clipboard`, `mspaint frames`,
`mspaint stream`, `paint.exe video player`, `mspaint bad apple`,
`mspaint presentation`, `MS Paint as a monitor`, `mspaint dll injection`. Code
search for `OleSetClipboard mspaint` only hits DoomPaint and old NT source trees.
**No existing "presentation in Paint" tool was found.**

## 4. Methods for getting pixels into Paint, compared

| # | Method | Throughput (est.) | Real Paint edit? | Fragility | Notes for presenter |
|---|---|---|---|---|---|
| A | Clipboard, naive `SetClipboardData` + Ctrl+V | Low (5–15 fps, error dialogs) | Yes | High | Fine for one paste per slide change if we wait between pastes |
| B | **Clipboard, OLE `IDataObject` + Ctrl+V** | **High (30–55 fps at 640×400)** | Yes | Medium (Ctrl+V self-test) | **Default choice.** Handles slides, transitions and short clips |
| C | Clipboard + UIA Edit > Paste | Low–medium | Yes | Low | Fallback |
| D | Open the file: `mspaint.exe slide.png`, or File > Open through UIA | Very low (window or document reload) | No (new document each time) | Medium | Simple v0, but no undo-stack navigation and visible reload flicker |
| E | Mouse-driven drawing with Paint tools | Seconds–minutes per image | Yes | High (cursor hijack) | "Hand-drawn reveal" effect only |
| F | Transparent overlay window on top of Paint's canvas | Very high (GPU) | **No, cheating** | Low | Defeats the joke. Avoid, or keep for a "video" fallback |
| G | Inject into Paint (DLL, hook D2D/canvas memory) | Highest | Sort of | Very high (packaged MSIX app, breaks on updates, looks like malware to EDR) | Not worth it |
| H | Pre-load all slides, then navigate with **Ctrl+Z / Ctrl+Y** | Instant | Yes | Unknown: undo-depth limit untested | **Promising: test this** |
| I | Paint **layers** (Win11 Paint since 2023) and toggling visibility through UIA | Instant (hypothesis) | Yes | Unknown | Test whether layers are UIA-scriptable and how many are allowed. `.paint` project files could hold a whole deck |

## 5. Implications for ms-paint-presentator

Initial thoughts, not decisions:

1. **Start from DoomPaint's display layer** (`clipserve.py` + `paint_out.py`). It is
   MIT, so we can reuse it with attribution. The OLE clipboard server, Ctrl+V/UIA
   self-test, focus guard, keyboard hook, canvas priming and zoom fitting are exactly
   the plumbing a slide player needs. The Doom engine is replaced by a "frame source".
2. **Frame sources** to support:
   - Static slides: PNGs exported from anything (PowerPoint export, Marp/reveal.js →
     PNG, Excalidraw, hand-drawn in Paint itself).
   - Transitions: generate N intermediate frames (wipe, dissolve, "Paint bucket fill"
     style) and stream them through path B.
   - Video clips: decode with ffmpeg/PyAV to the canvas resolution and stream at
     ≤30 fps. Audio plays separately and is synced to `GetData` consumption, as
     DoomPaint did with music.
   - Live content: a terminal capture, demo window capture or webcam, for the
     "screen share inside Paint" gag.
3. **Resolution strategy:** author slides at 1920×1080 but stream at 960×540 with
   Paint zoom at 200%. That is a quarter of the pixels with crisp nearest-neighbour
   scaling, and keeps transitions smooth. Measure first.
4. **Navigation:** a keyboard hook that captures clicker keys (PageUp/PageDown,
   arrows, B, F5, Esc). Forward = paste the next slide. Back = Ctrl+Z (if undo
   depth allows) or re-paste the previous slide.
5. **Presenter view:** Paint must be foreground on the projector. Speaker notes would
   need a second window that never takes focus (`WS_EX_NOACTIVATE`), on a second
   monitor or a phone. This is an open design problem.
6. **Tech stack:** DoomPaint shows Python + pywin32 + pywinauto works and is quick
   to iterate on. A single-binary alternative is Rust (`windows` crate + `uiautomation`)
   or C#/.NET. To be decided. Build and release can be scripted in bash/CI.

## 6. Open questions and experiments (need a Windows 11 box)

- [ ] Which Paint version or build is installed (`Get-AppxPackage Microsoft.Paint`),
      and does synthetic Ctrl+V work on it? Run DoomPaint's `test_quiet.py`.
- [ ] Throughput at 960×540, 1280×720 and 1920×1080 with the OLE path.
- [ ] Undo depth: how many pastes can Ctrl+Z walk back? What is the memory cost per
      undo step at 1080p?
- [ ] Are Paint layers UIA-scriptable (add layer, toggle visibility, reorder)? Is
      there a layer cap?
- [ ] Can a `.paint` project file be generated offline, so a whole deck opens as one
      file? The format is undocumented.
- [ ] Behaviour when presenting over Teams/Zoom screen share and through RDP.
- [ ] Does the Paint "Fit to window" zoom plus full-screen view (F11?) give a clean
      chrome-free projector view?
- [ ] Interaction with Windows clipboard history (Win+V) and clipboard managers.
      Can our data object opt out with `ExcludeClipboardContentFromMonitorProcessing`
      / `CanIncludeInClipboardHistory` formats?

## Sources

- **Scott & Mark Learn To… Rethink Authorship** (2026-08-12), 26:00–29:10: [audio](https://traffic.megaphone.fm/DSNIU3386929488.mp3), [YouTube](https://www.youtube.com/watch?v=0c0evW57Klk)
- DoomPaint repo, README, PLAN.md and commit history: <https://github.com/markrussinovich/DoomPaint>
- Windows Latest, 2026-08-05: <https://www.windowslatest.com/2026/08/05/i-played-doom-inside-ms-paint-on-windows-11-by-hijacking-the-clipboard-and-its-shockingly-good/>
- Tom's Hardware: <https://www.tomshardware.com/video-games/retro-gaming/microsoft-paint-used-as-a-monitor-to-run-doom-at-up-to-35-fps-project-released-by-firms-azure-cto-runs-actual-doom-engine-and-loads-real-shareware-doom1-wad>
- The Register, 2026-08-06: <https://www.theregister.com/offbeat/2026/08/06/azure-cto-pastes-doom-into-paint-one-frame-at-a-time/5283559>
- Gizmodo: <https://gizmodo.com/today-in-can-it-run-doom-microsoft-paint-2000793927>
- XDA Developers: <https://www.xda-developers.com/microsoft-executive-got-doom-running-in-paint-using-clipboard-hack/>
- TechSpot: <https://www.techspot.com/news/113328-doom-now-runs-microsoft-paint-thanks-wildly-inventive.html>
- Windows Central: <https://www.windowscentral.com/gaming/pc-gaming/doom-runs-in-paint-built-by-azure-microsoft-executive>
- Raymond Chen, The Old New Thing, 2025-10-14: <https://devblogs.microsoft.com/oldnewthing/20251014-00/?p=111681>
- Scott & Mark Learn To… RSS feed: <https://feeds.megaphone.fm/DSNIU3534204567>, Apple Podcasts: <https://podcasts.apple.com/us/podcast/scott-mark-learn-to/id1773124889>
- Paint layers, Windows Central: <https://www.windowscentral.com/software-apps/windows-11/whats-new-with-the-ms-paint-layers-september-update-for-windows-11>
- ghuntley Paint MCP server: <https://ghuntley.com/mcp/>
