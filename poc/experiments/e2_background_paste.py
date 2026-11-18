"""E2/E3: can a slide be pasted into Paint while another window has focus,
without keystrokes?

A stand-in "presenter view" window of our own takes the foreground (over
Paint's toolbar, clear of the canvas). Then each candidate tries to make Paint
paste, with no SendInput: posted window messages, and UI Automation. A
candidate passes when Paint reads the live OLE clipboard (GetData), the new
slide shows on screen, and the stand-in window is still in the foreground.

If a paste lands, it is then committed in the background too: `S` (E14) posted
as WM_KEYDOWN/WM_KEYUP to Paint's `InputSiteWindowClass` child, the WinUI
island's input window. Posted to the main window, MSPaintView or the
DesktopChildSiteBridge it does nothing, and UIA (invoking Crop, selecting a
tool) activates Paint. Checked by comparing a screen grab with the slide.

Run from poc/paintctl, so paintctl's venv is used:

    uv run python ../experiments/e2_background_paste.py [--monitor \\\\.\\DISPLAY1] [--size 2716x1434]

Hands off while it runs (about a minute). Prints JSON; progress on stderr.
"""
from __future__ import annotations

import argparse
import ctypes
import json
import sys
import threading
import time

import e6_paste_speed as e6  # same folder: slides, probes, OLE clipboard
import e11_display_area as e11
import win32con
import win32gui
from paintctl import core
from PIL import ImageGrab

WM_PASTE = 0x0302
ID_EDIT_PASTE = 0xE125  # MFC's standard Edit > Paste command ID (afxres.h)


class StandIn:
    """A plain top-level window standing in for the presenter view."""

    def __init__(self, x: int, y: int, w: int, h: int):
        self.hwnd = 0
        ready = threading.Event()

        def run():
            wc = win32gui.WNDCLASS()
            wc.lpszClassName = "PaintPresenterStandIn"
            wc.hbrBackground = win32con.COLOR_WINDOW + 1
            wc.lpfnWndProc = {win32con.WM_DESTROY:
                              lambda *a: win32gui.PostQuitMessage(0) or 0}
            try:
                win32gui.RegisterClass(wc)
            except win32gui.error as exc:
                if exc.winerror != 1410:  # class already exists: reuse it
                    raise
            self.hwnd = win32gui.CreateWindowEx(
                0, wc.lpszClassName, "E2 presenter stand-in",
                win32con.WS_OVERLAPPEDWINDOW | win32con.WS_VISIBLE,
                x, y, w, h, 0, 0, 0, None)
            ready.set()
            win32gui.PumpMessages()

        threading.Thread(target=run, daemon=True, name="standin").start()
        if not ready.wait(5) or not self.hwnd:
            raise RuntimeError("stand-in window did not start")

    def close(self) -> None:
        win32gui.PostMessage(self.hwnd, win32con.WM_CLOSE, 0, 0)


def _menu_paste(paint: int, view: int) -> None:
    """DoomPaint's fallback: Edit > Paste through UIA (opens the menu)."""
    win = core._uia_window(paint)
    win.child_window(title="Edit", control_type="MenuItem").wrapper_object().expand()
    time.sleep(0.3)  # the Paste item is not there straight after expand (step 1)
    win.child_window(title="Paste", control_type="MenuItem").wrapper_object().invoke()


CANDIDATES = {
    "post WM_COMMAND ID_EDIT_PASTE to main window":
        lambda p, v: win32gui.PostMessage(p, win32con.WM_COMMAND, ID_EDIT_PASTE, 0),
    "post WM_COMMAND ID_EDIT_PASTE to MSPaintView":
        lambda p, v: win32gui.PostMessage(v, win32con.WM_COMMAND, ID_EDIT_PASTE, 0),
    "send WM_COMMAND ID_EDIT_PASTE to main window":
        lambda p, v: win32gui.SendMessageTimeout(
            p, win32con.WM_COMMAND, ID_EDIT_PASTE, 0, win32con.SMTO_ABORTIFHUNG, 2000),
    "post WM_PASTE to main window":
        lambda p, v: win32gui.PostMessage(p, WM_PASTE, 0, 0),
    "post WM_PASTE to MSPaintView":
        lambda p, v: win32gui.PostMessage(v, WM_PASTE, 0, 0),
    "post WM_CHAR Ctrl+V (0x16) to MSPaintView":
        lambda p, v: win32gui.PostMessage(v, win32con.WM_CHAR, 0x16, 0),
    "UIA Edit > Paste menu": _menu_paste,
}


def input_site(paint: int) -> int:
    """Paint's WinUI input window, which takes its keyboard input."""
    return core._find_child_by_class(paint, "InputSiteWindowClass") or 0


def post_key(hwnd: int, vk: int) -> None:
    scan = ctypes.windll.user32.MapVirtualKeyW(vk, 0)
    win32gui.PostMessage(hwnd, win32con.WM_KEYDOWN, vk, 1 | (scan << 16))
    win32gui.PostMessage(hwnd, win32con.WM_KEYUP, vk,
                         1 | (scan << 16) | (1 << 30) | (1 << 31))


def background_commit(paint: int) -> None:
    """Commit a floating paste without focus: `S` to the input site (E14)."""
    post_key(input_site(paint), ord("S"))


def main() -> int:
    p = argparse.ArgumentParser(description=__doc__,
                                formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--monitor", default=r"\\.\DISPLAY1")
    p.add_argument("--size", default="2716x1434")
    p.add_argument("--wait", type=float, default=2.0, help="seconds per candidate")
    a = p.parse_args()
    w, h = (int(v) for v in a.size.lower().split("x"))
    mon = next(m for m in e11.monitors() if m["device"] == a.monitor)
    base = e11.test_pattern(w, h)
    slides = [e6.dib(e6.slide_image(base, k)) for k in range(len(CANDIDATES))]
    clip = e6.OleClipboard()
    screen = e6.ScreenProbe()
    paint = e11.open_maximised(mon, w, h)
    view = core._find_child_by_class(paint, "MSPaintView")
    info = core.info(paint)
    vp, c = info["viewport"], info["canvas_on_screen"]
    P = e11._PAD
    box = (c["left"] - P, c["top"] - P, c["left"] + w + P, c["top"] + h + P)
    probe = (c["left"] + e6.probe_points(w, h)[0][0] + e6._PROBE // 2,
             c["top"] + e6.probe_points(w, h)[0][1] + e6._PROBE // 2)
    # over Paint's title bar and toolbar, clear of the canvas
    standin = StandIn(mon["work"].left + 200, mon["work"].top + 20,
                      900, 160)  # short: its shadow must not reach the canvas
    results = {}
    try:
        for k, (name, fn) in enumerate(CANDIDATES.items()):
            clip.offer(slides[k])
            core.focus(standin.hwnd)
            time.sleep(0.3)
            fg_before = win32gui.GetForegroundWindow() == standin.hwnd
            t0 = time.perf_counter()
            error = None
            try:
                fn(paint, view)
            except Exception as exc:  # noqa: BLE001 - record, go on
                error = f"{type(exc).__name__}: {exc}"
            seen = None
            while time.perf_counter() - t0 < a.wait:
                if screen.pixel(*probe) == e6.colour(k):
                    seen = time.perf_counter()
                    break
            get = clip.getdata_at
            row = {
                "standin_foreground_before": fg_before,
                "getdata_ms": round((get - t0) * 1000, 1) if get else None,
                "visible_ms": round((seen - t0) * 1000, 1) if seen else None,
                "standin_foreground_after":
                    win32gui.GetForegroundWindow() == standin.hwnd,
                "error": error,
            }
            row["pass"] = bool(seen and get and fg_before
                               and row["standin_foreground_after"])
            if seen:  # a floating selection exists: commit it in the background
                time.sleep(0.5)
                background_commit(paint)
                time.sleep(1.0)
                grab = ImageGrab.grab(bbox=box, all_screens=True)
                cmp = e11.compare(grab, e6.slide_image(base, k))
                row["background_commit"] = {
                    "pixel_identical": cmp["identical"],
                    "diff_px": cmp["diff_pixels"],
                    "standin_still_foreground":
                        win32gui.GetForegroundWindow() == standin.hwnd}
                row["pass"] = row["pass"] and cmp["identical"] and \
                    row["background_commit"]["standin_still_foreground"]
            results[name] = row
            print(f"{name}: {json.dumps(row)}", file=sys.stderr, flush=True)
    finally:
        standin.close()
        screen.close()
        clip.close()
        core.close(paint, discard=True)
    json.dump({"monitor": a.monitor, "size": [w, h], "candidates": results},
              sys.stdout, indent=2)
    print()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
