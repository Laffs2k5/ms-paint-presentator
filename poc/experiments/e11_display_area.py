"""E11: Paint's display area and the slide size that shows pixel-exact.

Display area: the largest canvas that maximised Paint shows completely at 100%
zoom, with no scrollbars. Slide: the display area rounded down so that
viewport - slide is even on both axes. With an odd difference Paint centres the
canvas on a half pixel; the image snaps to whole pixels, but the canvas border
is drawn across the slide's last row or column.

At exactly 64 px of margin per axis, whether Paint shows a scrollbar varies
between runs, so `slide_rule` reports the size to use: viewport - 66 on both
axes (even, with a pixel of slack per side).

The pixel test pastes a test pattern through the clipboard, commits it with a
click in the top margin away from the selection's resize handles (Esc discards
a paste, Enter does nothing), grabs the screen around the canvas, finds the
offset where the pattern matches best (UIA's canvas rect can be a pixel off)
and counts the pixels that still differ.

Run from poc/paintctl, so paintctl's venv is used:

    uv run python ../experiments/e11_display_area.py [--monitor \\\\.\\DISPLAY2] [--out DIR]

Without --monitor it tests one monitor per distinct (resolution, scaling, work
area). It opens, moves, maximises and closes Paint windows, and sends Ctrl+V and
a mouse click to Paint, so leave the keyboard and mouse alone while it runs.
Prints JSON. The screen grabs it saves can include notification toasts that
pop up meanwhile, so don't commit them.
"""
from __future__ import annotations

import argparse
import ctypes
import io
import json
import sys
import time
from ctypes import wintypes
from pathlib import Path

from paintctl import core  # sets per-monitor-v2 DPI awareness first

import win32api  # noqa: E402
import win32clipboard  # noqa: E402
from PIL import Image, ImageChops, ImageGrab  # noqa: E402

_user32 = ctypes.windll.user32
_VK_CONTROL, _VK_V = 0x11, 0x56
_SLACK_MARGIN = 66  # viewport - slide on each axis that always fits (even)
_KEYUP = 0x0002


# --- monitors ---------------------------------------------------------------------
def monitors() -> list[dict]:
    out = []
    for hmon, _, _ in win32api.EnumDisplayMonitors():
        info = win32api.GetMonitorInfo(hmon)
        dpi_x, dpi_y = wintypes.UINT(), wintypes.UINT()
        ctypes.windll.shcore.GetDpiForMonitor(int(hmon), 0, ctypes.byref(dpi_x),
                                              ctypes.byref(dpi_y))
        out.append({"device": info["Device"],
                    "bounds": core.Rect.of(info["Monitor"]),
                    "work": core.Rect.of(info["Work"]),
                    "primary": bool(info["Flags"] & 1),
                    "dpi": dpi_x.value})
    return out


def distinct(mons: list[dict]) -> list[dict]:
    seen, keep = set(), []
    for m in sorted(mons, key=lambda m: not m["primary"]):
        key = (m["bounds"].width, m["bounds"].height, m["work"].width,
               m["work"].height, m["dpi"])
        if key not in seen:
            seen.add(key)
            keep.append(m)
    return keep


# --- Paint on a given monitor ---------------------------------------------------------
def open_maximised(mon: dict, width: int, height: int) -> int:
    """New Paint window with a blank width x height canvas, maximised on mon,
    at 100% zoom with the canvas centred."""
    hwnd = core.launch(canvas=(width, height), reuse=False)
    w = mon["work"]
    # Small restored rect inside the target monitor. Crossing a DPI boundary
    # makes Paint rescale itself, so keep it well inside.
    core.set_window_rect(hwnd, 900, 700, w.left + 40, w.top + 40)
    core.maximize(hwnd)
    core.set_zoom(hwnd, "fit")  # fit then 100 re-centres a scrolled canvas
    core.set_zoom(hwnd, 100)
    time.sleep(0.3)
    return hwnd


def probe(mon: dict, width: int, height: int) -> dict:
    try:
        hwnd = open_maximised(mon, width, height)
    except core.PaintError:  # e.g. the window was closed by hand: once more
        hwnd = open_maximised(mon, width, height)
    try:
        i = core.info(hwnd)
    finally:
        core.close(hwnd, discard=True)
    if i["monitor"]["device"] != mon["device"]:
        raise RuntimeError(f"Paint ended up on {i['monitor']['device']}, "
                           f"not {mon['device']}")
    scroll = i.get("scrollable") or {}
    shown = i.get("canvas_on_screen") or {}
    fits = (i.get("zoom_percent") == 100
            and i.get("canvas_px") == {"width": width, "height": height}
            # 1:1 on screen (the zoom box can read 100 at 99.8%). UIA's rect
            # can be a pixel off, so allow 1 px; 99.8% on 2400 px is 5 px.
            and abs((shown.get("width") or 0) - width) <= 1
            and abs((shown.get("height") or 0) - height) <= 1
            and scroll.get("horizontal") is False
            and scroll.get("vertical") is False
            and i.get("canvas_fully_visible") is True)
    return {"size": [width, height], "fits": fits, "info": i}


def largest(fits_at, lo: int, hi: int) -> int:
    """Largest n in [lo, hi) with fits_at(n), given fits_at(lo) and not fits_at(hi)."""
    while hi - lo > 1:
        mid = (lo + hi) // 2
        if fits_at(mid):
            lo = mid
        else:
            hi = mid
    return lo


# --- pixel test -------------------------------------------------------------------------
def test_pattern(width: int, height: int) -> Image.Image:
    """Every pixel encodes its own coordinates (R = x mod 256, G = y mod 256,
    B = the high bits), so any shift, scaling or colour change shows up. Plus a
    1 px black/white checkerboard in the middle, which any resampling smears."""
    rows = []
    for y in range(height):
        g, bhi = y & 255, (y >> 8) & 15
        rows.append(bytes(v for x in range(width)
                          for v in (x & 255, g, ((x >> 8) & 15) << 4 | bhi)))
    img = Image.frombytes("RGB", (width, height), b"".join(rows))
    box = min(256, width // 2, height // 2)
    x0, y0 = (width - box) // 2, (height - box) // 2
    checker = Image.frombytes("L", (box, box), bytes(
        255 * ((x + y) & 1) for y in range(box) for x in range(box)))
    img.paste(checker.convert("RGB"), (x0, y0))
    return img


def to_clipboard(img: Image.Image) -> None:
    with io.BytesIO() as buf:
        img.save(buf, "BMP")
        dib = buf.getvalue()[14:]  # CF_DIB = BMP without BITMAPFILEHEADER
    win32clipboard.OpenClipboard()
    try:
        win32clipboard.EmptyClipboard()
        win32clipboard.SetClipboardData(win32clipboard.CF_DIB, dib)
    finally:
        win32clipboard.CloseClipboard()


def _keys(*events: tuple[int, bool]) -> None:
    for vk, up in events:
        _user32.keybd_event(vk, 0, _KEYUP if up else 0, 0)
        time.sleep(0.03)


_PAD = 4  # search +/- this many pixels around UIA's canvas rect


def _mismatch(grab: Image.Image, want: Image.Image, dx: int, dy: int):
    w, h = want.size
    crop = grab.crop((_PAD + dx, _PAD + dy, _PAD + dx + w, _PAD + dy + h))
    mask = ImageChops.difference(crop, want).convert("L") \
        .point(lambda v: 255 if v else 0)
    return mask.histogram()[255], mask.getbbox()


def compare(grab: Image.Image, want: Image.Image) -> dict:
    """Best alignment of want inside grab (grab = canvas rect + _PAD all round)."""
    grab = grab.convert("RGB")
    best = None
    for dy in range(-_PAD, _PAD + 1):
        for dx in range(-_PAD, _PAD + 1):
            n, bbox = _mismatch(grab, want, dx, dy)
            if best is None or n < best[0]:
                best = (n, bbox, dx, dy)
    n, bbox, dx, dy = best
    return {"identical": n == 0, "diff_pixels": n,
            "diff_bbox": list(bbox) if bbox else None,
            "offset_from_uia_rect": [dx, dy]}


def _click(x: int, y: int) -> None:
    """Left click at screen x, y, then put the cursor back."""
    old = wintypes.POINT()
    _user32.GetCursorPos(ctypes.byref(old))
    _user32.SetCursorPos(x, y)
    time.sleep(0.05)
    _user32.mouse_event(0x0002, 0, 0, 0, 0)  # MOUSEEVENTF_LEFTDOWN
    _user32.mouse_event(0x0004, 0, 0, 0, 0)  # MOUSEEVENTF_LEFTUP
    time.sleep(0.05)
    _user32.SetCursorPos(old.x, old.y)


def pixel_test(mon: dict, width: int, height: int, out: Path) -> dict:
    pattern = test_pattern(width, height)
    tag = f"{mon['device'].strip(chr(92) + '.').lower()}-{width}x{height}"
    hwnd = open_maximised(mon, width, height)
    try:
        i = core.info(hwnd)
        c, vp = i["canvas_on_screen"], i["viewport"]
        box = (c["left"] - _PAD, c["top"] - _PAD,
               c["left"] + width + _PAD, c["top"] + height + _PAD)
        to_clipboard(pattern)
        if not core.focus(hwnd):
            return {"error": "could not focus Paint"}
        t0 = time.perf_counter()
        _keys((_VK_CONTROL, False), (_VK_V, False), (_VK_V, True),
              (_VK_CONTROL, True))
        probe_xy = (_PAD + width // 4, _PAD + height // 4)
        shown = None
        while time.perf_counter() - t0 < 10:
            if ImageGrab.grab(bbox=box, all_screens=True) \
                    .getpixel(probe_xy) != (255, 255, 255):
                shown = round(time.perf_counter() - t0, 2)
                break
            time.sleep(0.05)
        time.sleep(0.5)
        # Commit with a click outside the canvas, in the top margin a quarter
        # of the way across: away from the selection's 8 resize handles. Near
        # a corner, at 200% scaling, the click lands on a handle and Paint
        # rescales the slide.
        _click(c["left"] + width // 4, vp["top"] + 4)
        time.sleep(0.8)
        grab = ImageGrab.grab(bbox=box, all_screens=True)
        grab.save(out / f"e11-{tag}-screen.png")
        result = compare(grab, pattern)
        result.update(paste_visible_after_s=shown,
                      margin=[vp["width"] - width, vp["height"] - height])
        if not result["identical"]:
            pattern.save(out / f"e11-{tag}-pattern.png")
        return result
    finally:
        core.close(hwnd, discard=True)


# --- one monitor ------------------------------------------------------------------------
def measure(mon: dict, out: Path) -> dict:
    t0 = time.monotonic()
    count = 0

    def fits(w: int, h: int) -> bool:
        nonlocal count
        count += 1
        r = probe(mon, w, h)
        print(f"  {mon['device']} {w}x{h}: {'fits' if r['fits'] else 'no'}",
              file=sys.stderr)
        return r["fits"]

    small = probe(mon, 640, 400)
    count += 1  # the probes below count themselves in fits()
    vp = small["info"]["viewport"]
    vw, vh = vp["width"], vp["height"]
    lo_h, lo_w = max(400, vh - 240), max(640, vw - 240)
    if not (fits(640, lo_h) and fits(lo_w, 400)):
        raise RuntimeError("search floor does not fit; widen the search range")
    max_h = largest(lambda h: fits(640, h), lo_h, vh + 1)
    max_w = largest(lambda w: fits(w, 400), lo_w, vw + 1)
    corner = fits(max_w, max_h)
    trade_offs = None
    if corner:
        area = (max_w, max_h)
    else:
        # A scrollbar on one axis can take room from the other, so the two
        # maxima need not fit together. Search both trade-offs.
        trade_offs = [
            (max_w, largest(lambda h: fits(max_w, h), lo_h, max_h)),
            (largest(lambda w: fits(w, max_h), lo_w, max_w), max_h),
        ]
        area = max(trade_offs, key=lambda s: s[0] * s[1])
    # even margins: viewport - slide even on both axes (no half-pixel centring)
    slide = (area[0] - (vw - area[0]) % 2, area[1] - (vh - area[1]) % 2)
    slide_fits = slide == area or fits(*slide)
    i = small["info"]
    result = {
        "device": mon["device"], "primary": mon["primary"],
        "monitor": [mon["bounds"].width, mon["bounds"].height],
        "work_area": [mon["work"].width, mon["work"].height],
        "dpi": mon["dpi"], "scale_percent": round(mon["dpi"] * 100 / 96),
        "maximised_window": [i["visible_frame"]["width"],
                             i["visible_frame"]["height"]],
        "viewport": [vw, vh],
        "max_per_axis": [max_w, max_h],
        "max_per_axis_fit_together": corner,
        "trade_offs": [list(t) for t in trade_offs] if trade_offs else None,
        "display_area": list(area),
        "margin": [vw - area[0], vh - area[1]],
        "chrome": [mon["work"].width - vw, mon["work"].height - vh],
        "slide": list(slide), "slide_fits": slide_fits,
        "slide_rule": [vw - _SLACK_MARGIN, vh - _SLACK_MARGIN],
        "pixel_test_slide": pixel_test(mon, *slide, out),
        "pixel_test_display_area": (pixel_test(mon, *area, out)
                                    if area != slide else "same"),
        "probes": count, "seconds": round(time.monotonic() - t0, 1),
    }
    print(json.dumps(result), file=sys.stderr, flush=True)  # survive a later crash
    return result


def main() -> int:
    p = argparse.ArgumentParser(description=__doc__,
                                formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--monitor", action="append",
                   help=r"device name like \\.\DISPLAY2 (repeatable)")
    p.add_argument("--out", default=".", help="folder for images (default: cwd)")
    a = p.parse_args()
    out = Path(a.out)
    out.mkdir(parents=True, exist_ok=True)
    mons = monitors()
    if a.monitor:
        mons = [m for m in mons if m["device"] in a.monitor]
    else:
        mons = distinct(mons)
    results = [measure(m, out) for m in mons]
    json.dump(results, sys.stdout, indent=2)
    print()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
