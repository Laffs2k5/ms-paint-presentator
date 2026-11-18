"""E6: how fast does a slide-sized paste show up in Paint?

For each slide size: open a blank canvas of that size in maximised Paint at
100% zoom, then paste N distinct slides, one after another, the way a presenter
would: put the slide on the clipboard, send Ctrl+V (one atomic SendInput
batch), wait until it is on screen, commit it with `S` (E14), pause, repeat.

Per paste it records:
- getdata_ms: Ctrl+V until Paint asks the clipboard for the image (OLE mode:
  the live IDataObject from DoomPaint timestamps Paint's GetData call).
- visible_ms: Ctrl+V until the new slide is on screen, read straight from the
  screen: every slide carries small uniquely coloured probe squares, sampled
  with 1x1 BitBlts. A screen read waits for the display refresh, so this is
  resolved to one frame (~16.7 ms at 60 Hz), like what the audience sees.

Modes: `ole` (DoomPaint's live IDataObject, set once and updated in place) or
`plain` (EmptyClipboard + SetClipboardData(CF_DIB) per slide). `plain` is only
a comparison: from about 2.8 MP it drops pastes and can pop Paint's modal
"Can't complete operation" dialog, which then has to be dismissed by hand.

Run from poc/paintctl, so paintctl's venv is used:

    uv run python ../experiments/e6_paste_speed.py [--mode ole|plain] [--monitor \\\\.\\DISPLAY2] [--size 2446x1163 ...] [-n 20]

Hands off the keyboard and mouse while it runs. PowerToys Mouse Without
Borders' clipboard sharing should be off: it reads every clipboard image.
Prints JSON with per-size statistics; progress on stderr.
"""
from __future__ import annotations

import argparse
import ctypes
import io
import json
import statistics
import sys
import time
from ctypes import wintypes

import e11_display_area as e11  # same folder; also sets DPI awareness
import win32gui
from paintctl import core
from PIL import Image

# Own DLL instances with explicit types: handles are pointer-sized (the default
# c_int return would truncate them), and the shared ctypes.windll objects are
# also typed by pywinauto.
_user32 = ctypes.WinDLL("user32", use_last_error=True)
_gdi32 = ctypes.WinDLL("gdi32", use_last_error=True)
_user32.GetDC.restype = wintypes.HDC
_user32.GetDC.argtypes = (wintypes.HWND,)
_user32.ReleaseDC.argtypes = (wintypes.HWND, wintypes.HDC)
_user32.SendInput.restype = wintypes.UINT
_gdi32.CreateCompatibleDC.restype = wintypes.HDC
_gdi32.CreateCompatibleDC.argtypes = (wintypes.HDC,)
_gdi32.CreateCompatibleBitmap.restype = wintypes.HBITMAP
_gdi32.CreateCompatibleBitmap.argtypes = (wintypes.HDC, ctypes.c_int, ctypes.c_int)
_gdi32.SelectObject.restype = wintypes.HGDIOBJ
_gdi32.SelectObject.argtypes = (wintypes.HDC, wintypes.HGDIOBJ)
_gdi32.BitBlt.argtypes = (wintypes.HDC, ctypes.c_int, ctypes.c_int, ctypes.c_int,
                          ctypes.c_int, wintypes.HDC, ctypes.c_int, ctypes.c_int,
                          wintypes.DWORD)
_gdi32.GetPixel.restype = wintypes.DWORD
_gdi32.GetPixel.argtypes = (wintypes.HDC, ctypes.c_int, ctypes.c_int)
_gdi32.DeleteObject.argtypes = (wintypes.HGDIOBJ,)
_gdi32.DeleteDC.argtypes = (wintypes.HDC,)
_VK_CONTROL, _VK_V, _VK_S = 0x11, 0x56, 0x53
_PROBE = 16  # probe square size, px


# --- atomic keyboard input ----------------------------------------------------------
class _KEYBDINPUT(ctypes.Structure):
    _fields_ = (("wVk", wintypes.WORD), ("wScan", wintypes.WORD),
                ("dwFlags", wintypes.DWORD), ("time", wintypes.DWORD),
                ("dwExtraInfo", ctypes.c_size_t))


class _MOUSEINPUT(ctypes.Structure):
    _fields_ = (("dx", wintypes.LONG), ("dy", wintypes.LONG),
                ("mouseData", wintypes.DWORD), ("dwFlags", wintypes.DWORD),
                ("time", wintypes.DWORD), ("dwExtraInfo", ctypes.c_size_t))


class _INPUTUNION(ctypes.Union):
    _fields_ = (("ki", _KEYBDINPUT), ("mi", _MOUSEINPUT))


class _INPUT(ctypes.Structure):
    _fields_ = (("type", wintypes.DWORD), ("u", _INPUTUNION))


def send_keys(*events: tuple[int, bool]) -> None:
    """(vk, key_up) events as one SendInput batch: no gaps between them."""
    arr = (_INPUT * len(events))()
    for i, (vk, up) in enumerate(events):
        arr[i].type = 1  # INPUT_KEYBOARD
        arr[i].u.ki = _KEYBDINPUT(vk, 0, 0x0002 if up else 0, 0, 0)
    if _user32.SendInput(len(events), arr, ctypes.sizeof(_INPUT)) != len(events):
        raise ctypes.WinError(ctypes.get_last_error())


CTRL_V = ((_VK_CONTROL, False), (_VK_V, False), (_VK_V, True), (_VK_CONTROL, True))
KEY_S = ((_VK_S, False), (_VK_S, True))


# --- reading the screen ---------------------------------------------------------------
class ScreenProbe:
    """Read single screen pixels fast: BitBlt 1x1 from the screen DC."""

    def __init__(self):
        self._screen = _user32.GetDC(0)
        self._mem = _gdi32.CreateCompatibleDC(self._screen)
        self._bmp = _gdi32.CreateCompatibleBitmap(self._screen, 1, 1)
        _gdi32.SelectObject(self._mem, self._bmp)

    def pixel(self, x: int, y: int) -> tuple[int, int, int]:
        _gdi32.BitBlt(self._mem, 0, 0, 1, 1, self._screen, x, y, 0x00CC0020)
        c = _gdi32.GetPixel(self._mem, 0, 0)
        return c & 0xFF, (c >> 8) & 0xFF, (c >> 16) & 0xFF

    def close(self) -> None:
        _gdi32.DeleteObject(self._bmp)
        _gdi32.DeleteDC(self._mem)
        _user32.ReleaseDC(0, self._screen)


# --- slides ------------------------------------------------------------------------------
def probe_points(w: int, h: int) -> list[tuple[int, int]]:
    """Top-left corners of the probe squares, inside the slide and away from the
    edges (selection border) and the bottom centre (selection toolbar)."""
    return [(w // 4, h // 4), (3 * w // 4, h // 4), (w // 4, 3 * h // 4),
            (3 * w // 4, 3 * h // 4)]


def colour(k: int) -> tuple[int, int, int]:
    return (37 + 71 * k) % 256, (11 + 113 * k) % 256, (199 + 59 * k) % 256


def dib(img: Image.Image) -> bytes:
    with io.BytesIO() as buf:
        img.save(buf, "BMP")
        return buf.getvalue()[14:]  # CF_DIB = BMP without BITMAPFILEHEADER


def slide_image(base: Image.Image, k: int) -> Image.Image:
    """Slide k: the test pattern with probe squares in slide k's colour."""
    img = base.copy()
    for x, y in probe_points(*base.size):
        img.paste(colour(k), (x, y, x + _PROBE, y + _PROBE))
    return img


def make_slides(w: int, h: int, n: int) -> list[bytes]:
    base = e11.test_pattern(w, h)
    return [dib(slide_image(base, k)) for k in range(n)]


# --- clipboard modes --------------------------------------------------------------------
class OleClipboard:
    def __init__(self):
        import doompaint_clipserve
        self._srv = doompaint_clipserve.ClipboardServer()
        self.getdata_at: float | None = None

    def offer(self, data: bytes) -> None:
        self.getdata_at = None

        def render():
            if self.getdata_at is None:
                self.getdata_at = time.perf_counter()
            return data

        self._srv.publish(render_fn=render)

    def close(self) -> None:
        self._srv.finalize()


class PlainClipboard:
    getdata_at = None

    def offer(self, data: bytes) -> None:
        import win32clipboard
        win32clipboard.OpenClipboard()
        try:
            win32clipboard.EmptyClipboard()
            win32clipboard.SetClipboardData(win32clipboard.CF_DIB, data)
        finally:
            win32clipboard.CloseClipboard()

    def close(self) -> None:
        pass


# --- one size ---------------------------------------------------------------------------
def pct(values: list[float], p: float) -> float:
    s = sorted(values)
    return round(s[min(len(s) - 1, int(round(p / 100 * (len(s) - 1))))], 1)


def summary(values: list[float]) -> dict | None:
    if not values:
        return None
    return {"p50": pct(values, 50), "p95": pct(values, 95), "max": round(max(values), 1),
            "mean": round(statistics.mean(values), 1)}


def measure(mon: dict, w: int, h: int, n: int, warmup: int, clip, timeout: float,
            pause: float, trigger: str = "ctrl-v", background: bool = False) -> dict:
    slides = make_slides(w, h, n + warmup)
    hwnd = e11.open_maximised(mon, w, h)
    screen = ScreenProbe()
    rows = []
    standin = None
    try:
        info = core.info(hwnd)
        c, vp = info["canvas_on_screen"], info["viewport"]
        pts = [(c["left"] + x + _PROBE // 2, c["top"] + y + _PROBE // 2)
               for x, y in probe_points(w, h)]
        if background:
            # E2: another window in front (over Paint's toolbar, clear of the
            # canvas). Commit then goes through a posted `S` (E2).
            import e2_background_paste as e2
            standin = e2.StandIn(mon["work"].left + 200, mon["work"].top + 20, 900,
                                 160)  # short: its shadow must not reach the canvas
            front = standin.hwnd
        else:
            front = hwnd
        if not core.focus(front):
            return {"error": "could not focus the front window"}
        for k, data in enumerate(slides):
            want = colour(k)
            clip.offer(data)
            t0 = time.perf_counter()
            if trigger == "wm-command":  # E2: posted Edit > Paste, no keys
                _user32.PostMessageW(hwnd, 0x0111, 0xE125, 0)
            else:
                send_keys(*CTRL_V)
            # A screen read syncs with the display refresh (~16.7 ms at 60 Hz),
            # so poll one probe per frame, then confirm the others once.
            seen = None
            polls = 0
            while time.perf_counter() - t0 < timeout:
                polls += 1
                if screen.pixel(*pts[0]) == want:
                    seen = time.perf_counter()
                    if all(screen.pixel(x, y) == want for x, y in pts[1:]):
                        break
                    seen = None
            get = clip.getdata_at
            rows.append({
                "k": k, "warmup": k < warmup,
                "getdata_ms": round((get - t0) * 1000, 1) if get else None,
                "visible_ms": round((seen - t0) * 1000, 1) if seen else None,
                "polls": polls})
            rows[-1]["front_kept_focus"] = (
                win32gui.GetForegroundWindow() == front)
            print(f"  {w}x{h} #{k}: getdata={rows[-1]['getdata_ms']} "
                  f"visible={rows[-1]['visible_ms']} ms", file=sys.stderr, flush=True)
            if background:
                e2.background_commit(hwnd)  # posted `S` to the input site (E2)
            else:
                send_keys(*KEY_S)  # commit (E14); needs Paint focused
            time.sleep(pause)
    finally:
        if standin:
            standin.close()
        screen.close()
        core.close(hwnd, discard=True)
    timed = [r for r in rows if not r["warmup"]]
    vis = [r["visible_ms"] for r in timed if r["visible_ms"] is not None]
    get = [r["getdata_ms"] for r in timed if r["getdata_ms"] is not None]
    return {"size": [w, h], "megapixels": round(w * h / 1e6, 2),
            "dib_mb": round(len(slides[0]) / 1e6, 1),
            "trigger": trigger, "background": background,
            "front_kept_focus": all(r["front_kept_focus"] for r in timed),
            "pastes": len(timed), "missed": len(timed) - len(vis),
            "visible_ms": summary(vis), "getdata_ms": summary(get),
            "rows": rows}


def main() -> int:
    p = argparse.ArgumentParser(description=__doc__,
                                formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--mode", choices=("ole", "plain"), default="ole")
    p.add_argument("--monitor", default=r"\\.\DISPLAY2")
    p.add_argument("--size", action="append",
                   help="WxH, repeatable (default: 1214x475 1854x875 2446x1163)")
    p.add_argument("-n", type=int, default=20, help="timed pastes per size")
    p.add_argument("--warmup", type=int, default=2)
    p.add_argument("--timeout", type=float, default=5.0)
    p.add_argument("--pause", type=float, default=0.3,
                   help="seconds after each commit before the next slide")
    p.add_argument("--trigger", choices=("ctrl-v", "wm-command"), default="ctrl-v",
                   help="ctrl-v: SendInput; wm-command: posted Edit > Paste (E2)")
    p.add_argument("--background", action="store_true",
                   help="keep another window in front of Paint (E2)")
    a = p.parse_args()
    sizes = [tuple(int(v) for v in s.lower().split("x"))
             for s in (a.size or ["1214x475", "1854x875", "2446x1163"])]
    mon = next(m for m in e11.monitors() if m["device"] == a.monitor)
    clip = OleClipboard() if a.mode == "ole" else PlainClipboard()
    try:
        results = [measure(mon, w, h, a.n, a.warmup, clip, a.timeout, a.pause,
                           a.trigger, a.background)
                   for w, h in sizes]
    finally:
        clip.close()
    json.dump({"mode": a.mode, "monitor": a.monitor,
               "scale_percent": round(mon["dpi"] * 100 / 96), "results": results},
              sys.stdout, indent=2)
    print()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
