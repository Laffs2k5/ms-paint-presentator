# POC plan: prove it on the Windows 11 laptop

Goal: answer the open questions from the research before choosing an architecture
and a language. Each experiment is small, standalone, and records its result in
`docs/poc-results.md`.

Research it builds on:
[01 streaming](research/01-streaming-frames-into-ms-paint.md) ·
[02 OLE surface](research/02-ole-and-paint-integration-surface.md) ·
[03 authoring/rendering/PPTX](research/03-authoring-rendering-and-pptx.md)

## Presentation constraints (decided)

These are fixed, not open questions. Experiments measure within them.

- **Paint stays recognisably Paint.** Title bar, menus, toolbar and status bar
  stay visible: no View > Full screen, no hidden toolbar. Seeing that it really is
  Paint is the point of the project. Without the chrome it is just another window.
- **Paint runs maximised at 100% zoom** on the presenting display.
- **Slides fill the display area.** The display area is the largest canvas that
  maximised Paint shows completely at 100% zoom: the screen minus the taskbar,
  Paint's title bar, toolbar and status bar, and the margin Paint keeps around
  the canvas. It depends on the display's resolution, scaling and taskbar.
- **The reference screen is 16:10** (for example 1920×1200 or 2560×1600). Once
  the chrome is subtracted its display area is much wider than 16:10, and that
  is the shape slides are designed for.
- **Slides are never scaled or distorted by Paint.** No Fit to window and no
  fractional zoom. One slide pixel is one screen pixel.
- **The presenter adapts to the screen.** When the presentation starts it
  measures the display area and renders each slide at the largest size of the
  reference shape that fits. On another screen shape the slide keeps its shape
  and is **centred, with Paint's own background around it**: the canvas is the
  slide's size, not the display area's. Slides do not reflow. Keep viewport −
  slide even on both axes (E11).

## Presenter design (decided)

Decided from the results so far. Experiments still open are named where a
point depends on them.

- **A slide change is a paste.** Each slide is a PNG in a new OLE clipboard
  object (E1, E7), pasted and committed with `S` (E14). PNG is the only image
  format offered, plus the clipboard-history opt-out (E8). Paint never notices
  a clipboard change by itself (E5), so there is no way around the paste.
- **Navigation is re-paste only.** Back, Home, End and go-to-slide paste their
  target like Next does, so every slide is one paste away. The undo stack (E7)
  is not used: its depth is limited, any paste or stray click cuts off redo,
  and its state can drift from the deck's.
- **Keys come through a low-level keyboard hook, not a window** (E10). Paint
  stays in front with its title bar active. The hook swallows the show keys
  only while Paint or the presenter view is in front, so other apps keep their
  keys, and it lets our own injected keys through. The keys are PowerPoint's
  slide-show set, which presenter remotes are built for: Next on Right, Down,
  Space, Enter, PageDown, N; Previous on Left, Up, Backspace, PageUp, P; Home,
  End, a number + Enter to jump; B or `.` black, W or `,` white; Esc ends. With
  Paint in front the slide change uses Paint's shortcuts (Ctrl+V, `S`); with
  the presenter view in front, the background route (E2). If security software
  objects to the hook, fall back to `RegisterHotKey` while Paint is in front.
- **Slides are rendered at start, for the chosen screen.** The start-up flow:
  1. **Screen picker** when there are several displays: each with resolution,
     scaling and predicted slide size, an Identify button that shows a big
     number on each screen, the non-laptop screen as default, and the last
     choice remembered.
  2. Open Paint maximised there at 100%, apply the E4 checklist, measure the
     display area and calibrate with a test paste (E11).
  3. Render every slide from the source at exactly that pixel size, never
     resampled (E12). Cache the result per deck and slide size.
  4. Pre-flight checks, then wait for the first Next.
- **The loading painting.** While the slides render, the presenter draws a
  deliberately crappy painting live in Paint, with Paint's own tools (E15–E17):
  - a hand-drawn title: letter outlines from an installed font, taken at
    runtime so no font data ships with the repo, wobbled (uneven baseline,
    overshoot, a shaky hand) and drawn stroke by stroke, random on every run;
  - a progress bar coloured in as rendering really progresses;
  - classic Paint mishaps, staged: the bucket fill that leaks through a
    one-pixel gap and floods the sky.

  The painting stays on the canvas until the first Next. If live drawing does
  not work, ready-made pieces are stamped in place by paste instead (E17).
  It uses Paint's 20 palette colours (E16). Paint remembers only the Text
  tool's font across sessions, so the presenter puts that back if it changes it.

Planned features:

- **Presenter view** on the other screen: current and next slide, notes, a
  timer, slide n of N. It needs no focus, since the hook gets the keys.
- **Pre-flight checks:**
  - the charger is connected (Paint is slower and less steady on battery, E6);
  - Do not disturb is on (E19);
  - the screen is kept awake for the whole show (`SetThreadExecutionState`);
  - the paste routes pass a self-test (E2).
- **Build steps are whole slides** (Slidev `--with-clicks`), so Back works the
  same way for them. Black and white screens are pre-rendered slides too.
- **Recovery:**
  - close Paint's "Can't complete operation" dialog automatically (`paintctl dismiss`);
  - measure and render again behind a fresh loading painting when the display
    changes (E18);
  - relaunch Paint and resume at the current slide if it closes.
- **The mouse stays Paint's.** The presenter parks the cursor off the canvas,
  and the speaker can pick up Paint's pen and draw on a slide; the next slide
  pastes over it.

## 0. Environment facts to capture first

```powershell
winver                                                       # Windows build
Get-AppxPackage Microsoft.Paint | Select Name, Version        # Paint build
$PSVersionTable.PSVersion
python --version; node --version; dotnet --list-sdks          # what's allowed on the laptop
# Paint manifest: COM servers, protocols, file associations (doc 02 §3.6)
$loc = (Get-AppxPackage Microsoft.Paint).InstallLocation
Select-String -Path "$loc\AppxManifest.xml" -Pattern 'com:|ComServer|protocol|fileTypeAssociation|AppExecutionAlias'
reg query "HKCR\Paint.Picture" /s
```

Also: is PowerPoint installed? Is there a clipboard manager, DLP agent or EDR that
might object to `SendInput` or keyboard hooks?

## 0b. Instrumentation: get `paintctl` working

[`poc/paintctl`](../poc/paintctl) (see [research 04](research/04-instrumenting-paint.md))
is untested, so fix it first. It is the measuring tape for every experiment below.

```powershell
cd poc\paintctl; uv sync
uv run paintctl launch --canvas 1280x720 --maximize
uv run paintctl info                      # check: viewport, canvas_on_screen, canvas_px, zoom, dpi
uv run paintctl zoom 100; uv run paintctl info
uv run paintctl fit-viewport 1920 1080    # if the screen allows
uv run paintctl screenshot vp.png --region viewport
uv run paintctl uia-dump --depth 10 > docs\uia-paint-<build>.json   # commit this, it feeds E4
claude mcp add paintctl -- uv run --extra mcp --directory $PWD paintctl-mcp   # optional
```

Pass: `info` returns no `errors`, and `zoom_check` expected ≈ actual.

## 1. Baseline: does DoomPaint run?

Clone <https://github.com/markrussinovich/DoomPaint> and run `run.bat`, then
`test_quiet.py`. This confirms the OLE clipboard + Ctrl+V path works on *this*
Paint build. Note which paster the self-test picks (Ctrl+V or UIA menu) and the
`pastes/s` figure.

## 2. Experiments (priority order)

| # | Question | How | Pass |
|---|---|---|---|
| **E11** | What is the display area? | Paint maximised at 100% zoom. Find the largest canvas that is fully visible with no scrollbars, for each display resolution and scaling at hand (and on a projector). Note the taskbar's effect. Paste a test pattern of that size and compare a screenshot with the source. Derive a formula to predict the display area of other screens (16:10 reference screens, projectors) | Display area recorded per display, plus the formula. Screenshot of the canvas is pixel-identical to the slide |
| **E6** | How fast is a slide-sized paste? | Live `IDataObject`, paste N distinct frames at the display areas from E11 (for example maximised on 1920×1080 and 2560×1440), measure keypress→`GetData`→canvas-changed (`PrintWindow` polling) | p95 ≤ 150 ms at the display area of a typical projector |
| **E2/E3** | Can we paste **without focus/keystrokes**? | `PostMessage(hwndPaint, WM_COMMAND, 0xE125, 0)` with Paint in the background. If no luck, log `WM_COMMAND` while clicking Edit > Paste to find the real ID | `GetData` called and canvas updated with Paint not foreground |
| **E1** | Which formats does Paint consume? Does alpha work? | Offer `PNG` + `CF_DIBV5` + `CF_DIB`. Log `QueryGetData`/`GetData` order | Format preference recorded. Transparent overlay works or doesn't. **Result: PNG, alpha works (E1)** |
| **E7** | Undo stack as slide history | Paste 50 slides, then Ctrl+Z ×50 / Ctrl+Y ×50. Watch Paint's RAM | Max depth + RAM per step recorded |
| **E4** | What can UIA drive? | Accessibility Insights / `inspect.exe` / `paintctl uia-dump`: Edit > Paste menu item (there is no toolbar Paste button), Layers panel, zoom | AutomationIds table recorded. **Done (E4), plus a start-up checklist** |
| **E8** | Keep slides out of Win+V history | Add `ExcludeClipboardContentFromMonitorProcessing` + `CanIncludeInClipboardHistory=0` + `CanUploadToCloudClipboard=0` | Win+V stays clean. **Done (E8): add `ExcludeClipboardContentFromMonitorProcessing`** |
| **E5** | Does anything call `DAdvise`? | Implement it with `CreateDataAdviseHolder` and log | Documented yes or no. **No (E5)** |
| **E10** | Keys and clicker | A low-level keyboard hook that swallows the show keys only while Paint or the presenter view is in front and lets injected keys through. The callback only queues the key (Windows silently drops a hook that is too slow). Keyboard first, then your presenter remote's keycodes. Fallback: `RegisterHotKey` while Paint is in front | Next/prev/black work, Paint never sees the keys, other apps keep theirs |
| **E12** | Build pipeline smoke test | `npx @slidev/cli export --format png --with-clicks` and `npx @marp-team/marp-cli --images png` on a sample deck, plus a PowerPoint COM `Slide.Export` of a sample `.pptx`, each at an exact pixel size measured at start (for example 1854×875) | PNGs at exactly that size, rendered from the source rather than resampled; time per slide recorded (it paces the loading painting); notes extracted |
| **E14** | Commit a pasted slide cleanly | A paste is a floating selection with a dashed border and a toolbar over it. Esc discards it, Enter does nothing, and a click outside commits but moves the mouse and can hit a resize handle (E11). Try: the next paste committing the previous one, switching tool through UIA, keyboard tool shortcuts, a click while the cursor is hidden | Slide shows with no selection marks and no visible mouse movement, pixel-identical |
| **E15** | Draw in Paint with synthetic input (the loading painting) | Strokes with the Pencil and a brush by three routes: `SendInput` mouse drags (move the real cursor), synthetic pen input (`InjectSyntheticPointerInput`: pressure, and the cursor stays put), mouse messages posted to `InputSiteWindowClass` (as `S` in E2). Find how fast points can be fed before Paint drops or smooths them | A scripted stroke lands where intended and shows live, at hand-drawing speed. **Pass (E15): pen and mouse both exact and live; the pen leaves the cursor alone and has pressure on the Watercolor and Oil brushes; posted messages draw nothing** |
| **E16** | Pick tools, colours and sizes by script | Brush types, brush size, palette and custom colours, Shapes, Fill and Text, through keys or UIA (E4 covered only Pencil, Eraser and Brushes). Check whether Paint carries our choices into the user's next session | Every tool the loading painting uses can be selected by script, and the user's settings can be restored. **Pass (E16): keys, menus, pen taps and the size slider choose everything; only the Text font survives a relaunch, and it is restored. Edit colors is off-limits** |
| **E17** | Place a small paste | Where a paste smaller than the canvas lands, whether it can be moved before `S` (arrow keys, a drag), and what it costs against a full-size paste | Pieces stamped at a chosen spot: the loading painting's fallback if E15 fails |
| **E18** | Display changes mid-show | Unplug and replug the projector, change resolution and scaling, switch between duplicate and extend (`DisplaySwitch.exe`), with FancyZones on. Record what Paint does and which events arrive. Includes the projector run E11 has not had yet | The show survives, or recovers by measuring and rendering again behind a fresh loading painting |
| **E19** | Interruptions | Do notifications show over maximised Paint? Can Do not disturb be detected or turned on? Does Paint show tips or a what's-new panel at launch? | A pre-flight check fixes each one or warns the presenter |
| **E13** | End-to-end mini presenter | Screen picker → loading painting while the slides render → slides by key (paste and `S`) → presenter view on the other screen. Measure the feel | "Snappy" by your own judgement, and the loading painting looks hand-made |

## 3. Suggested POC tooling

- Installs are allowed on the laptop, so use **Python 3.11+ managed by `uv`**.
  Reuse DoomPaint modules directly (MIT; keep the attribution) plus `paintctl` for
  window management and measurement. Least code, fastest answers. Also install
  Node LTS (Slidev/Marp), and optionally the .NET SDK for a later C# host.
- Put POC code under `poc/` in this repo, one script per experiment. Make results
  reproducible with a `poc/README.md`.
- Let Claude Code run on the laptop and drive the experiments through `paintctl`
  (CLI or MCP). It can read the JSON geometry and iterate without you clicking.

## 4. Decision gates after the POC

1. **Slide resolution:** the display area (E11) of the presenting display, with
   E6 confirming that pasting at that size is fast enough. E11's rule: slide ≤
   (work-area width, work-area height − 210.5 × scale) − 66 px on each axis,
   for example 1854×875 on a 1920×1200 screen at 100%, then calibrated at start
   by a test paste so no edge row is overdrawn (E14). Decided: render at start
   for the measured display area of the chosen screen, keeping the reference
   shape, centred, while the loading painting is drawn. E6:
   fast enough (p95 133 ms at 1854×875; 200–270 ms on 1440p and the 200%
   laptop panel), **with the live OLE clipboard only**: a clipboard rewrite per
   slide drops pastes and pops a modal error dialog from about 2.8 MP.
2. **Focus model:** E2 decides between a background paste (free presenter view) and
   Paint in the foreground (no-activate presenter view or phone view). E2 result:
   both work. Background (posted paste command, posted `S` to the input site)
   keeps the presenter view free but relies on Paint internals and is about
   60–120 ms slower; foreground uses Paint's public shortcuts. Either way,
   self-test the route at start and fall back to the other. **Decided:** Paint
   in front, keys through a low-level hook (E10); the foreground route by
   default, the background route when the presenter view is in front.
3. **Back navigation:** undo stack (E7) or re-paste. E7 result: undo/redo is about 7×
   faster than a paste (33–41 ms vs about 250 ms) and pixel-clean, but holds
   only about 100 slides or about 1 GiB of frames, whichever is less (68 at
   2716×1434). **Decided: re-paste only.** The undo stack's edges are too
   sharp: limited depth, redo cut off by any paste or stray click, and state
   that can drift from the deck's.
4. **Host language:** Python vs C#. C# is the likely pick if E2 needs message
   hooks and we want a single exe.
5. **Authoring:** Slidev primary + Marp + PPTX adapters (per doc 03), unless E12
   surprises us.
