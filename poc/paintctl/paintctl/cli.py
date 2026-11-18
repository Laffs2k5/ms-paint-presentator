"""paintctl command line. Every command prints JSON to stdout.

    paintctl launch [--canvas 1280x720 | --image slide.png] [--maximize] [--new]
    paintctl list
    paintctl info
    paintctl maximize | restore | focus
    paintctl resize WIDTH HEIGHT [--x X --y Y]      # outer window size
    paintctl fit-viewport WIDTH HEIGHT              # canvas viewport size
    paintctl zoom PERCENT|fit
    paintctl screenshot OUT.png [--region window|client|viewport|canvas]
    paintctl uia-dump [--depth N]
"""
from __future__ import annotations

import argparse
import json
import sys

from . import core


def _size(text: str) -> tuple[int, int]:
    w, _, h = text.lower().partition("x")
    return int(w), int(h)


def main(argv: list[str] | None = None) -> int:
    p = argparse.ArgumentParser(prog="paintctl", description=__doc__,
                                formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--hwnd", type=int, help="target a specific Paint window")
    sub = p.add_subparsers(dest="cmd", required=True)

    s = sub.add_parser("launch")
    g = s.add_mutually_exclusive_group()
    g.add_argument("--canvas", type=_size, help="blank canvas WxH, e.g. 1280x720")
    g.add_argument("--image", help="open this image")
    s.add_argument("--maximize", action="store_true")
    s.add_argument("--new", action="store_true", help="don't reuse a running Paint")

    sub.add_parser("list")
    sub.add_parser("info")
    sub.add_parser("maximize")
    sub.add_parser("restore")
    sub.add_parser("focus")

    s = sub.add_parser("resize")
    s.add_argument("width", type=int)
    s.add_argument("height", type=int)
    s.add_argument("--x", type=int)
    s.add_argument("--y", type=int)

    s = sub.add_parser("fit-viewport")
    s.add_argument("width", type=int)
    s.add_argument("height", type=int)

    s = sub.add_parser("zoom")
    s.add_argument("value", help="percent (e.g. 100) or 'fit'")

    s = sub.add_parser("screenshot")
    s.add_argument("path")
    s.add_argument("--region", default="window",
                   choices=["window", "client", "viewport", "canvas"])

    s = sub.add_parser("uia-dump")
    s.add_argument("--depth", type=int, default=6)

    a = p.parse_args(argv)
    try:
        if a.cmd == "launch":
            hwnd = core.launch(image=a.image, canvas=a.canvas, reuse=not a.new)
            if a.maximize:
                core.maximize(hwnd)
            result = core.info(hwnd)
        elif a.cmd == "list":
            result = core.list_paint_windows()
        else:
            hwnd = core.require_paint(a.hwnd)
            if a.cmd == "info":
                result = core.info(hwnd)
            elif a.cmd == "maximize":
                core.maximize(hwnd)
                result = core.info(hwnd)
            elif a.cmd == "restore":
                core.restore(hwnd)
                result = core.info(hwnd)
            elif a.cmd == "focus":
                result = {"foreground": core.focus(hwnd)}
            elif a.cmd == "resize":
                core.set_window_rect(hwnd, a.width, a.height, a.x, a.y)
                result = core.info(hwnd)
            elif a.cmd == "fit-viewport":
                vp = core.fit_viewport(hwnd, a.width, a.height)
                result = {"viewport": vp.to_dict() if vp else None,
                          "exact": bool(vp and vp.width == a.width
                                        and vp.height == a.height)}
            elif a.cmd == "zoom":
                result = {"zoom_percent": core.set_zoom(hwnd, a.value)}
            elif a.cmd == "screenshot":
                result = core.screenshot(hwnd, a.path, a.region)
            elif a.cmd == "uia-dump":
                result = core.uia_dump(hwnd, a.depth)
            else:  # pragma: no cover
                p.error(f"unknown command {a.cmd}")
    except core.PaintError as exc:
        json.dump({"error": str(exc)}, sys.stdout)
        print()
        return 1
    json.dump(result, sys.stdout, indent=2, ensure_ascii=False)
    print()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
