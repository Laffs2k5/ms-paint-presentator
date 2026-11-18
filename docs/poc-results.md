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

| Monitor | Scaling | Work area | Viewport (maximised) | Largest canvas that fits | **Slide** (viewport − 66) | Pixel test |
|---|---|---|---|---|---|---|
| 2560×1440 | 100% | 2512×1440 | 2512×1229 | 2448×1165 (3 of 4 runs) | **2446×1163** (4 of 4) | identical |
| 2880×1920 (laptop) | 200% | 2784×1920 | 2784×1499 | 2720×1435 | **2718×1433** (4 of 4) | identical |

"Identical" means the screen grab of the pasted test pattern matches the source
exactly: 0 of 2.8–3.9 million pixels differ. Each pattern pixel encodes its own
coordinates, so any shift or scaling would show.

**The rule:**

> viewport = (work-area width, work-area height − 210.5 × scale), rounded half up
> (211 px at 100%, 421 px at 200%; a maximised window has no side borders)
>
> slide = viewport − 66 px on each axis

- **Paint keeps 64 px around the canvas** (32 per side), in *physical* pixels at
  both 100% and 200%. At exactly 64 px the scrollbar decision varies between
  runs, so the rule keeps 1 px of slack per side.
- **viewport − slide must be even on both axes.** With an odd difference Paint
  centres the canvas on a half pixel: the image snaps cleanly to whole pixels,
  but the canvas border line is drawn across the slide's last row or column
  (seen: exactly one column wrong at 2425 wide, a margin of 87).

**Predicted for 16:10 reference screens** (taskbar at the bottom, 48 px × scale;
100% and 200% measured, other scalings extrapolated, so verify on the screen):

| Screen | Scaling | Viewport | Slide | Slide aspect |
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
- **A slide-sized paste took 0.6–0.8 s to appear** (plain `SetClipboardData` +
  Ctrl+V, 2.8–3.9 MP, timed coarsely by polling screen grabs). That is well over
  E6's 150 ms target, so E6 is the next priority.
- **Notifications cover the slide.** A Teams toast appeared over the canvas in
  one run. Turn on Do Not Disturb before presenting.
