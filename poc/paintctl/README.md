# paintctl

Small instrumentation tool for Windows 11 Paint: **launch, size, maximise, measure,
zoom, screenshot and UIA-dump**. Every command prints JSON. The same functions are
exposed as an **MCP server**, so Claude (Code or Desktop) on the laptop can drive
Paint and read exact pixel geometry.

> Tested on Windows 11 ARM64 (build 26300) with Paint 11.2609.401.0, English UI,
> at 100% and 200% display scaling. Every CLI command works. The MCP server has
> not been run yet. Background:
> [docs/research/04-instrumenting-paint.md](../../docs/research/04-instrumenting-paint.md),
> results: [docs/poc-results.md](../../docs/poc-results.md).

## Install (Windows 11)

```powershell
winget install --id=astral-sh.uv -e        # Python + venv manager
cd poc\paintctl
uv sync                                    # creates .venv with pywin32, pywinauto, pillow
uv run paintctl --help
uv sync --extra mcp                        # only if you want the MCP server (see below)
```

### Windows on ARM64

Use a native ARM64 Python, so timings don't include x64 emulation. All core
dependencies have `win_arm64` wheels.

```powershell
uv sync --python cpython-3.13-windows-aarch64-none
uv run python -c "import sysconfig; print(sysconfig.get_platform())"   # expect win-arm64
```

- **`UV_PYTHON` wins over everything.** If it is set (for example to an x64
  system Python), every plain `uv run`/`uv sync` silently rebuilds `.venv` with
  that interpreter, and a `.python-version` pin is ignored. Pass `--python …`
  on each call, or override `UV_PYTHON` for the session.
- **The MCP extra does not install on ARM64 Python.** `mcp` needs
  `cryptography`, which has no `win_arm64` wheel and would build from source.
  Run the MCP server from an x64 Python instead.

## Use

```powershell
uv run paintctl launch --canvas 1280x720 --maximize   # blank canvas of exactly 1280x720 px
uv run paintctl info                                   # geometry report (below)
uv run paintctl zoom fit                               # or: zoom 100
uv run paintctl fit-viewport 1920 1080                 # make the render area exactly 1920x1080
uv run paintctl resize 1600 1000 --x 0 --y 0           # outer window size/position
uv run paintctl maximize ; uv run paintctl restore ; uv run paintctl focus
uv run paintctl screenshot out.png --region viewport   # window|client|viewport|canvas
uv run paintctl uia-dump --depth 8 > uia.json          # discover AutomationIds
uv run paintctl list                                   # all Paint windows (hwnd, title)
uv run paintctl close --discard                        # close; "Don't save" if prompted
```

`--hwnd N` (before the subcommand) targets a specific window when several are open.

### What `info` reports (all physical screen pixels)

| Key | Meaning |
|---|---|
| `dpi`, `scale_percent`, `dpi_mode` | Monitor DPI (96 = 100%). `dpi_mode` should be `per-monitor-v2`, otherwise the numbers are virtualised |
| `monitor.mode` / `bounds` / `work_area` | Display resolution and refresh rate, monitor rect, and the area without the taskbar |
| `window_rect` | `GetWindowRect`, including Windows 11's invisible resize borders |
| `visible_frame` | DWM extended frame bounds: what you actually see |
| `client_rect` | Client area in screen coordinates |
| `mspaintview_rect` | The Win32 `MSPaintView` child window |
| **`viewport`** | UIA `scrollViewer`: the **render area** where the canvas can appear |
| **`canvas_on_screen`** | UIA `image`: the canvas at the current zoom, **clipped to the viewport** by UIA |
| **`canvas_px`** | Document size from the status bar (`CanvasSizeTextBlock`). Missing in a narrow window, see below |
| `zoom_percent` | From `ZoomValuesComboBox`. **Not exact**: it read "121" at a measured 121.6% and "100" at a measured 99.8% |
| `zoom_measured` | The real zoom: canvas width on screen ÷ `canvas_px` width. `null` when the canvas is clipped |
| `canvas_clipped` | `canvas_on_screen` is smaller than `canvas_px` × zoom: the canvas is scrolled or bigger than the viewport |
| `scrollable` | `horizontal`/`vertical` from the viewport's UIA ScrollPattern: whether Paint shows scrollbars. Both `false` = the whole canvas fits |
| `canvas_visible_on_screen` | viewport ∩ canvas |
| `canvas_fully_visible` | Whole canvas on screen. `null` when it can't be checked (no `canvas_px`) |
| `viewport_in_canvas_px` | How many document pixels fit the viewport at the current zoom |
| `zoom_check` | Expected vs actual on-screen canvas size (sanity check, ≈1% slack from the rounded zoom) |
| `errors` | UIA lookups that failed (e.g. a Paint update renamed something) |

## MCP server

```powershell
# Claude Code (on the laptop)
claude mcp add paintctl -- uv run --extra mcp --directory C:\src\ms-paint-presentator\poc\paintctl paintctl-mcp
```

Claude Desktop (`%APPDATA%\Claude\claude_desktop_config.json`):

```json
{
  "mcpServers": {
    "paintctl": {
      "command": "uv",
      "args": ["run", "--extra", "mcp", "--directory", "C:\\src\\ms-paint-presentator\\poc\\paintctl", "paintctl-mcp"]
    }
  }
}
```

Tools: `paint_launch`, `paint_list`, `paint_info`, `paint_window`
(maximize/restore/focus/resize/fit_viewport), `paint_close`, `paint_zoom`,
`paint_screenshot` (returns the image), `paint_uia_dump`.

Claude Code running natively on Windows can also just call the CLI and parse the
JSON. The MCP server is for Claude Desktop/Cowork, or if you prefer typed tools.

## Notes and known gaps

- **`uia-dump` redacts the signed-in account.** Paint's account button is named
  `Signed in as <full name>`, and dumps get committed. The redaction matches the
  English text only, so check a dump from a localised Paint by hand.
- **Localised UI:** AutomationIds are stable across languages, but *names* aren't.
  `zoom fit` looks up the button named "Fit to window", which fails on a Norwegian
  UI, and that button has **no AutomationId** (nor does Layers). A localised
  lookup needs the translated name, or the button's position next to
  `ZoomValuesComboBox`.
- **Re-centre the canvas** with `zoom fit` then `zoom 100`. After a move between
  monitors the canvas can end up scrolled, which `info` reports as
  `canvas_clipped: true`.
- **Fit to window can leave the slider at exactly 100.0 while the real zoom is
  99.8%** (a canvas just smaller than the viewport). Setting 100 again is then
  a no-op, so `zoom N` steps to N±1 first. Check `zoom_measured` when exact
  1:1 matters.
- **Moving across a DPI boundary resizes the window.** Paint is per-monitor DPI
  aware, so `resize` onto a 200% monitor is followed by Paint scaling itself for
  the new DPI (2400×1600 became 4778×1929). Run `fit-viewport` after the move:
  it iterates until the viewport is exact.
- **100% zoom is one document pixel per physical pixel**, also at 200% display
  scaling. Fit to window gives a fractional zoom (resampled, so soft), and the
  zoom it picks varied between runs in the same maximised window.
- **`launch` does not bring Paint to the foreground.** Run `focus` before
  anything that sends keystrokes.
- **Closing:** Paint hides its window within ~50 ms of `WM_CLOSE` but can take
  0.3–6 s more to destroy it, so `close` waits for the window to be hidden. The
  save prompt is a WinUI dialog whose buttons have locale-independent
  AutomationIds: `PrimaryButton` (Save), `SecondaryButton` (Don't save),
  `CloseButton` (Cancel).
- **Window managers can move Paint.** PowerToys FancyZones moves windows into
  zones on launch (if Paint was snapped before) and when displays change.
  Exclude `mspaint.exe` in FancyZones for clean measurements.
- **Exact canvas size** is done by opening a blank PNG of that size
  (`%TEMP%\paintctl\canvas-WxH.png`). That is simpler and more robust than Paint's
  Resize dialog (which scales) or dragging the resize handle (DoomPaint's trick).
- Changing the **display resolution** is deliberately not included. Use
  `fit-viewport` to emulate a projector-sized render area on any screen.
- Credits: UIA anchors and the PrintWindow capture approach come from
  [DoomPaint](https://github.com/markrussinovich/DoomPaint) (MIT, © Mark Russinovich).
