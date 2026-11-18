# paintctl

Small instrumentation tool for Windows 11 Paint: **launch, size, maximise, measure,
zoom, screenshot and UIA-dump**. Every command prints JSON. The same functions are
exposed as an **MCP server**, so Claude (Code or Desktop) on the laptop can drive
Paint and read exact pixel geometry.

> ⚠️ Written from research and DoomPaint's proven UIA anchors, **not yet run on
> Windows**. The first POC task is to run it and fix what breaks. See
> [docs/research/04-instrumenting-paint.md](../../docs/research/04-instrumenting-paint.md).

## Install (Windows 11)

```powershell
winget install --id=astral-sh.uv -e        # Python + venv manager
cd poc\paintctl
uv sync                                    # creates .venv with pywin32, pywinauto, pillow, mcp
uv run paintctl --help
```

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
| **`canvas_on_screen`** | UIA `image`: the canvas at the current zoom (may extend off-screen) |
| **`canvas_px`** | Document size from the status bar (`CanvasSizeTextBlock`) |
| `zoom_percent` | From `ZoomValuesComboBox` |
| `canvas_visible_on_screen`, `canvas_fully_visible` | viewport ∩ canvas |
| `viewport_in_canvas_px` | How many document pixels fit the viewport at the current zoom |
| `zoom_check` | Expected vs actual on-screen canvas size (sanity check) |
| `errors` | UIA lookups that failed (e.g. a Paint update renamed something) |

## MCP server

```powershell
# Claude Code (on the laptop)
claude mcp add paintctl -- uv run --directory C:\src\ms-paint-presentator\poc\paintctl paintctl-mcp
```

Claude Desktop (`%APPDATA%\Claude\claude_desktop_config.json`):

```json
{
  "mcpServers": {
    "paintctl": {
      "command": "uv",
      "args": ["run", "--directory", "C:\\src\\ms-paint-presentator\\poc\\paintctl", "paintctl-mcp"]
    }
  }
}
```

Tools: `paint_launch`, `paint_list`, `paint_info`, `paint_window`
(maximize/restore/focus/resize/fit_viewport), `paint_zoom`, `paint_screenshot`
(returns the image), `paint_uia_dump`.

Claude Code running natively on Windows can also just call the CLI and parse the
JSON. The MCP server is for Claude Desktop/Cowork, or if you prefer typed tools.

## Notes and known gaps

- **Localised UI:** AutomationIds are stable across languages, but *names* aren't.
  `zoom fit` looks up the button named "Fit to window", which fails on a Norwegian
  UI. Run `uia-dump`, find its AutomationId, and switch the lookup.
- **Exact canvas size** is done by opening a blank PNG of that size
  (`%TEMP%\paintctl\canvas-WxH.png`). That is simpler and more robust than Paint's
  Resize dialog (which scales) or dragging the resize handle (DoomPaint's trick).
- Changing the **display resolution** is deliberately not included. Use
  `fit-viewport` to emulate a projector-sized render area on any screen.
- Credits: UIA anchors and the PrintWindow capture approach come from
  [DoomPaint](https://github.com/markrussinovich/DoomPaint) (MIT, © Mark Russinovich).
