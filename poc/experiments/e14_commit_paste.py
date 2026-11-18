"""E14: commit a pasted slide so it shows cleanly, without the mouse.

A paste in Paint is a floating selection: a dashed border and a floating
selection toolbar sit over it until it is committed. Esc discards it and Enter
does nothing (E11). For each candidate method this script pastes E11's test
pattern at slide size into maximised Paint at 100%, applies the method, and
polls the screen until it matches the source pixel for pixel (clean) or a
timeout passes. It also checks whether the method moved the mouse.

Run from poc/paintctl, so paintctl's venv is used:

    uv run python ../experiments/e14_commit_paste.py [--monitor \\\\.\\DISPLAY2] [--size 2446x1163] [--method key-S]

Hands off the keyboard and mouse while it runs; it sends keys and, for the
baseline, one mouse click. Progress lines on stderr include a timeline of
differing pixels after the method; the JSON summary goes to stdout. The size
also decides whether the canvas border overdraws an edge row (see E11/E14 in
docs/poc-results.md), so a method can look "not clean" for that reason alone.
"""
from __future__ import annotations

import argparse
import ctypes
import json
import sys
import time
from ctypes import wintypes

import e11_display_area as e11  # same folder; also sets DPI awareness
from paintctl import core
from PIL import ImageGrab

_VK_CONTROL, _VK_V, _VK_A, _VK_ESCAPE = 0x11, 0x56, 0x41, 0x1B


def _cursor() -> tuple[int, int]:
    p = wintypes.POINT()
    ctypes.windll.user32.GetCursorPos(ctypes.byref(p))
    return p.x, p.y


def _tap(vk: int) -> None:
    e11._keys((vk, False), (vk, True))


def _diff_at(grab, pattern, dx: int, dy: int) -> tuple[int, list | None]:
    """Differing pixels with the pattern at a known offset (fast: one alignment,
    where e11.compare tries 81 and takes ~2 s at slide size)."""
    n, bbox = e11._mismatch(grab.convert("RGB"), pattern, dx, dy)
    return n, list(bbox) if bbox else None


def _uia_button(hwnd: int, auto_id: str):
    return core._uia_window(hwnd).child_window(
        auto_id=auto_id, control_type="Button").wrapper_object()


# Each method gets (hwnd, ctx) and commits the floating paste somehow.
METHODS = {
    "none": lambda h, ctx: None,
    "click-margin": lambda h, ctx: e11._click(ctx["canvas"]["left"] + ctx["w"] // 4,
                                              ctx["viewport"]["top"] + 4),
    "key-P": lambda h, ctx: _tap(ord("P")),
    "key-S": lambda h, ctx: _tap(ord("S")),
    "ctrl-A-then-Esc": lambda h, ctx: (
        e11._keys((_VK_CONTROL, False), (_VK_A, False), (_VK_A, True),
                  (_VK_CONTROL, True)),
        time.sleep(0.3), _tap(_VK_ESCAPE)),
    "uia-crop": lambda h, ctx: _uia_button(h, "CropButton").invoke(),
}


def run(mon: dict, w: int, h: int, name: str, pattern) -> dict:
    hwnd = e11.open_maximised(mon, w, h)
    try:
        i = core.info(hwnd)
        c, vp = i["canvas_on_screen"], i["viewport"]
        P = e11._PAD
        box = (c["left"] - P, c["top"] - P, c["left"] + w + P, c["top"] + h + P)
        e11.to_clipboard(pattern)
        if not core.focus(hwnd):
            return {"error": "could not focus Paint"}
        e11._keys((_VK_CONTROL, False), (_VK_V, False), (_VK_V, True),
                  (_VK_CONTROL, True))
        probe = (P + w // 4, P + h // 4)
        t0 = time.perf_counter()
        while time.perf_counter() - t0 < 10:
            if ImageGrab.grab(bbox=box, all_screens=True).getpixel(probe) \
                    != (255, 255, 255):
                break
            time.sleep(0.05)
        time.sleep(0.5)
        floating = e11.compare(ImageGrab.grab(bbox=box, all_screens=True), pattern)
        cursor_before = _cursor()
        ctx = {"canvas": c, "viewport": vp, "w": w, "h": h}
        t1 = time.perf_counter()
        try:
            METHODS[name](hwnd, ctx)
        except Exception as exc:  # noqa: BLE001 - report, don't stop the run
            return {"error": f"{type(exc).__name__}: {exc}",
                    "floating_diff_px": floating["diff_pixels"]}
        dx, dy = floating["offset_from_uia_rect"]
        clean_after = None
        n = None
        timeline = []  # [seconds, differing px, bbox] whenever the count changes
        while time.perf_counter() - t1 < 6:
            t = time.perf_counter() - t1
            n, bbox = _diff_at(ImageGrab.grab(bbox=box, all_screens=True),
                               pattern, dx, dy)
            if not timeline or timeline[-1][1] != n:
                timeline.append([round(t, 2), n, bbox])
            if n == 0:
                clean_after = round(t, 3)
                break
        after = core.info(hwnd)
        return {
            "clean": clean_after is not None,
            "clean_after_s": clean_after,
            "diff_px_at_end": n,
            "floating_diff_px": floating["diff_pixels"],
            "timeline": timeline,
            "mouse_moved": _cursor() != cursor_before,
            "canvas_px_after": after.get("canvas_px"),
            "zoom_measured_after": after.get("zoom_measured"),
            "title": after.get("title"),
        }
    finally:
        core.close(hwnd, discard=True)


def main() -> int:
    p = argparse.ArgumentParser(description=__doc__,
                                formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--monitor", default=r"\\.\DISPLAY2")
    p.add_argument("--size", default="2446x1163")
    p.add_argument("--method", action="append", choices=list(METHODS),
                   help="default: all")
    a = p.parse_args()
    w, h = (int(v) for v in a.size.lower().split("x"))
    mon = next(m for m in e11.monitors() if m["device"] == a.monitor)
    pattern = e11.test_pattern(w, h)
    results = {}
    for name in a.method or list(METHODS):
        results[name] = run(mon, w, h, name, pattern)
        print(f"{name}: {json.dumps(results[name])}", file=sys.stderr, flush=True)
    json.dump({"monitor": a.monitor, "size": [w, h], "methods": results},
              sys.stdout, indent=2)
    print()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
