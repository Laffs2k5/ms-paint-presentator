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

## E16. Choose Paint's tools, colours and sizes by script (2026-11-18): **pass**

Script: [`poc/experiments/e16_tools.py`](../poc/experiments/e16_tools.py).
For the loading painting. Laptop panel at 200%, Paint maximised and in front,
the synthetic pen (E15) for strokes and taps. Every choice is checked by what
Paint reports through UI Automation (the canvas is named "Using Pencil tool on
Canvas", the colour slots "Color 1: Red") and by what lands on the canvas. The
numbers are from the final run; earlier runs found the pitfalls below.

**How each thing can be chosen:**

| What | Works | Does not work |
|---|---|---|
| Tools | Keys: `P` Pencil, `B` Fill ("Fill with color"), `T` Text, `E` Eraser, `I` Colour picker ("Pick color"), `Z` Magnifier, `S` Select. Or UIA Toggle on the tool buttons | – |
| Brushes (9) | Invoke an item of the Brushes button's menu: Brush, Calligraphy brush, Calligraphy pen ("Calligraphy brush 2"), Airbrush, Oil brush, Crayon, Marker, Natural pencil, Watercolor brush | SelectionItem.Select on the Brushes button |
| Shapes (23) | A pen tap on the shape (Line, Oval, Rectangle, Five-point star, Heart tried) | UIA select of its list item |
| Shape outline / fill | Invoke an item of the drop-down: No / Solid outline (fill), Crayon, Marker, Oil brush, Natural pencil, Watercolor brush; each item's Toggle state says which is on | – |
| Colour slot | UIA select of "Color 1" / "Color 2" | – |
| Colours (20) | A pen tap on the swatch (30 of 30 picks) | UIA: the swatches have no SelectionItem pattern |
| Size, opacity | The Size and Opacity sliders (UIA RangeValue), there while a drawing tool is active: size 0–90, opacity 0–100 | – |
| Text | `T`, drag a box with the pen, pick the font, type the text as Unicode key events (`KEYEVENTF_UNICODE`), commit with a pen tap outside the box or by choosing another tool through UIA (later the tool change dropped the text in some runs: use the tap, see E13's third round) | Letters sent as virtual-key presses (no text came out); `S` while a box is open types an S |
| Custom colours | – | Edit colors, see below |

A pen tap on a control is the general fallback: it clicks without moving the
mouse.

**The palette**, as drawn with the Pencil (RGB):

| Colour | RGB | Colour | RGB |
|---|---|---|---|
| Black | 0, 0, 0 | White | 255, 255, 255 |
| Gray | 127, 127, 127 | Light gray | 195, 195, 195 |
| Dark red | 136, 0, 21 | Brown | 185, 122, 87 |
| Red | 237, 28, 36 | Rose | 255, 174, 201 |
| Orange | 255, 127, 39 | Gold | 255, 201, 14 |
| Yellow | 255, 242, 0 | Light yellow | 239, 228, 176 |
| Green | 34, 177, 76 | Lime | 181, 230, 29 |
| Turquoise | 0, 162, 232 | Light turquoise | 153, 217, 234 |
| Indigo | 63, 72, 204 | Blue-gray | 112, 146, 190 |
| Purple | 163, 73, 164 | Lavender | 200, 191, 231 |

**Pencil width by size:** 0 → 1 px, 5 → 6, 20 → 21, 45 → 68, 90 → 248. Up to
about 20 the width is size + 1; above that it grows faster.

**Shapes:** dragged with the pen, a shape stays editable, with handles and a
toolbar up to 40 px above it, until `S` commits it. Then the ink matches the
drag within 3 px and stays put. A Rectangle with Crayon outline and Solid fill
was filled with Colour 2 (yellow, 255, 242, 0).

**What Paint keeps after close and relaunch:** almost nothing. The tool (always
the Brush), colours (Black, White), shape outline and fill (Solid outline, No
fill), Pencil size (0) and opacity (100) all reset. **The Text tool's font is
kept.** The script records the font at the start and puts it back, even when a
run fails, then checks it after one more relaunch. (The Text font size is kept
too, found later in E13's third round; this script does not change it.)

**Pitfalls found:**

- **Edit colors is off-limits.** It opens a dialog inside Paint's window.
  Invoked through UIA, the call blocks until the dialog closes; walking Paint's
  UIA tree while it is open hangs (one probe hung for over three minutes). The
  20 palette colours are enough for a crappy painting.
- **The font list is awkward.** It is virtualised (a font far down the list is
  not there to select), holds several items of one name, and typing a name
  plus Enter sometimes autocompletes ("Calibri" became "Calibri Light"). The
  setter selects the exact item when it is there, else types the name, then
  Delete (drops an autocompleted tail), then Enter. The box can report the old
  font for a moment; a new text box or a relaunch shows the real one.
- **An earlier run left the Text font as Comic Sans MS** in the user's Paint.
  The original was identified from a screen grab of default text (Calibri,
  whose size matched; Segoe UI's did not), put back and checked after a
  relaunch. That is why the script now restores the font itself.
- **For a moment after a tool change, two canvas elements** carry the
  AutomationId `image` and the "Using … tool" name; the lookup retries.
- **A tap with the Text tool on empty canvas opens a new box,** and if the font
  list is still open a tap only closes the list. Close a box by choosing
  another tool through UIA. (Later, E13's third round: that can drop the
  box's text. To keep the text, tap outside the box first, then change tool
  to drop the new empty box.)

**For the loading painting:** compose it in the 20 palette colours above,
choose tools by key, brushes and shape styles through their menus, colours by
pen tap, and sizes through the slider. If it uses the Text tool, restore the
font afterwards. Never open Edit colors.

## E17. Place a small paste (2026-11-18): **pass**

Script: [`poc/experiments/e17_place_paste.py`](../poc/experiments/e17_place_paste.py).
For stamping ready-made pieces at a chosen spot, the loading painting's
fallback. Solid-colour PNG stamps, each a new OLE clipboard object, pasted with
Ctrl+V into a 2000×1000 canvas that fits the viewport (laptop panel at 200%,
Paint in front). Positions are in canvas pixels, found by the stamp's colour.

| Question | Result |
|---|---|
| Where does a small paste land? | **Always at the canvas's top-left (0, 0)**, also when a selection rectangle was made first: a selection does not steer the paste |
| Arrow keys on the floating paste | **Move it exactly 1 px per press** (Right, Down; Shift+arrow also 1 px) |
| Ctrl+arrow | **Stretches it** by 1 px per press instead of moving it, so the stamp is resampled: avoid |
| Pen drag from inside the paste | Moves it to within 1 px of the target (exact in one test, 1 px off in the other) |
| Alpha stamp moved, then committed | Transparent corners stay see-through, the opaque part is exact, and nothing is left where it first landed |

**Cost** (Ctrl+V to on screen, 5 pastes each, median):

| Stamp | 100×100 | 400×300 | 1000×600 | 2000×1000 |
|---|---|---|---|---|
| On screen | 76 ms | 60 ms | 71 ms | 95 ms |

- **Small pastes are not much cheaper:** about 60–75 ms of each paste is
  fixed, whatever the size. Solid colours decode fast; pieces with detail cost
  more (E6: about 50–70 ms per megapixel on top).
- **Recipe for a stamp:** paste, drag it with the pen to about the spot, find
  it on screen, nudge it exact with arrow keys, commit with `S`. Each stamp
  also shows the floating-selection border for a moment (E14).
- The canvas always fits the viewport (E11), so (0, 0) is always on screen.
  With a scrolled canvas a paste would likely land at the top-left of the
  visible part; not tested.

## E10. The presenter's keys through a low-level hook (2026-11-18): **pass**

Script: [`poc/experiments/e10_keys.py`](../poc/experiments/e10_keys.py).
A `WH_KEYBOARD_LL` hook on its own thread, as the plan's design says: it takes
the show keys only while Paint (or the presenter view) is in front, and lets
the presenter's own keys through. The presenter tags its `SendInput` events in
`dwExtraInfo`; every untagged key counts as the user. The tests stand in for
the user with keys under a second tag. Paint maximised and in front, laptop
panel, final run (earlier runs agree).

| Test | Result |
|---|---|
| Swallowing, Paint in front, a floating paste on the canvas | The user's Right ×3, B and Esc **never reached Paint**: the paste stayed put, the tool stayed Select, Esc did not discard the paste. The presenter got next ×3, black, end |
| Control: the hook only watching | The same keys moved the paste 3 px and switched to the Fill tool, so they would have reached Paint |
| Another app in front | It got Right, Space and B; the presenter got nothing |
| Key press to the next slide on screen (2000×1000, already on the clipboard; 8 each) | **113 ms** through the hook against 112 ms for a direct Ctrl+V (an earlier run: 98 against 104). The hook costs nothing measurable |
| Hook callback called after the key is sent | 0.1 ms; **16 ms** (max 20) while another Python thread is busy (the GIL) |
| Fallback: `RegisterHotKey` on Right, no modifiers | The presenter got `WM_HOTKEY`, Paint got nothing; after unregistering, Right reached Paint again |

**A callback that stalls once** (another app in front; `LowLevelHooksTimeout`
is not set here, so this is Windows' default):

| Callback stalls | The sender's `SendInput` returns after | The stalled key | The next key reaches the app after | Hook afterwards |
|---|---|---|---|---|
| 200 ms | 200 ms | swallowed as normal | 1 ms | alive |
| 400 ms | 401 ms | **passed on anyway** | 2 ms | alive |
| 700 ms | 617 ms | passed on anyway | 86 ms | alive |
| 1,100 ms | 612 ms | passed on anyway | 300 ms | alive |
| 2,100 ms | 609 ms | passed on anyway | 316 ms | alive |

- **Windows waits about 300 ms for the hook, then passes the key on,** so a key
  the hook should have swallowed reaches Paint after all.
- **A stuck callback holds up the whole keyboard:** every key waits up to about
  300 ms, and whoever sent a key with `SendInput` (the presenter itself, for
  its pastes) blocks for up to about 610 ms.
- **Windows did not remove the hook** after any single stall, although its
  documentation says Windows 7 and later remove a hook that times out. It may
  take repeated time-outs; not tested.
- **A heartbeat detects a lost hook:** a tagged key with an unassigned code
  (0x97) that only the hook sees and swallows. With the hook removed it went
  unanswered; after reinstalling it came back.

**For the presenter:**

- The callback does only the tag check, the foreground check and a queue put
  (0.1 ms), on a thread that does nothing else. The pastes run elsewhere.
- In Python, other busy threads delay the callback by about 16 ms (the GIL).
  That leaves plenty of room under 300 ms, but a long C call that holds the
  GIL could reach it. That counts for C# at gate 4, or for keeping the hook in
  a small process of its own.
- Check the hook with the heartbeat now and then, and reinstall it if it goes
  quiet.
- **No presenter remote:** the user presents from the keyboard, so there are
  no remote keys to record. `e10_keys.py --listen 30` is there if one turns up.

## E12. Render a deck at a measured pixel size (2026-11-18): **pass**

Script: [`poc/experiments/e12_render.py`](../poc/experiments/e12_render.py).
A four-slide sample deck (its own text; slide 3 has three click steps, slide 4
a 400×200 box with a 1 px border) rendered by Marp (marp-cli 4.5.1), Slidev
(@slidev/cli 53.1.0, theme-default 0.25.0) and PowerPoint (16.0.20527, through
COM), at the projector's slide size 1854×875 and this laptop's 2650×1251. The
Node tools were installed in a temp folder, not the repo. Marp and Slidev
render through the installed Edge, headless; both start it with a throwaway
profile of their own (Marp a `marp-cli-*` folder, Slidev/Playwright a
non-persistent context), which the script keeps in a dedicated temp folder.
The user's Edge profiles were never used, and no Edge process was stopped.

**Two ways to reach the pixel size:**

| | Canvas set to the pixel size | **Fixed design canvas, rendered at a scale** |
|---|---|---|
| How | Marp theme `section` sized W×H; Slidev `canvasWidth` W | Marp: 1280×604 canvas, `--image-scale` W/1280. Slidev: 980 wide, `--scale` W/980. PowerPoint: slide in points, `Slide.Export` at W×H |
| Size | exact | PowerPoint exact; **Marp and Slidev can be 1 px off in height** (2650×1250, 1854×876) because the design canvas is rounded to whole CSS pixels |
| Layout (slide 2's text, as a fraction of the slide) | **changes with the screen:** Marp 0.044–0.292 of the width at 1854, 0.031–0.205 at 2650; Slidev 0.030–0.177 against 0.021–0.124 | **the same on every screen**, within 0.001, for all three |
| 1 px border | 1 px, crisp | as wide as the scale makes it (2–3 px, anti-aliased): drawn at that size, not resampled |

- **Use the fixed design canvas.** With the canvas set to the pixel size, the
  text keeps its CSS pixel size, so it shrinks relative to the slide on a
  bigger screen. That is a reflow, which the presentation constraints rule
  out. PowerPoint does the right thing by itself: a slide is vector and scales.
- **Crop or pad the odd row.** When Marp or Slidev comes out 1 px off, crop or
  pad one background row to the measured size. That is lossless (no
  resampling) and keeps viewport − slide even (E11).
- **Speaker notes** came back complete and in order from all three: Marp
  `--notes`, Slidev's rule (a slide's last HTML comment), PowerPoint's notes
  page through COM.
- **Slidev's `--with-clicks`** gives one image per click step (slide 3 gave 4),
  so build steps are whole slides, as the design says. PowerPoint's
  `Slide.Export` ignores animations: builds there would need one slide per
  step.
- Slidev's PNG export defaults to scale 2; always pass `--scale`.

**Render time** (scale mode, 2650×1251):

| Deck | Marp | Slidev | PowerPoint |
|---|---|---|---|
| 4 slides (7 Slidev images) | 2.5 s | 6.2 s | 0.2 s + 0.5 s to start |
| 40 slides (43 Slidev images) | 6.2 s | 11.0 s | 2.0 s + 0.5 s to start |
| So, roughly | 2 s + 0.1 s per slide | 5 s + 0.14 s per image | 0.5 s + 0.05 s per slide |

The loading painting has about 3 to 11 seconds to fill for a 40-slide deck;
a cache of rendered slides (per deck and size) makes the next start instant.

## E19. Interruptions (2026-11-18): **answered**

Script: [`poc/experiments/e19_interruptions.py`](../poc/experiments/e19_interruptions.py),
with [`toast.ps1`](../poc/experiments/toast.ps1) to show and remove a test
toast (sent as Windows PowerShell's app, removed from the notification history
afterwards). Paint maximised and in front, laptop panel. Screen grabs stay in
memory; only counts of changed pixels are reported.

| Question | Result |
|---|---|
| Can the documented API see Do not disturb? | **No.** `SHQueryUserNotificationState` said "accepts notifications" while Windows 11's Do not disturb was on (the taskbar showed its bell; a look at the screen confirmed it) |
| Does a toast show over maximised Paint? | **Yes**, with Do not disturb off: about 17,000 pixels of the toast corner changed (188 with it on, when nothing showed). Paint stayed in front: the toast does not take the focus |
| Windows' presentation mode (`PresentationSettings.exe /start`, a documented tool on laptops) | The state becomes "presentation mode" and `/stop` puts it back, but **it does not hold toasts back**: the same toast showed over Paint |
| Does Paint pop anything up at launch? | Only the "100%" zoom tooltip, by the status bar's zoom slider (from setting the zoom), clear of the canvas. The same in 3 of 3 launches; no What's new or sign-in prompts |
| Keep the display awake (`SetThreadExecutionState` with `ES_CONTINUOUS`, `ES_DISPLAY_REQUIRED`, `ES_SYSTEM_REQUIRED`) | Accepted. `powercfg /requests` would show it, but needs an elevated prompt |

The first run had Do not disturb on, which hid the toast; the user switched it
off for a second run.

**For the presenter's pre-flight check:** toasts do show over the slides, and
only Do not disturb stops them. Presentation mode does not, and Paint
maximised does not count as full screen. The documented API cannot read Do
not disturb (the state lives in undocumented places), so the check asks the
speaker to switch it on.

## E13. End-to-end mini presenter (2026-11-18): **pass** (the user's verdict: "awesome")

Script: [`poc/experiments/e13_presenter.py`](../poc/experiments/e13_presenter.py),
deck [`e13_deck.md`](../poc/experiments/e13_deck.md) (seven slides written
for it). The plan's presenter design end to end, on the laptop panel (the
taskbar is on the left there, so the viewport is 2784×1499 and the slide
2718×1283, 3.5 MP), on mains power, with `--auto` playing a scripted set of
keys: Next ×3, Back, End, Home, 3 + Enter, B twice, then four quick Nexts.

| Step | Result |
|---|---|
| Slide size | Measured from Paint maximised at 100%, then the largest reference shape in viewport − 66 with even margins (E11, E12) |
| Rendering (Marp, scale mode, Edge headless with a throwaway profile) | 5.3 s for 7 slides; 0 s from the cache on the next start. No Edge process left behind |
| Loading painting | A scribbled crayon sun, the progress bar's frame drawn with the Rectangle tool, the title as wobbly outlined letters (random colour), the bar coloured in with the crayon as slides finish. 14.8 s with rendering, 11.8 s from the cache: the painting, not the rendering, is the long pole |
| Key to slide on screen | **223 ms median** (n = 11), 182–321 ms for single keys. All 11 changes seen and committed |
| Keys during a paste | Combined: four quick Nexts went 2 → 3, then straight to the last slide in one paste (545 ms from the first of the three combined keys) |
| Black screen, back | 182 ms to black, 230 ms back to the slide |

- **Measuring can slow the show down.** The first run gave 0.8–3.6 s per slide
  and missed two slides: it read 36 screen pixels before and during every
  paste, and each read waits for a display refresh (E6). The presenter now
  works out in memory where the new slide differs from what is on screen and
  polls three of those points. Rule: never read more than a few screen pixels
  per slide.
- **Not built yet:** the screen picker (Tk does not load in the ARM64 Python,
  its DLL is missing, so it will be a small Win32 window; it needs two
  displays to test, E18), the presenter view, E11's calibration paste, and
  clearing out the cache.
- **The user drove it from the keyboard:** "apart from that, awesome", with
  three points, all handled:

  | Feedback | Change |
  |---|---|
  | Paint opens twice at start | The first window only measured the viewport. Now E11's rule predicts it (2784×1499, exact here), Paint opens once at the slide size, and reopens only if the prediction was off |
  | Must be able to get back to the loading painting, as slide 0 | A snapshot of the painting heads the slide list: Home, `0` + Enter, or Left from slide 1 go back to it; a number + Enter means that slide |
  | The loading painting needs more work | The designer's concept, The sky leaks ([spec](loading-painting.md)) |

- **Second scripted run** (14 slide changes, back to the painting by Home,
  `0` + Enter and Left, black and back, a burst of keys): all 14 seen,
  **230 ms median**, one Paint launch.
- **A bug found on the way:** paintctl reused its cached blank canvas file,
  which the user's File > Save during the manual run had overwritten, so the
  next launches opened the saved painting. The canvas origin, found from the
  white area, came out 510 px off and most pastes went unseen. paintctl now
  checks the file is blank and keeps a saved picture under another name. The
  presenter also waits for the canvas to sit centred before measuring, and
  later takes the origin from UIA's rectangle plus the offset measured while
  the canvas was blank (0, 1 px here).
- **Third round: the designer's painting**, [The sky leaks](loading-painting.md)
  ([`loading_painting.py`](../poc/experiments/loading_painting.py)), painted
  live after E27. A scripted run with the cached slides, `--simulate-prep 12`
  pacing the fill as if rendering took 12 s. Each time is when the step
  started drawing:

  | Time | Step |
  |---|---|
  | 1.8 s | The title, in Black this time (the colour is random), after choosing the Brush and its colour |
  | 5.5 s | The sun |
  | 8.0 s | The cloud, left open |
  | 8.9 s | The frame |
  | 10.2 s | The fill catches up: slides 1–5 of 7 |
  | 11.9 s | Birds |
  | 13.0 s | The fill: slides 6 and 7 |
  | 14.3 s | The bucket hesitates in the cloud, then clicks; the sky turns turquoise through the gap |
  | 15.6 s | The signature: Comic Sans, 48 pt, in a white box; the typo is put right with Backspace |
  | 24.1 s | Done |

  Then 230 ms median key to slide (n = 14), one Paint launch, and the
  painting as slide 0. The keyboard hook is on while it paints: the first key
  stops it and goes to slide 1. The painter's own keys (`B`, `T`, Backspace)
  carry the presenter's tag, and the date goes in as Unicode characters
  (`VK_PACKET`), which are not show keys.
- **Always the whole painting (the user's call):** the concept's short
  versions for a quick preparation (no sun under 9 s, no cloud or birds under
  6 s) would have been nearly every show, since a cached deck is ready at once.
  Now sun, cloud and birds come every time: 25 s with cached slides, then
  231 ms median key to slide.
- **Letters in colour (the user's addition):** after the sky, the bucket
  fills 1 to 4 random letters of the title, each in its own random colour.
  One screen read of the title's area, compared at half resolution with the
  letters' true outlines (even-odd, so holes stay out), finds white spots
  well inside letter bodies; a 5 × 5 px read before each tap skips a letter
  that is no longer white. 1.5 s for two letters, 3.5 s for four; holes
  stay white. A letter that the shaky outline splits in two gets one part
  filled, which looks hand-made.
- **The signature is slow:** about 9 s against the concept's 4 s, mostly
  choosing the font, size, Background fill and colours through UIA. The first
  key cuts it short, but it is the next thing to speed up.
- **What turned up while building it:**
  - **The font size** box accepts a value through UIA's Value pattern, and
    typed text, and Paint uses neither. Selecting an item of its list works,
    so only the listed sizes (… 28, 36, 48, 72 …) can be had. The date asks
    for 6% of the canvas height (the user wanted it larger than the concept's
    3.3%) and gets the largest listed size not above that: 48 pt here, where
    it had been Paint's 22.
  - **Paint keeps the font size across sessions, not only the font** (E16
    checked only the font). The painter writes both to a file before it
    changes them and puts them back after the show, so a run that dies half
    way is put right by the next one. Checked after a relaunch: Calibri, 22,
    Background fill off.
  - **Choosing another tool does not reliably commit a text box.** It
    committed in the painting's first runs and in two probes, and dropped the
    typed text in eight other probe runs (nothing on the canvas 4 s later). A
    pen tap on the canvas outside the box committed it in all 7 runs it was
    tried. With the Text tool the tap also opens a new, empty box, which the
    tool change then drops.
  - **A detour worth remembering:** in downscaled screenshots the Comic Sans
    date looked like another font, which sent the debugging the wrong way,
    and one of those probes changed the user's Paint font without putting it
    back (put back to Calibri and checked). Judge a font by measuring, not by
    eye: overlap the text's ink with renders of the candidate fonts (Comic Sans
    scored 0.80, Calibri 0.47). And every probe restores what it changes.

## E20. A C# live clipboard object (2026-11-18): **pass**

Script: [`poc/experiments/e20_csharp_clipboard.py`](../poc/experiments/e20_csharp_clipboard.py)
drives [`poc/dotnet/E20Clipboard`](../poc/dotnet/E20Clipboard) (C#, .NET 9,
native ARM64) over stdin. Each slide is a new data object offering PNG plus
the clipboard-history opt-out (E7, E8), pasted with Ctrl+V and committed with
`S`. Slide 1854×875, Paint maximised and in front, laptop panel. The C# side
logs every call on its object with `Stopwatch` time, the same clock as
Python's `perf_counter`.

| Data object | Pastes | Offer | Ctrl+V to Paint's `GetData` of the PNG | Ctrl+V to on screen (median / p95 / max) | Missed |
|---|---|---|---|---|---|
| **C#: our own `IDataObject`** (built-in COM interop, `OleSetClipboard`) | 200 | 4.2 ms | **4.4 ms** (p95 6.3) | **82.5 / 99.9 / 116.9 ms** | 0 |
| C#: WinForms `DataObject` with `Clipboard.SetDataObject(obj, false)` | 200 | 4.2 ms | not visible (see below) | 84.5 / 115.1 / 147.9 ms | 0 |
| Python: E1's object (the POC baseline) | 50 | 5.2 ms | 4.3 ms | 96.9 / 113.0 / 136.2 ms | 0 |

- **Both C# objects serve Paint as well as the Python one:** every slide
  pasted, no error dialog, nothing added to Win+V history.
- **Paint calls our own object exactly as it calls the Python one:**
  `EnumFormatEtc`, `GetData` PNG, `GetDataHere` PNG (refused with `E_NOTIMPL`,
  as in E1). Nothing else.
- **WinForms' `DataObject` is a black box:** the overridden `GetData` and
  `GetDataPresent` were never called, because WinForms answers OLE from its own
  internals. It works, but the presenter could not see when Paint has read a
  slide.
- **Use our own `IDataObject`.** It gives full control over the formats, the
  same behaviour as the proven POC object, and `GetData` doubles as a "Paint
  has the slide" timestamp. WinForms' object is the fallback.
- **Gotcha:** this laptop's culture writes 0,5 for 0.5. Anything the
  presenter writes for machines (logs, protocols, cache keys) must use the
  invariant culture.

## E23. The C# keyboard hook under memory pressure (2026-11-18): **pass**

Program: [`poc/dotnet/E23Hook`](../poc/dotnet/E23Hook) (C#, .NET 9, native
ARM64, workstation GC with background collection). A `WH_KEYBOARD_LL` hook on
its own thread swallows a probe key (unassigned code 0x97, so nothing reaches
any app) sent every 20 ms, 500 per load, tagged with its sequence number. The
probes come from a **separate, idle process**. A first version sent them from
a thread of the same process, which the collections suspended too, so no
probe was ever sent during a pause; it was thrown away. Both processes use
`Stopwatch`, one clock.

| Load (10 s each) | Missed | Send to callback: median / p99 / max | Gen 0 / gen 2 collections | Total GC pause | Longest full blocking pause |
|---|---|---|---|---|---|
| Idle | 0 | 0.08 / 1.6 / 2.0 ms | 0 / 0 | 0 | – |
| 4 threads churning small objects and 1 MB arrays | 0 | 0.10 / 1.6 / 3.2 ms | 4,288 / 1,804 | 1.7 s | 0.7 ms |
| Churn, a 400 MB live heap, a forced blocking compacting full GC every 2 s | 0 | 0.26 / 1.2 / 1.8 ms | 5,380 / 1,110 | 1.8 s | 1.4 ms |
| The same under `SustainedLowLatency` | 0 | 0.09 / 1.4 / 1.8 ms | 5,397 / 1,075 | 1.7 s | 1.1 ms |

- **The hook stays a hundred times inside Windows' limit.** Even with
  collections taking 17% of the time, each pause lasted about a millisecond,
  so no key waited more than 3.2 ms (the limit is about 300 ms, E10).
- **Why the pauses are short:** background GC does most of the marking while
  threads run, and a heap of byte arrays (like the presenter's PNG slides)
  holds no references to trace. A heap of many small linked objects would
  pause longer; the presenter does not need one.
- **No separate hook process is needed.** Keep the callback free of
  allocations and locks (it only writes a timestamp here), give the hook its
  own thread, and use the default workstation GC.

## E24. PDF as the adapter for any tool (2026-11-18): **pass**

Script: [`poc/experiments/e24_pdf.py`](../poc/experiments/e24_pdf.py), with
[`poc/dotnet/E24Pdf`](../poc/dotnet/E24Pdf) (C#, `Windows.Data.Pdf`, part of
Windows: nothing to install). E12's sample deck was exported to PDF by Marp,
Slidev and PowerPoint (`SaveAs` PDF), each PDF rendered at 2650 px wide (the
laptop's slide width), and every page compared with the same tool's own PNG
export at that size (E12, scale mode). Edge ran headless with throwaway
profiles; no Edge process was left behind.

| Exported by | Pages | Pixel size | Mean difference per channel (of 255) | Pixels off by > 40 | Time per page |
|---|---|---|---|---|---|
| Marp | 4 | 2650×1251 | 0.09–0.17 | 0.09–0.15% | 48–70 ms |
| Slidev | 4 | 2650×1251 | 0.13–0.32 | 0.13–0.38% | 51–61 ms |
| PowerPoint | 4 | 2650×1251 | 0.13–0.75 | 0.13–0.60% | 48–58 ms |

Loading a PDF took 44–49 ms, measuring the scale factor (below) about 200 ms
once.

- **The size needs one measurement.** `PdfPageRenderOptions.DestinationWidth`
  is in DIPs, and Windows turned them into pixels by a factor of **1.8** on
  this laptop (the panel is at 200%). Making the process DPI-aware did not
  change it. So render page 1 once, read the pixel width, and request
  width ÷ factor. That landed on exactly 2650 px; the height may need a pixel
  cropped or padded, as for Marp and Slidev (E12). A first run without this
  wrote 4770×2250 PNGs.
- **The pixels match the tools' own exports:** the differences are
  anti-aliasing at edges.
- **PDF is a universal and fast path.** About 50 ms per page, the speed of
  PowerPoint's `Slide.Export` and faster than Marp or Slidev (E12). Any page
  can be rendered on its own, so the show can start early (research 05 §7).
  Not measured: how long each tool takes to export the PDF.
- **Build steps are lost** unless the tool exports one page per step (Slidev
  `--with-clicks` can).

## E25. Past PowerPoint decks (2026-11-18): **pass**, for a synthetic deck and for 23 real decks

Script: [`poc/experiments/e25_legacy_ppt.py`](../poc/experiments/e25_legacy_ppt.py).
A made-up "legacy" deck: 4:3 (720×540 pt), entrance animations on click (Fly
In by paragraph, Appear on a shape), a hidden slide, a font the laptop lacks,
a picture. Saved as `.pptx` and as an old `.ppt` (PowerPoint 97-2003),
reopened read-only and exported slide by slide at the largest 4:3 size for the
laptop's viewport (2784×1499: 1910×1431, E11's rule).

| | `.pptx` | `.ppt` |
|---|---|---|
| Open | 0.06 s | 0.17 s |
| Export, 4 slides | 0.30 s | 0.32 s |
| Exported size | 1910×1431, every slide | the same |
| Animated content in the export | all there: the box and every bullet (the **final state**) | the same |
| Hidden slide | exported like any other; flagged by `SlideShowTransition.Hidden` | the same |
| Animations readable up front | `TimeLine.MainSequence.Count` (4 effects on slide 2) | the same |

- **Old `.ppt` files work** through the same path, slightly slower to open.
- **Build steps are lost, nothing else:** `Slide.Export` draws every animated
  object in place. A deck with builds shows each slide complete; splitting
  builds into steps (PPspliT-style, research 03 §4) is a later option.
- **Skip hidden slides** to match PowerPoint's own show; the adapter reads the
  flag.
- **Use exact-aspect sizes:** `Slide.Export` scales to exactly the width and
  height asked, so 1910×1431 for a 4:3 slide is stretched by 0.1%. The size
  rule should pick sizes that keep the deck's shape exactly (for 4:3, a width
  divisible by 4; 1908×1431 here) and still keep viewport − slide even (E11).
- **Fonts:** the Windows font list is the wrong check. It flagged Office's
  own default fonts (Aptos, Aptos Display), which Office fetches by itself, as
  missing. A font that is really missing is substituted exactly as in
  PowerPoint's own show on the same laptop, so the presenter can at most
  warn.
- **Not covered by the synthetic deck:** embedded video or audio (the export
  would show a still frame), and the real decks below.

**Part 2: the user's real decks.** Script:
[`poc/experiments/e25_real_decks.py`](../poc/experiments/e25_real_decks.py).
Every `.ppt` and `.pptx` in the user's work OneDrive and Documents, each
opened read-only without a window, in its own subprocess with a time limit and
PowerPoint's alerts off. Every visible slide was exported at the largest size
of the deck's exact shape for the laptop's viewport. The decks are
employer-internal, so only anonymous counts are recorded here; the exports
went to a temporary folder that was deleted.

| | Result |
|---|---|
| Decks | 23, all `.pptx` (none older), last saved 2016–2026; 370 slides, 1–48 per deck (median 13) |
| Shape | **16:9, all of them**: 2548×1433 on this laptop (0.44 px from exact) |
| Opened and exported | **23 of 23, every slide**: no export error, no slide that came out as a single colour |
| Time | opening at most 0.56 s; export 0.1 s per slide (median; 0.085–0.535 s), 5.9 s for the slowest deck |
| Animations | in 3 decks: 6 slides, 23 effects. They come out in their final state (the synthetic deck above) |
| Hidden slides, transitions, video or audio, embedded or linked objects, linked pictures, embedded fonts | none |
| SmartArt, tables | 8 SmartArt graphics in 4 decks, 34 tables in 9 decks: exported without error (PowerPoint draws them itself) |

- **PowerPoint's own renderer makes past decks the easy case:** every slide
  exported, quickly, at the exact size.
- **Expect builds to be the only loss.** Three decks of 23 use animations; a
  later PPspliT-style split (research 03 §4) could turn them into steps.
- **Gotcha:** `Presentation.Fonts(n).Embedded` raised `E_INVALIDARG` for
  some fonts in 6 decks; the first run lost those decks to it. The script
  now counts unreadable fonts instead of failing.

## E21. A finished paste without reading the screen (2026-11-18): **no event exists; poll the Crop button over UIA**

Script: [`poc/experiments/e21_paste_event.py`](../poc/experiments/e21_paste_event.py),
with [`poc/dotnet/E21Watch`](../poc/dotnet/E21Watch) (C#, FlaUI UIA3, MIT).
Paint enables its Crop button while a selection exists, so a paste turns it
on and the `S` commit turns it off. The watcher listened for three signals at
once while the harness pasted 1854×875 slides (a new PNG clipboard object
each) and timed them by a probe pixel (30 pastes; a second run of 20 for the
raw WinEvents).

| Signal | Paste seen | Against the slide's pixel | Commit (`S`) seen | Cost |
|---|---|---|---|---|
| UIA property-changed event, `IsEnabled`, anywhere in Paint's window | **never** (0 of 30) | – | never | – |
| WinEvents from Paint's process (window shown or hidden, MSAA state changes) | never: no popup shown; 80 state changes in a run, none tied to the pastes | – | – | – |
| **Polling the Crop button's `IsEnabled` over UIA** | **30 of 30**, 88 ms after Ctrl+V (median) | **2.5 ms before the pixel** (median; −12 to +9 ms) | **30 of 30, 47 ms after `S`** (35–71 ms) | 3.4 ms per read |
| Probe pixel (the POC's way) | 30 of 30, 94 ms after Ctrl+V | – | – | one display refresh (~17 ms) per read |

- **Paint offers no event for it.** Its WinUI controls do not raise UIA
  property-changed events for `IsEnabled`, and no WinEvent marks a paste.
- **Polling the Crop button is the better poll:**
  - it tells the paste within a few ms of the pixel;
  - each read is 5× cheaper than a screen read and does not wait for the
    display;
  - it needs no probe points chosen from the slide's content;
  - it also tells when the commit is done, which nothing did before.
- **Reading UIA does not take the focus:** Paint stayed in front, unlike UIA
  actions (E2).
- **For the presenter:** after Ctrl+V, read the Crop button every few ms until
  it is enabled, send `S`, then until it is disabled. Keep the three-point
  pixel poll (E13) as a fallback if a Paint update renames the button (E4's
  start-up checklist checks it).
- Not tried: Windows.Graphics.Capture frames. Polling the Crop button made it
  unnecessary.

## E22. One slide at a time (2026-11-18): **works; the first slide takes 1.9 s (Marp) or 5 s (Slidev)**

Script: [`poc/experiments/e22_per_slide.py`](../poc/experiments/e22_per_slide.py),
with [`e22_slide_server.mjs`](../poc/experiments/e22_slide_server.mjs), a Node
"slide server" that renders any slide on request. It uses the deck's own
toolchain and the Playwright that comes with it. Playwright for .NET ships no
ARM64 driver for Windows, and the renderers are child processes anyway
(research 05). Marp's slides come from its own HTML output (the bespoke
template with controls and transitions off), Slidev's from its own dev
server, at the URL `slidev export` uses. Edge runs headless in a
non-persistent context (a throwaway profile in the work folder); no Edge
process was left behind. E12's sample deck at 2650 px, slides asked for out
of order (the last before the second).

| | Server ready | First slide | Each further slide or click step | Against the tool's own export (E12) |
|---|---|---|---|---|
| Marp | 1.67 s | **1.92 s** | 145–152 ms | same size; identical (mean difference 0.0–0.2 of 255) |
| Slidev | 2.78 s | **5.03 s** (its first page compiles: 2.3 s) | 332–405 ms | same size; close (0.4–0.9; 3.4 on slide 1) |

- **Out-of-order rendering works:** any slide or click step, on request.
- **It trades total time for an early first slide.** A full export of 40
  slides takes 6.2 s (Marp) or 11 s (Slidev) before any slide is usable
  (E12); one at a time the first comes at 1.9 s or 5 s, but 40 take about 8 s
  or 20 s.
- **For the presenter:** start the slide server, ask for the first (or
  resume) slide, then the rest in deck order, and move a slide to the front
  when the speaker jumps to it. For a typical deck the loading painting (12–15
  s, E13) covers either way; early start matters for long decks and for
  resuming after a display change at a new size.
- PowerPoint (`Slide.Export`, 0.05 s a slide, E12) and PDF (50 ms a page,
  E24) already render one slide at a time.
- The pass criterion's "first slide under a second" is not met for these two
  tools; their start-up (HTML export, dev server, Edge) dominates.

## E26. What identifies a monitor (2026-11-18): **the EDID serial**; the replug test is part of E18

Program: [`poc/dotnet/E26MonitorId`](../poc/dotnet/E26MonitorId) (C#). For
every active display it reads the GDI name (`\.\DISPLAY1`, what the POC
used), the monitor device path and output technology from
`QueryDisplayConfig`, and the EDID that Windows keeps in the registry
(`Enum\DISPLAY\<model>\<instance>\Device Parameters\EDID`): manufacturer,
product code, serial number and serial text. It also lists every monitor
Windows remembers. Serials and paths are printed only as short hashes (enough
to compare runs), unless `--raw`. Only the laptop panel was connected; the
replug itself is tested in E18.

| | Result |
|---|---|
| The active display | the laptop panel: output technology **"internal"**, EDID serial number present (no serial text, no friendly name) |
| Two runs in a row | identical: GDI name, path, EDID identity |
| Monitors Windows remembers | 5: the panel, three monitors of one model (two with the very same product code) and one other monitor, all connected at some point through the dock |
| EDID serials | **every one present** (serial number and serial text on all four external monitors), and **distinct** for the two monitors of the same model and product code |

- **Use the EDID as the identity:** manufacturer, product code, serial number
  and serial text. It belongs to the monitor, not to the port or to Windows'
  numbering, and it tells apart even two identical monitors here. The GDI
  name is only a handle for the current session.
- **Fallback when a display has no serial** (common on cheap monitors and
  projectors): the model plus the device path, which is tied to the port.
  Good enough for "the same cable back in".
- **The picker's default is easy:** the first display whose output
  technology is not "internal" (the plan's "the non-laptop screen as
  default").
- **For E18 tomorrow:** run E26 before, during and after the replug. Does
  the GDI name change? Does the path change on another port or dock? Does the
  EDID identity stay the same, as it should?

## E27. What the loading painting needs from Paint (2026-11-18): **answered**

Script: [`poc/experiments/e27_painting_checks.py`](../poc/experiments/e27_painting_checks.py).
The designer's concept ([The sky leaks](loading-painting.md)) was simulated in
a browser; this checks its open points in Paint, maximised at 100% on a
2000×1000 canvas, drawing with the synthetic pen (E15).

| Question | Result |
|---|---|
| Brush width | **size + 1 px**, from size 0 (1 px) to size 20 (21 px), as E16 found for the Pencil |
| The bucket through a gap | Light turquoise, clicked inside a Brush cloud left open by a 40 px gap: **the far corner of the canvas turns turquoise too**. The gag works |
| Closed shapes | A rectangle drawn as one stroke **stays white** inside. A ring drawn as one stroke **leaked**: Paint drops the last segment of a stroke, so the ring never closed |
| Edges after the flood | 488 pixels of a 400 × 230 area around the cloud are neither white, turquoise nor stroke black: a thin anti-aliased rim along the strokes |
| The Text tool's toolbar | Font family, Font size, Bold, Italic, Underline, Strikethrough, the alignment buttons and a **Background fill** check box: the opaque box the signature needs. UIA lists each control twice |
| A white box before the flood | A Rectangle with a solid white fill and a light grey outline stays white. A white box without an outline is the same white as the canvas, so the flood would take it |

- **The signature goes after the bucket**, not before it as in the concept,
  so its white box needs no outline.
- **Send the end point of every stroke twice**, so that shapes meant to close
  do close, and the cloud's gap is the only way out.
- **Brush size for a width of w px: w − 1.**

## E18. Display changes mid-show (2026-11-19): **pass** for a display leaving and coming back; the rest still open

Tool: [`poc/dotnet/E18Displays`](../poc/dotnet/E18Displays) (C#) switches
displays off and on through `SetDisplayConfig`, the API Windows itself uses
when a display goes away: the desktop shrinks, windows move off it, and
`WM_DISPLAYCHANGE` goes out. Harness:
[`poc/experiments/e18_display_changes.py`](../poc/experiments/e18_display_changes.py)
runs `presenter present --verify --soak`, makes the change mid-show, and
answers the screen picker over UI Automation the way a speaker would. Deck: a
synthetic 4-page PDF. Displays: the laptop panel plus three 2560×1440
monitors at 100%.

| Run | What happened |
|---|---|
| Demo mode on DISPLAY2, no change | Viewport 2512×1229, slide 2066×1163, the 8 help slides; every frame pixel-exact, median 219 ms a key |
| DISPLAY3 (presenting) off for 12 s, then back | The picker came up 4.8 s after the change and listed DISPLAY3 again when it returned. Picking it: **Repaste**, the same slide pixel-exact. The EDID identity and the GDI name survived the round trip |
| DISPLAY3 off for 20 s; DISPLAY4 picked meanwhile | Picker after 3.8 s, **MovePaint** to DISPLAY4, the current slide shown again 5.9 s after the change. When DISPLAY3 came back, **a second picker came up**; DISPLAY4 picked again: Repaste. 68 slide changes, none unconfirmed |
| Only the laptop panel for 12 s | Picker after 4.4 s; four speaker keys skipped while it was up (Paint not in front). DISPLAY3 picked once it was back: **Repaste**, 22 s after the change including the wait |

- **The picker follows the displays.** It came up every time, listed a
  returning display without being reopened, and the show resumed at the
  current slide. With the same display back there was nothing to render.
- **A slide change during the switch verifies at 0 %:** the screen it was
  read from had gone. That is the moment of the change, not a wrong slide.
- **On DISPLAY4 every frame verified at about 82 %** (black 84 %). An Edge
  picture-in-picture window, always on top, covered part of the canvas. The
  audience would see it too. **A pre-flight check could warn about
  always-on-top windows over the chosen display;** not built yet.
- **The primary display cannot be switched off this way:** `SetDisplayConfig`
  refuses with error 87. Present on a non-primary display for these runs.
- **Not covered yet:** a real cable pull (a software detach keeps the port,
  so the GDI name and device path stayed the same), picking the laptop panel
  (NewSlideSize), resolution and scaling changes, duplicate/extend with
  Win+P, FancyZones, and a projector.
- **Harness gotcha:** reading the presenter's stderr in a thread *and*
  calling `communicate()` splits the log between them, and lines go missing.
  A run whose 40-key soak ended the show before the display came back also
  looked like "no picker on return". The harness now waits on stdout alone,
  keeps the whole log (`--log`), and the soak should outlast the change
  (`--soak 130` for a 20 s change).
