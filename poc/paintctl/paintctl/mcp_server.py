"""paintctl as an MCP server (stdio), so Claude Code / Claude Desktop on the
Windows laptop can drive and measure Paint with structured results.

    claude mcp add paintctl -- uv run --directory C:\\path\\to\\poc\\paintctl paintctl-mcp
"""
from __future__ import annotations

import tempfile
from pathlib import Path

from mcp.server.fastmcp import FastMCP, Image

from . import core

mcp = FastMCP("paintctl")


def _hwnd(hwnd: int | None) -> int:
    return core.require_paint(hwnd)


@mcp.tool()
def paint_launch(canvas_width: int | None = None, canvas_height: int | None = None,
                 image: str | None = None, maximize: bool = False,
                 new_window: bool = False) -> dict:
    """Start Paint (or reuse a running one). Give canvas_width+canvas_height for a
    blank canvas of exactly that many pixels, or image to open a file. Returns the
    full geometry report (see paint_info)."""
    canvas = ((canvas_width, canvas_height)
              if canvas_width and canvas_height else None)
    hwnd = core.launch(image=image, canvas=canvas, reuse=not new_window)
    if maximize:
        core.maximize(hwnd)
    return core.info(hwnd)


@mcp.tool()
def paint_list() -> list[dict]:
    """List open Paint document windows (hwnd + title)."""
    return core.list_paint_windows()


@mcp.tool()
def paint_info(hwnd: int | None = None) -> dict:
    """Geometry of Paint in physical screen pixels: monitor mode and DPI scale,
    window/visible-frame/client rects, the canvas viewport rect (render area),
    the canvas rect on screen, canvas size in document pixels, zoom percent,
    and derived values (visible canvas rect, document pixels that fit the
    viewport at the current zoom)."""
    return core.info(_hwnd(hwnd))


@mcp.tool()
def paint_window(action: str, width: int | None = None, height: int | None = None,
                 x: int | None = None, y: int | None = None,
                 hwnd: int | None = None) -> dict:
    """Window control. action: maximize | restore | focus | resize (outer window
    width/height, optional x/y) | fit_viewport (make the canvas viewport exactly
    width x height pixels). Returns paint_info afterwards."""
    h = _hwnd(hwnd)
    if action == "maximize":
        core.maximize(h)
    elif action == "restore":
        core.restore(h)
    elif action == "focus":
        core.focus(h)
    elif action == "resize":
        core.set_window_rect(h, int(width), int(height), x, y)
    elif action == "fit_viewport":
        core.fit_viewport(h, int(width), int(height))
    else:
        raise ValueError(f"unknown action {action!r}")
    return core.info(h)


@mcp.tool()
def paint_zoom(zoom: str, hwnd: int | None = None) -> dict:
    """Set Paint's view zoom: a percent like "100" or "fit"."""
    return {"zoom_percent": core.set_zoom(_hwnd(hwnd), zoom)}


@mcp.tool()
def paint_screenshot(region: str = "viewport", hwnd: int | None = None) -> Image:
    """Capture Paint (works even if occluded). region: window | client |
    viewport | canvas."""
    out = Path(tempfile.gettempdir()) / "paintctl" / f"shot-{region}.png"
    out.parent.mkdir(exist_ok=True)
    core.screenshot(_hwnd(hwnd), str(out), region)
    return Image(path=str(out))


@mcp.tool()
def paint_uia_dump(max_depth: int = 6, hwnd: int | None = None) -> list[dict]:
    """Dump Paint's UI Automation tree (control type, name, AutomationId,
    class, rect) to discover controls such as the layers panel."""
    return core.uia_dump(_hwnd(hwnd), max_depth)


def main() -> None:
    mcp.run()


if __name__ == "__main__":
    main()
