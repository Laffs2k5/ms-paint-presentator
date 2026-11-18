# Research: instrumenting Paint (launch, size, measure)

> Status: 2026-11-18. Goal: a simple, reliable way for us (and Claude on the
> laptop) to start Paint, set the window or render-area size, maximise, and read
> back the render area and canvas geometry in **physical pixels**, for the POC
> experiments and later for the presenter itself.

## TL;DR

- General-purpose Windows automation (Claude computer use, Windows-MCP,
  mcp-windows) is good for **exploring** Paint. It is not precise enough for
  **measuring** it: screenshots and element boxes are not a pixel-exact
  geometry contract.
- So we built a small, purpose-made tool: **[`poc/paintctl`](../../poc/paintctl)**
  (Python, pywin32 + pywinauto/UIA). It is a CLI with JSON output plus an MCP server
  wrapping the same functions. It is built on the UIA AutomationIds that
  DoomPaint already proved against Windows 11 Paint. **Not yet run on Windows.**
  Making it work is POC step 0.
- The simplest setup on the laptop is **Claude Code + the `paintctl` CLI**.
  Add the MCP server if you want typed tools in Claude Desktop/Cowork. Keep
  Windows-MCP or Accessibility Insights around for ad-hoc discovery.

## Options compared

| Option | What it is | Launch/size/maximise | Exact render-area pixels | Notes |
|---|---|---|---|---|
| **Claude computer use** (Cowork / Claude Code desktop control on Windows, research preview) | Claude sees screenshots and clicks/types on your real desktop | Yes (by clicking) | **No.** Screenshot-estimated | Great for "go poke at Paint". Availability depends on plan: one source says not on Team/Enterprise plans ([Thurrott](https://www.thurrott.com/a-i/anthropic/334498/anthropic-brings-claude-computer-use-to-windows), [The Decoder](https://the-decoder.com/claude-code-and-cowork-now-let-anthropics-ai-take-control-of-your-mac-or-windows-desktop/)) **[verify for your account]** |
| **[Windows-MCP](https://github.com/CursorTouch/Windows-MCP)** (CursorTouch, Python, MIT) | MCP server: UIA "state" snapshot of interactive elements, click/type/keys/wait, screenshot, PowerShell; installable as a Claude Desktop extension or `uvx windows-mcp` | Yes (launch, keys, PowerShell) | Partly: element bounding boxes from UIA, no Paint-specific semantics | Good **discovery** tool (dump Paint's controls). Must run on the Windows side (not inside WSL) |
| **[mcp-windows](https://github.com/sbroenne/mcp-windows)** (sbroenne, .NET) | MCP server: `window_management` (find/activate/move/resize), `ui_find/ui_click/ui_read` by name, `screenshot_control` | Yes, including resize | Partly: element metadata | Name-based lookups are **localised** (Norwegian UI ≠ English names). Won't drive elevated windows unless elevated itself |
| [ghuntley/mcp-server-microsoft-paint](https://github.com/ghuntley/mcp-server-microsoft-paint) | Paint-specific MCP (Rust): connect, activate, canvas dimensions, draw lines/shapes/pixels | Launch/activate | `get_canvas_dimensions` | Aimed at *drawing*. Interesting, but not our display path |
| **Accessibility Insights for Windows** / `inspect.exe` (Windows SDK) | Manual UIA tree inspectors | — | Shows rects | Best for a human hunting AutomationIds (layers panel, full screen) |
| **`paintctl` (ours)** | Purpose-built CLI + MCP | `launch --canvas WxH`, `maximize`, `resize`, **`fit-viewport W H`** | **Yes:** viewport (`scrollViewer`), canvas on screen (`image`), canvas px (status bar), zoom, DPI, monitor mode | Small and deterministic. We own it, and it becomes the presenter's window-management module later |

## How `paintctl` measures things

- **Physical pixels:** the process opts into per-monitor-v2 DPI awareness before
  any Win32/UIA call. Without it, a 150%-scaled laptop screen reports virtualised
  coordinates.
- **Render area** = UIA element `AutomationId=scrollViewer` (the canvas viewport).
  **Canvas on screen** = `AutomationId=image`. **Document size** = the status bar
  text `AutomationId=CanvasSizeTextBlock`. **Zoom** = `ZoomValuesComboBox`
  (read) and `ZoomSliderControl` (write). It cross-checks
  `canvas_px × zoom ≈ canvas_on_screen`.
- **Window size** has three different "sizes": `GetWindowRect` (includes
  invisible ~7 px resize borders on Windows 11), DWM extended frame bounds (the
  visible window), and the client rect. All three are reported.
- **Exact canvas size:** open a generated blank PNG of W×H (`mspaint path.png`).
  There is no dialog and no drag.
- **Exact render-area size:** `fit-viewport W H` resizes the window iteratively until
  the viewport measures W×H. It iterates because scrollbars appear and disappear.
  This emulates a projector's resolution on any screen.
- **Capture:** `PrintWindow(PW_RENDERFULLCONTENT)` works even when Paint is occluded.
  It crops to window, client, viewport or visible canvas.

## Known risks

- **Localisation:** names like "Fit to window", "Edit", "Paste" depend on the
  Windows display language. AutomationIds don't. Use `uia-dump` to replace every
  name-based lookup with AutomationIds. (DoomPaint's menu paster uses names too.)
- **Paint updates** can rename AutomationIds. `info.errors` will show which lookup
  broke.
- UIA calls are relatively slow (tens of ms). They are fine for setup and
  measurement but must stay **out of the per-slide hot path**.

## Sources

- DoomPaint UIA anchors: <https://github.com/markrussinovich/DoomPaint/blob/main/mspaintdoom/paint_out.py>
- Windows-MCP: <https://github.com/CursorTouch/Windows-MCP>, listing: <https://mcpservers.org/servers/CursorTouch/Windows-MCP>
- mcp-windows: <https://github.com/sbroenne/mcp-windows>
- ghuntley Paint MCP: <https://ghuntley.com/mcp/>
- Claude computer use on Windows: <https://www.thurrott.com/a-i/anthropic/334498/anthropic-brings-claude-computer-use-to-windows>, <https://the-decoder.com/claude-code-and-cowork-now-let-anthropics-ai-take-control-of-your-mac-or-windows-desktop/>
- DPI awareness: <https://learn.microsoft.com/windows/win32/hidpi/high-dpi-desktop-application-development-on-windows>
- DWM extended frame bounds: <https://learn.microsoft.com/windows/win32/api/dwmapi/ne-dwmapi-dwmwindowattribute>
