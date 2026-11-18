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
