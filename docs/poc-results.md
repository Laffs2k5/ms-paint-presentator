# POC results

Results of the [POC plan](poc-plan.md), one section per step. Newest findings at
the bottom of each section.

## 0. Environment (2026-11-18)

| | |
|---|---|
| OS | Windows 11 Pro 26H2, build 26300.9974, **ARM64** (Snapdragon X Elite X1E80100, Adreno X1-85 GPU) |
| Paint | `Microsoft.Paint` **11.2609.401.0**, arm64 package, English UI, dark theme |
| PowerShell | 7.7.0 |
| Python | System Python 3.13 is **x64** (runs emulated). Native ARM64 3.13.12 installed through uv |
| uv / Node / .NET | uv 0.10.2, Node 22.23.5, .NET SDK 9.0.321 |
| PowerPoint | Installed (16.0.20527), `PowerPoint.Application` COM ProgID registered, so E12's `Slide.Export` path is available |
| Security | Windows Defender only. No third-party EDR or DLP agent found |
| Displays | Laptop panel 2880×1920 at 200%, plus three 2560×1440 at 100% |

**Things that may interfere with later experiments:**

- **Windows clipboard history is on.** E8 tests keeping slides out of it.
- **PowerToys Mouse Without Borders** shares the clipboard between machines, so it
  may read every frame put on the clipboard. That adds a reader to our
  `IDataObject`, which can skew paste timing (E6) and DoomPaint's "consumed"
  signal. Turn it off for timing runs, or measure with it on and off.
- **PowerToys Advanced Paste** is enabled (reads the clipboard only on its hotkey).
- **PowerToys FancyZones** moves windows into zones when displays change and on
  launch (for apps snapped before; Paint has no zone history yet). Matters for
  E11 with a projector. Exclude `mspaint.exe` to rule it out.

**Paint's manifest** (confirms [research 02 §3.6](research/02-ole-and-paint-integration-surface.md)):

- Classic OLE server `Paint.Picture`, CLSID `{D3E34B21-9D75-101A-8C3D-00AA001A1652}`,
  an `ExeServer` (`PaintApp\mspaint.exe`) marked `InsertableObject`, verbs Edit
  and Open, readable format 8 (`CF_DIB`).
- `ms-paint:` protocol, an App Execution Alias, file type associations
  (`paintpicture`, `paintprojectfile`), a share target, and an Explorer shell
  extension (`ExplorerCommand.dll`, surrogate COM server).

## 0b. Instrumentation: `paintctl` (2026-11-18): **pass**

`info` returns no errors, and `zoom_check` matches at 100% (1280×720 expected,
1280×720 measured). Every CLI command works: `launch`, `list`, `info`, `focus`,
`maximize`, `restore`, `resize`, `fit-viewport`, `zoom`, `screenshot` (all four
regions) and `uia-dump`. A `canvas` screenshot of a blank canvas is 100% white,
which confirms the crop lands on the canvas exactly. The MCP server was not run.

Fixes needed to get there are in the paintctl
[README](../poc/paintctl/README.md) and git history. In short: `mcp` became an
optional extra (no ARM64 wheel for `cryptography`), `info` now detects a clipped
canvas, and `uia-dump` redacts the signed-in account name.

**Geometry measured** (physical pixels, visible frame without invisible borders):

| | 100% scaling | 200% scaling |
|---|---|---|
| Maximised on 2560×1440 (taskbar 48 px) | viewport 2512×1229 | — |
| Window for an exact 1920×1080 viewport | 1922×1300 | 1924×1516 |
| Chrome around the viewport | 2 × 220 px | 4 × 436 px |

- **100% zoom maps one document pixel to one physical pixel**, also at 200%
  scaling. Paint's chrome takes about 211–220 px of height at 100%, so the
  display area is never the full screen: on a 1080p projector a 16:9 1920×1080
  slide will not fit. The chrome stays (see the plan's constraints), so E11
  measures the display area that remains.
- **Fit to window gives a fractional zoom** (121.6% shown as "121%"), so slides
  would be resampled. In the same maximised window it chose 121% once and 157%
  later, so it is not a reliable way to size slides.
- Moving Paint onto a monitor with different scaling makes Paint resize its own
  window for the new DPI. `fit-viewport` copes.
- After a monitor move the canvas can end up scrolled. `zoom fit` then `zoom 100`
  re-centres it.
- Paint's status bar hides the canvas size in a narrow window (seen at 960
  logical px wide).

**UIA dump:** [uia-paint-11.2609.401.0.json](uia-paint-11.2609.401.0.json), 183
elements to depth 10, maximised at 100%. Input for E4.

- AutomationIds present: `scrollViewer`, `image`, `CanvasSizeTextBlock`,
  `FileSizeTextBlock`, `ZoomValuesComboBox`, `ZoomSliderControl`,
  `SettingsButton`, `OptionsButton`, `CropButton`, `RotateDropdown`, `Flip`,
  `PencilTool`, `EraserTool`, `BrushesSplitButton`, `CopilotDropDownButton`,
  `MenuBar`.
- **No AutomationId:** Fit to window, Layers, Undo/Redo, Select, and most tools.
  Those can only be found by (localised) name or position.
- **There is no Paste button in the toolbar.** Paste is only in the Edit menu
  (closed in this dump) and on Ctrl+V. That matters for E2/E4.

## 1. Baseline: DoomPaint (2026-11-18): **pass**

[DoomPaint](https://github.com/markrussinovich/DoomPaint) at commit `ba95982`,
cloned outside this repo, run with `--no-sound` and default `--res 640x400`.
PowerToys Mouse Without Borders off. Windows clipboard history and FancyZones on.

- **The live `IDataObject` + synthetic Ctrl+V path works on this Paint build.**
  The start-up self-test chose **Ctrl+V** on both runs. The UIA menu fallback was
  not needed.
- **About 35 pastes/s at 640×400** (34–36 in steady state, single dips to 24 and
  32). That is Doom's engine rate of 35 tics/s, so Paint keeps up and the real
  ceiling at this size is higher. Slide-sized pastes (about 2.4 MP for 1960×1225
  versus 0.26 MP here) are E6's job.
- **ViZDoom is x64-only on Windows** (`win_amd64` wheels, no ARM64), so DoomPaint
  ran on the x64 system Python under emulation while Paint ran natively. The
  presenter has no ViZDoom, so it can be native ARM64.
- **`test_quiet.py` does not run as shipped.** It reads and writes the author's
  own folder (`C:\Source\MsPaintDoom\…`), and `smoke_test.py` writes the frame it
  needs there. With the paths changed, the engine smoke test passes, but the
  quiet test reports "canvas changed: False".
- **Cause: a race in the menu fallback.** `MenuPaster` invokes Edit > Paste right
  after expanding the menu. On this build the item isn't there yet, so all three
  retries fail silently. With a 300 ms wait after expanding, the same path works:
  Paste is enabled, `invoke` takes 80 ms, Paint calls `GetData`, and the canvas
  changes. The menu path still opens the Edit menu on screen for every paste,
  which a presenter would show the audience; E2/E3 look for a quieter way.

## E11. Display area and slide size (2026-11-18): **pass**

Script: [`poc/experiments/e11_display_area.py`](../poc/experiments/e11_display_area.py).
Paint maximised at a measured 100% zoom. "Fits" means no scrollbars (UIA
ScrollPattern), the whole canvas visible, and the canvas exactly 1:1 on screen.
This machine's taskbar is on the left, so it takes width, not height.

| Monitor | Scaling | Work area | Viewport (maximised) | Largest canvas that fits | Pixel-identical slide |
|---|---|---|---|---|---|
| 2560×1440 | 100% | 2512×1440 | 2512×1229 | 2448×1165 (3 of 4 runs); 2446×1163 (4 of 4) | **2446×1163** (E14), 2424×1165 |
| 2880×1920 (laptop) | 200% | 2784×1920 | 2784×1499 | 2720×1435 (4 of 4); 2718×1433 (4 of 4) | **2716×1434** (E14), 2720×1435 |

"Pixel-identical" means the screen grab of the pasted test pattern matches the
source exactly: 0 of 2.8–3.9 million pixels differ. Each pattern pixel encodes
its own coordinates, so any shift or scaling would show.

**Size rule (fit):**

> viewport = (work-area width, work-area height − 210.5 × scale), rounded half up
> (211 px at 100%, 421 px at 200%; a maximised window has no side borders)
>
> largest slide that reliably fits = viewport − 66 px on each axis

- **Paint keeps 64 px around the canvas** (32 per side), in *physical* pixels at
  both 100% and 200%. At exactly 64 px the scrollbar decision varies between
  runs, so the rule keeps 1 px of slack per side.

**Edge rows (pixel-identical):** at some sizes Paint draws the canvas border
line across the slide's last row or column (or first). It depends on the
size, the viewport and the scaling, and no single formula fitted both screens:

| Screen | Seen clean | Seen with one edge row or column overdrawn |
|---|---|---|
| 100%, viewport 2512×1229 | viewport − slide even (66, 64) | odd: 69 rows, 87 columns |
| 200%, viewport 2784×1499 | slide height even (margins 65, 67, 69, 71), and 64 | slide height odd (margins 66, 68, 70, 72, 74, 76) |

So the size from the fit rule can still lose an edge row (2718×1433 does).
The presenter should **calibrate at start**: paste a test pattern at the
candidate size, check it is pixel-identical, and step down a pixel per axis
until it is. That fits the adaptive design and covers scalings not tested here.

**Predicted for 16:10 reference screens** (taskbar at the bottom, 48 px × scale;
100% and 200% measured, other scalings extrapolated, so verify on the screen):

| Screen | Scaling | Viewport | Slide (fit rule, before calibration) | Slide aspect |
|---|---|---|---|---|
| 1280×800 | 100% | 1280×541 | **1214×475** | 2.56:1 |
| 1920×1200 | 100% | 1920×941 | **1854×875** | 2.12:1 |
| 1920×1200 | 125% | 1920×877 | **1854×811** | 2.29:1 |
| 1920×1200 | 150% | 1920×812 | **1854×746** | 2.49:1 |
| 2560×1600 | 100% | 2560×1341 | **2494×1275** | 1.96:1 |
| 2560×1600 | 150% | 2560×1212 | **2494×1146** | 2.18:1 |
| 2880×1800 | 200% | 2880×1283 | **2814×1217** | 2.31:1 |

Scaling changes the slide shape a lot, because the chrome grows with scaling
while the screen does not. An adaptive presenter has to measure at start.

**Found along the way:**

- **A pasted slide is a floating selection** with a dashed border and a floating
  selection toolbar over it. **Esc discards the paste; Enter does nothing.** A
  click outside the canvas commits it, but moves the mouse. Near a corner at 200%
  scaling the click lands on a selection resize handle and Paint silently
  stretches the slide by the distance to the corner. The presenter needs a clean,
  mouse-free way to commit (E14).
- **Paint's zoom box is not exact.** After Fit to window it read "100" (and the
  slider 100.0) at a measured 99.8%, and setting 100 was then a no-op. Fixed in
  `paintctl`; `info` now reports `zoom_measured`.
- **UIA's canvas rectangle can be a pixel off** the real canvas. Locate the
  canvas by image instead when it matters.
- **A slide-sized paste seemed to take 0.6–0.8 s to appear.** That was the
  measurement: each poll grabbed the whole virtual screen. E6 measured it
  properly: about 200 ms at 2.8 MP.
- **Notifications cover the slide.** A Teams toast appeared over the canvas in
  one run. Turn on Do Not Disturb before presenting.

## E14. Commit a pasted slide cleanly (2026-11-18): **pass**

Script: [`poc/experiments/e14_commit_paste.py`](../poc/experiments/e14_commit_paste.py).
A paste lands as a floating selection: a dashed border and a floating toolbar
over the slide (about 4,500–30,000 differing pixels). Each method was applied
once the paste showed, then the screen was polled (about every 0.2 s) until it
matched the source exactly. 2560×1440 at 100%, slide 2446×1163:

| Method | Clean within | Input | Verdict |
|---|---|---|---|
| Nothing | never | – | decorations stay; the outline also keeps animating |
| **Key `S`** (Select tool) | **0.06–0.32 s** | one keypress | **use this** |
| Click in the margin | 0.12–0.34 s | mouse click (cursor put back) | works; moves the mouse and can hit a resize handle (E11) |
| UIA invoke of Crop (`CropButton`) | 0.18–0.40 s | none | works without keys or mouse; candidate if E2 needs a background commit |
| Ctrl+A, then Esc | 0.49–0.70 s (incl. a 0.3 s pause) | three keys | works, slower |
| Posted `S` to `InputSiteWindowClass` | 0.3–0.4 s | none, works with Paint in the background | see E2/E3 |
| Key `P` (Pencil) | never | one keypress | commits, but see below |
| Esc / Enter | – | – | Esc discards the paste, Enter does nothing (E11) |

- **Keep the Select tool active.** Drawing tools (Pencil, Brush, Eraser) show a
  floating brush size/opacity panel at the left of the viewport, which covers
  the left edge of a full-width slide (the 19 px strip key `P` left behind).
  Key `S` commits the paste and keeps Select, so the panel never appears. It
  also toggles between rectangle and free-form select, which is harmless.
- **Single-letter tool shortcuts exist** in Windows 11 Paint: `P` Pencil, `E`
  Eraser, `B` Fill, `I` Colour picker, `T` Text, `Z` Zoom, `S` Select
  ([community list](https://magpie.bearblog.dev/keyboard-shortcuts-for-windows-11-ms-paint/)).
  Microsoft's support pages did not list them.
- **The decorations still show briefly**: from when the paste appears until the
  commit lands, roughly 0.2–0.3 s. E13 judges whether that is acceptable.
  Sending `S` as soon as a selection exists (DoomPaint watches the Crop button
  become enabled) keeps it short.
- **The size decides the edge rows**, not the method. The 200% sweep in E11's
  table was done with key `S`.

## E6. Paste speed at slide size (2026-11-18): **pass**

Script: [`poc/experiments/e6_paste_speed.py`](../poc/experiments/e6_paste_speed.py).
Per slide, as a presenter would: offer the slide on the clipboard, send Ctrl+V
(one atomic `SendInput` batch), wait until it is on screen, commit with `S`
(E14), pause 0.3 s. 20 timed pastes per size after 2 warm-ups, Paint maximised
at 100% zoom, native ARM64 Python, Mouse Without Borders off, Windows clipboard
history on. "Visible" is read off the screen from probe squares in each slide.
A screen read waits for the display refresh, so times are resolved to one frame
(16.7 ms at 60 Hz), as the audience sees them.

**Live OLE `IDataObject`** (DoomPaint's, vendored as
[`doompaint_clipserve.py`](../poc/experiments/doompaint_clipserve.py)):

| Slide | For screen | MP | CF_DIB | Paint reads it (p50 / p95) | **On screen (p50 / p95 / max)** | Missed |
|---|---|---|---|---|---|---|
| 1214×475 | 1280×800 @100% | 0.58 | 1.7 MB | 7 / 9 ms | 66 / 67 / 67 ms | 0 of 20 |
| **1854×875** | **1920×1200 @100%** | 1.62 | 4.9 MB | 7 / 9 ms | **116 / 133 / 133 ms** | 0 of 20 |
| 2446×1163 | 2560×1440 @100% (this monitor) | 2.84 | 8.5 MB | 7 / 9 ms | 200 / 216 / 216 ms | 0 of 20 |
| 2716×1434 | 2880×1920 @200% (laptop) | 3.89 | 11.7 MB | 7 / 8 ms | 252 / 269 / 269 ms | 0 of 20 |

- **Pass:** p95 is 133 ms at the slide size of a 1920×1200 projector, under the
  150 ms target.
- **Paint asks for the image within about 7 ms** of Ctrl+V at every size. The
  rest is Paint processing and drawing it, roughly 50–70 ms per megapixel. Large
  displays take 200–270 ms per slide; E13 judges whether that feels snappy.
- **The timing is steady:** p95 is within one or two frames of p50 everywhere.

**Plain clipboard** (`EmptyClipboard` + `SetClipboardData(CF_DIB)` per slide),
for comparison:

| Slide | On screen (p50 / p95) | Missed |
|---|---|---|
| 1214×475 | 66 / 83 ms | 0 of 20 |
| 1854×875 | 132 / 164 ms | 0 of 20 |
| 2446×1163 | 209 / 210 ms | **8 of 20** |
| 2716×1434 (stopped) | 258–395 ms | paste 8 failed with Paint's modal **"Can't complete operation"** dialog |

- **The presenter must use the live OLE object**, set once and updated in place,
  never a clipboard rewrite per slide. From about 2.8 MP the plain clipboard
  drops pastes and can stop the show with a modal dialog that needs a click.
  That is the race DoomPaint documents (rewriting the clipboard while Paint is
  still reading it). It happened here even with half a second between slides,
  so something keeps reading after the paste shows: Paint itself or Windows
  clipboard history. Not verified which.
- Single pastes into a fresh window (E11, E14) are not affected.

**E6 addendum: battery, and the paste trigger** (laptop undocked, on battery,
Balanced power plan; 2716×1434, 15 pastes each,
[`e6_paste_speed.py`](../poc/experiments/e6_paste_speed.py) `--trigger`/`--background`):

| Trigger | Paint in front | Commit | Paint reads it (p50 / p95) | On screen (p50 / p95 / max) |
|---|---|---|---|---|
| Ctrl+V (`SendInput`) | yes | `S` (`SendInput`) | 11 / 97 ms | 297 / 444 / 516 ms |
| posted `WM_COMMAND` | yes | `S` (`SendInput`) | 7 / 81 ms | 353 / 514 / 516 ms |
| posted `WM_COMMAND` | no | posted `S` (E2) | 19 / 84 ms | 416 / 467 / 534 ms |
| posted `WM_COMMAND` | no | none (each paste lands on the last, still floating) | 292 / 382 ms | 633 / 837 / 900 ms |

- **On battery Paint is slower and less steady:** Ctrl+V p50 297 ms against
  252 ms docked on mains power (E6 above), and p95 444 against 269 ms.
  **Present with the charger connected.**
- **Commit every slide.** Pasting onto a still-floating selection is much
  slower: Paint takes about 300 ms just to read the clipboard.

## E2/E3. Paste without focus or keystrokes (2026-11-18): **pass**

Script: [`poc/experiments/e2_background_paste.py`](../poc/experiments/e2_background_paste.py).
A stand-in "presenter view" window of our own stays in front, over Paint's
toolbar. Each candidate must make Paint read the live OLE clipboard and show
the slide, with the stand-in still in front. Laptop panel, 200%, 2716×1434.

| Candidate | Paint reads it | Slide shows | Stand-in keeps focus |
|---|---|---|---|
| **post `WM_COMMAND` 0xE125 to the main window** (`MSPaintApp`) | **8 ms** | **yes** | **yes** |
| post `WM_COMMAND` 0xE125 to `MSPaintView` | 11 ms | yes | yes |
| *send* `WM_COMMAND` 0xE125 to the main window | no | no | yes |
| post `WM_PASTE` to the main window or `MSPaintView` | no | no | yes |
| post `WM_CHAR` 0x16 (Ctrl+V) to `MSPaintView` | no | no | yes |
| UIA Edit > Paste menu | 733 ms | yes | **no**: Paint activates and the menu shows |

0xE125 is `ID_EDIT_PASTE`, MFC's standard Edit > Paste command. Paint's main
window is still an MFC frame (`MSPaintApp`, with an `AfxWnd140u` child); its
canvas and toolbar are a WinUI 3 island (`Microsoft.UI.Content.DesktopChildSiteBridge`
with an `InputSiteWindowClass` child). A *sent* command probably fails because
COM forbids the clipboard call inside a synchronous cross-thread message; not
verified.

**Committing in the background** (the paste is a floating selection, E14):

| Candidate | Clean (pixel-identical) | Stand-in keeps focus |
|---|---|---|
| **post `S` (WM_KEYDOWN/WM_KEYUP) to `InputSiteWindowClass`** | **yes, 0.3–0.4 s** (2 of 2, plus the final run) | **yes** |
| post `S` to the main window, `MSPaintView` or `DesktopChildSiteBridge` | no | yes |
| UIA invoke of Crop | yes | **no**: Paint activates |
| UIA SelectionItem on the Select or Brushes tool | no | **no**: Paint activates |
| a second posted paste | no (commits the previous one, the new one floats) | yes |

Found by the user watching the screen: an early run looked "not clean" only
because the stand-in window's drop shadow fell on the canvas. Keep anything
that must not show away from the canvas, shadows included.

- **A slide change can run entirely in the background:** post the paste
  command to the main window, then post `S` to the input-site window. No
  focus change, no `SendInput`, no mouse. On battery it shows in 416 ms p50
  against 297 ms for foreground Ctrl+V (E6 addendum).
- **It relies on internals a Paint update could change:** an MFC command ID
  and a WinUI window class. The foreground route uses only Paint's public
  keyboard shortcuts. The presenter should self-test at start (paste a test
  slide by each route and check it, as DoomPaint does for its paste path) and
  fall back to the foreground route.
- UIA on Paint's WinUI controls (invoke, select) activates Paint every time.

## E1. Clipboard formats and alpha (2026-11-18): **pass**

Script: [`poc/experiments/e1_formats.py`](../poc/experiments/e1_formats.py).
An OLE `IDataObject` of its own offers `PNG` (registered format), `CF_DIBV5`
(32-bit BGRA with an alpha mask, sRGB) and `CF_DIB` in any mix, and logs
Paint's calls. Laptop panel at 200%, 2716×1434, on battery, Paint in front.

**Preference: Paint takes PNG.** Offered all three, Paint pasted the PNG in
both offering orders, so the order does not matter. Its calls, in ms after
Ctrl+V (`PNG > CF_DIBV5 > CF_DIB` offered):

| ms | Call |
|---|---|
| −0.6 | `EnumFormatEtc` |
| 6 | `GetData CF_DIB` |
| 32 | `GetData PNG`, then `GetDataHere`, then `GetData PNG` again |
| 235 | `GetData CF_DIBV5` |

- Paint asks for formats directly whatever is advertised: with only `CF_DIBV5`
  offered it asked for `CF_DIB`, `CF_DIBV5` and `PNG`; with only `CF_DIB` it
  also tried `image/png`.
- It reads the format it uses more than once (PNG twice, `CF_DIB` up to three
  times), and something reads again about 230 ms later (Paint or Windows
  clipboard history; not verified).
- **No call to `DAdvise`** in any log (first answer for E5).

**Alpha** (overlay pasted over a committed opaque slide: an opaque red block,
a 50% blue block, the rest fully transparent):

| Format | Pastes | Opaque red | 50% blue | Transparent area |
|---|---|---|---|---|
| **PNG** | yes | exact red | **blended exactly** (61.4, 60.3, 171.3 vs 61.7, 60.5, 171.1 expected) | **slide shows through, exactly** |
| `CF_DIB` 32-bit | yes | exact red | solid blue: alpha ignored | black: alpha ignored |
| `CF_DIBV5` (as encoded here) | **no**: modal error dialog, opaque or not | – | – | – |

**Speed** (opaque slide, 5 pastes each, Ctrl+V → on screen):

| Format | Clipboard size | On screen |
|---|---|---|
| `CF_DIB` 24-bit | 11.7 MB | 274–292 ms (median 277) |
| PNG | 0.2 MB (this test pattern; real slides compress less) | 273–295 ms (median 276) |

- **Use PNG, offered alone:** Paint prefers it, it is the only format with
  working alpha, it keeps the clipboard small, and it is as fast as `CF_DIB`.
  Paint's own processing is the bottleneck, not the transfer. Offering several
  formats only makes Paint read more of them.
- **PNG alpha opens up overlays:** a build step (a bullet appearing, a
  highlight) can be pasted as a transparent PNG over the committed slide.
- **Never offer a format that has not been proven.** An unreadable format
  stops the show with a modal "Can't complete operation" dialog, and every
  paste fails until it is closed. The presenter should be able to close it
  itself, as `paintctl dismiss` now does. Why this `CF_DIBV5` fails was not
  investigated, since PNG covers alpha.
- One `CF_DIBV5` speed reading (25 ms) was a false positive: its probe colour
  was already on screen from the PNG run. The script now uses probe colours
  unique per format.

**E1 addendum: PNG must be a new clipboard object per slide** (found in E7).
Swapping a PNG inside the live data object (DoomPaint's in-place update) does
not work: Paint asks for it once (`GetData PNG`) but then pastes its cached
copy of the previous image, with no error. `CF_DIB` swapped in place is read
fresh every time (as in E6). So for PNG, put a new OLE data object on the
clipboard for each slide. That is safe from E6's race: the OLE object is
reference-counted, so a read in flight keeps the old object alive (E7 pasted
220 PNG slides that way with no miss and no error dialog).

## E7. Undo stack as slide history (2026-11-18): **pass**

Script: [`poc/experiments/e7_undo_history.py`](../poc/experiments/e7_undo_history.py).
Paste N distinct PNG slides (each committed with `S`), then Ctrl+Z until the
history runs out, then Ctrl+Y until it runs out. Laptop panel at 200%, on
battery, Paint in front.

| Run | Pasted | Undo went back | Redo went forward | Paint memory (private) |
|---|---|---|---|---|
| 50 × 2716×1434 | 50 of 50 | **all 50**, to the blank canvas | all 50 | 236 → 1,144 MB |
| 120 × 2716×1434 | 120 of 120 | **68** (oldest 52 dropped) | 68 | levels off at 1,414 MB from about slide 68 |
| 150 × 2048×1024 | 150 of 150 | **101** (oldest 49 dropped) | 101 | levels off at about 1,190 MB from about slide 100 |

- **One slide is one undo step** (paste and `S` commit together).
- **Undo and redo are about 7× faster than pasting:** Ctrl+Z 33 ms p50 /
  51–59 ms p95, Ctrl+Y 41 ms p50 / 67 ms p95, against about 250 ms per paste.
  Every undone and redone slide checked was **pixel-identical**, with no
  selection marks.
- **Each step costs a full uncompressed frame:** about 15.7 MB per slide at
  2716×1434 (one BGRA frame is 14.9 MB), and Paint gives little back while
  walking the history.
- **The depth is limited.** Both capped runs fit "about 100 steps or about
  1 GiB of frames, whichever comes first": 68 × 14.9 MiB ≈ 1,010 MiB, while
  101 × 8 MiB ≈ 808 MiB stopped at the step count. Two data points, so treat
  it as observed, not proven. Predicted depth for the 1920×1200 projector's
  1854×875 slides (6.2 MiB): about 100.
- A new paste after some undos cuts off the redo chain, as in any editor.

**What this means for navigation (gate 3):** stepping back and forth through
recent slides with Ctrl+Z / Ctrl+Y is fast and clean; jumping further back
than the history holds needs a re-paste. A deck that fits the depth can even
be preloaded: paste every slide at start, undo back to the first, and then
every "next" is a 40 ms Ctrl+Y. That costs up to about 1 GiB in Paint and the
preload is visible, so it would have to happen before the audience looks (or
be part of the show).

## E4. What UI Automation can drive (2026-11-18): **pass**

Script: [`poc/experiments/e4_uia_survey.py`](../poc/experiments/e4_uia_survey.py).
Menus and drop-downs opened through UIA ExpandCollapse; new controls found by
diffing the UIA tree; items read, never clicked. Paint 11.2609.401.0, English
UI. The full toolbar dump from 0b is [uia-paint-11.2609.401.0.json](uia-paint-11.2609.401.0.json).

**Controls the presenter may need** (an AutomationId does not change with the
UI language; a name does):

| Where | Control | AutomationId | Name (English) | Patterns |
|---|---|---|---|---|
| Canvas | viewport | `scrollViewer` | – | Scroll |
| Canvas | canvas | `image` | "Using … tool on Canvas" | – |
| Status bar | canvas size | `CanvasSizeTextBlock` | "W × Hpx" | – |
| Status bar | zoom slider | `ZoomSliderControl` | Zoom | RangeValue 20–1000, Value |
| Status bar | zoom box | `ZoomValuesComboBox` | Zoom | ExpandCollapse, Selection, Value (25%–1000%) |
| Status bar | Fit to window / Zoom in / Zoom out | – | (those names) | Invoke |
| Menu bar | File / Edit / View | – | (those names) | ExpandCollapse, Invoke |
| File | New, Open, Save, Save as, Recent | `FileNew`, `FileOpen`, `FileSave`, `FileSaveAs`, `RecentFiles` | – | Invoke / ExpandCollapse |
| Edit | Cut, Copy, **Paste**, Copy visible layers | – | (those names) | Invoke |
| View | Zoom ▸ **100%**, Zoom in, Zoom out, Fit to window | – | (those names) | Invoke |
| View | Rulers, Gridlines, Status bar, Always show toolbar, Automatically hide toolbar | – | (those names) | Toggle |
| View | Full screen, Thumbnail | – | (those names) | Invoke |
| Selection | Select (split button) | – | Select | ExpandCollapse, Invoke, SelectionItem |
| Selection ▸ | Rectangle, Free-form | `RectangularSelectionMenuFlyoutItem`, `FreeFormSelectionMenuFlyoutItem` | – | Toggle, Invoke |
| Selection ▸ | Select all, Invert selection, Delete | – | (those names) | Invoke |
| Selection ▸ | **Transparent selection** | – | Transparent selection | Toggle |
| Tools | Pencil, Eraser | `PencilTool`, `EraserTool` | – | **Toggle only** (no Invoke) |
| Tools | Brushes | `BrushesSplitButton` | Brushes | ExpandCollapse, SelectionItem |
| Image | Crop, Rotate, Flip | `CropButton`, `RotateDropdown`, `Flip` | – | Invoke (+ ExpandCollapse) |
| Layers | panel toggle | – | – (unnamed) | Toggle |
| Layers ▸ | layer list, hide/show | `layersList`, `HideShowLayerButton`, `HideShowBackgroundButton`, `BackgroundButton` | – | Selection / Toggle / Invoke |
| Toolbar | Toolbar options, Settings | `OptionsButton`, `SettingsButton` | – | Invoke |

- **Many controls have no AutomationId:** Edit > Paste, everything under View,
  Fit to window, Select all, Transparent selection, the Layers toggle. On a
  localised Paint they can only be found by the translated name or by position.
- **Undo and Redo are not in the Edit menu**, only on the title bar (by name).
- Two panes have nearly the same id: the canvas viewport is `scrollViewer`, the
  toolbar's is `ScrollViewer`.
- **Any UIA action activates Paint** (E2). UIA is for set-up and checks, not
  for the slide change itself.

**Start-up checklist for the presenter** (states read here: all already right):

| Setting | Here | Must be | Why |
|---|---|---|---|
| View > Always show toolbar | on | on | the chrome is the point (constraints) |
| View > Automatically hide toolbar | off | off | same |
| View > Status bar | on | on | part of the chrome; also how `canvas_px` is read |
| View > Rulers, Gridlines | off | off | they would draw over the slide |
| Selection > Transparent selection | off | **off** | when on, white in a pasted slide turns see-through |
| Active tool | Brush at launch | **Select** | drawing tools show a brush panel over the slide's left edge (E14); key `S` |
| Zoom | – | exactly 100% | View > Zoom > 100%, or the slider stepped via a neighbour (0b) |
| Layers panel | closed | closed | it narrows the viewport |

## E8. Keep slides out of clipboard history (2026-11-18): **pass**

Script: [`poc/experiments/e8_clipboard_history.py`](../poc/experiments/e8_clipboard_history.py),
history read by [`clipboard_history.ps1`](../poc/experiments/clipboard_history.ps1)
(WinRT `Clipboard.GetHistoryItemsAsync`, ids, times and format names only).
Clipboard history on. Each case leaves a slide on the clipboard for 3 s, reads
the history, then pastes the slide into Paint. The formats are Microsoft's
documented opt-outs ([Clipboard Formats](https://learn.microsoft.com/windows/win32/dataxchg/clipboard-formats#cloud-clipboard-and-clipboard-history-formats)).

| Slide offered as | Opt-out formats added | New Win+V history items | Paint pastes |
|---|---|---|---|
| `CF_DIB` | none (control) | **1** (Bitmap) | yes |
| `CF_DIB` | `ExcludeClipboardContentFromMonitorProcessing` | **0** | yes |
| `CF_DIB` | `CanIncludeInClipboardHistory`=0 + `CanUploadToCloudClipboard`=0 | **0** | yes |
| `CF_DIB` | all three | **0** | yes |
| PNG only | none (control) and each opt-out | **0** in every case | yes |

- **The opt-outs work, and Paint pastes normally with them present.** Add
  `ExcludeClipboardContentFromMonitorProcessing` to every slide: it covers both
  history and cloud sync, and it also stops the history service from reading
  the slide at all.
- **That service was E1's unexplained late reader.** Without an opt-out, the
  `CF_DIB` slide was read at about 7 ms and again at about 235 ms after it was
  offered, before any paste. With `CanIncludeInClipboardHistory`=0 the second
  read became a read of that flag (225 ms); with the exclude format it went
  away. One early read (1–7 ms) stays in every case, from a process not
  identified.
- **PNG-only slides never entered the history,** even without an opt-out.
  History seems to record only standard bitmap formats. Still add the opt-out.
- **Experiments filled the history with test slides.** History keeps 25 items;
  by E8 all 25 were bitmaps from the E7 runs onwards, so earlier unpinned items
  had been pushed out. Then the `CF_DIB` run, which called the WinRT
  `DeleteItemFromHistory` on its one control item, ended with the history
  **empty**. Most likely that call cleared everything, but this was not
  re-tested. The script no longer deletes; `poc/README.md` warns about both.

## E5. Does anything call DAdvise? (2026-11-18): **no**

Script: [`poc/experiments/e5_dadvise.py`](../poc/experiments/e5_dadvise.py).
E1's data object logs every IDataObject call. Here it ran through the phases
of a presentation, with `DAdvise` refused (`OLE_E_ADVISENOTSUPPORTED`, as in
E1–E8) and accepted (subscriber kept, connection id returned). `CF_DIB` slide,
1600×1000, Paint in front.

| Phase | Calls (the same in every variant) |
|---|---|
| Slide offered (3 s) | `GetData CF_DIB` ×1; ×2 without the history opt-out (the second is the history service, E8) |
| Pasted (Ctrl+V, `S`) | `GetData PNG`, `GetData image/png` (both refused: not offered), `GetDataHere` ×2, `GetData CF_DIB` ×1 |
| Paint loses focus and gets it back | **none** |
| Pasted again | as the first paste |

- **No `DAdvise`, `DUnadvise` or `EnumDAdvise` call in any variant or phase**,
  and none in the hundreds of pastes logged in E1, E7 and E8 either.
- Paint does not look at the clipboard when it is activated, only on paste.
- So there is no notification route into Paint: a slide change has to be a
  paste command (Ctrl+V, or E2's posted `WM_COMMAND`). DoomPaint's design,
  which re-arms its data object per frame and triggers each paste, is the right
  model.

## E15. Draw in Paint with synthetic input (2026-11-18): **pass**

Script: [`poc/experiments/e15_draw.py`](../poc/experiments/e15_draw.py).
For the loading painting. Laptop panel (2880×1920 at 200%), canvas 2000×1000,
Paint maximised at 100%, the Pencil (1 px) unless noted. Each test draws in its
own cell, then a screen grab is compared with the ideal path. Two full runs
gave the same numbers; extra probes chased the pen failure and pressure.

**Circles** (350° of r = 95 px, 146 points 4 px apart):

| Route | Time between points | Speed | Path covered | Worst gap / stray ink |
|---|---|---|---|---|
| Mouse (`SendInput`, absolute) | none: the whole stroke in one call | 10,000–12,000 px/s | 100% | 1 px / 1 px (drawn 1 px up-left) |
| Mouse | 1 / 4 / 8 / 16 ms | 3,600 / 900 / 450 / 225 px/s | 100% | 1 px / 1–2 px |
| Synthetic pen (`InjectSyntheticPointerInput`) | none | – | **fails part-way**, see below | – |
| Synthetic pen | 1 / 4 / 8 / 16 ms | 3,600 / 900 / 450 / 225 px/s | 100% | 1 px / 1–2 px |
| Mouse messages posted to `InputSiteWindowClass` | 4–8 ms | – | **nothing drawn** | – |

- **Strokes land where intended,** within a pixel, at 200% scaling, with no
  point dropped and no arcs cut into chords, even when the whole stroke goes in
  one `SendInput` call.
- **They show live.** Paused half way with the button still down, the first
  half was already fully on screen, on both routes.
- **Rapid strokes all land:** 12 short strokes back to back took 0.11 s by
  mouse and 0.01 s by pen, and all 12 showed every time.
- **The pen leaves the mouse cursor where it is.** The mouse route moves the
  real cursor (the script puts it back).
- **Pen frames must be spaced.** Injected back to back with no wait, the
  stroke failed part-way (after 55–95 frames) with `ERROR_INVALID_PARAMETER`
  in 6 of 8 tries, whatever came before. 1 ms apart, it never failed.
- **No drawing in the background.** Posted mouse messages draw nothing, with
  Paint in front or not, unlike the posted `S` in E2. With another window in
  front, the pen still draws, but Paint comes to the front. So Paint is in
  front while the loading painting is drawn.

**Pressure** (pen at 50 and at 1000 out of 1024; stroke width in px):

| Brush | Light | Hard |
|---|---|---|
| Brush (the mouse also gives 3) | 3 | 3 |
| Calligraphy pen | 3 | 3 |
| Natural pencil | 4 | 4 |
| **Watercolor brush** | **16** | **31** |
| **Oil brush** | **20** | **39** |

**Choosing a tool by script** (more in E16): the Brushes button's
SelectionItem.Select does not switch tools; invoking an item of its menu does
(Brush, Calligraphy brush, Calligraphy pen, Airbrush, Oil brush, Crayon,
Marker, Natural pencil, Watercolor brush). The canvas's UIA name says which
tool is active ("Using Pencil tool on Canvas"); the Calligraphy pen reports
itself as "Calligraphy brush 2".

**For the loading painting:** draw with the synthetic pen, frames at least
1 ms apart. It is exact and live, it leaves the mouse alone, and with the
Watercolor or Oil brush pressure gives strokes a hand-drawn swell. The mouse
route is the fallback. Speed is no limit: a hand-like pace of a few hundred
px/s is far below what either route takes. Not tested: whether the speaker
moving or clicking the mouse during a pen stroke disturbs it.
