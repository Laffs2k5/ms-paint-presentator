"""E15: can a script draw in Paint, live, with Paint's own tools?

For the loading painting (see the plan's presenter design), the presenter
draws with Paint's tools through synthetic input. Three input routes:

- mouse: `SendInput` with absolute coordinates. Moves the real cursor.
- pen: a synthetic pen (`CreateSyntheticPointerDevice(PT_PEN)` and
  `InjectSyntheticPointerInput`), with pressure.
- posted: WM_MOUSEMOVE / WM_LBUTTONDOWN / WM_LBUTTONUP posted to Paint's
  `InputSiteWindowClass` window, where a posted `S` works (E2).

Each test draws one figure in its own 250 x 250 cell of a blank canvas, with
the Pencil (key P), Paint maximised at 100% zoom:

- circle: 350 degrees of a circle traced in 4 px steps, at a given interval
  between points. A screen grab is compared with the ideal path: coverage
  (path pixels with ink within 1 px), the largest gap, stray ink more than
  2 px off the path (dropped points turn arcs into chords), and the best
  offset between intended and drawn (coordinate mapping).
- live: one circle per route pauses half way with the button still down and
  grabs the screen, to see whether the stroke shows while it is drawn.
- dashes: 12 short strokes in quick succession: does each one land?
- background: posted messages and the pen, with a stand-in window in front
  (E2): does it draw, and who is in front afterwards?
- pressure: the pen at light and hard pressure with five brushes: does the
  width follow? (Watercolor and Oil brush do; Brush, Calligraphy pen and
  Natural pencil do not.)

Pen frames must be spaced: injected back to back with no wait, a stroke fails
part-way with ERROR_INVALID_PARAMETER (87) in most tries ("pen circle 0 ms");
1 ms apart it never failed.

Run from poc/paintctl, so paintctl's venv is used:

    uv run python ../experiments/e15_draw.py [--monitor \\\\.\\DISPLAY1] [--tests circle,dashes,...]

Hands off the mouse and keyboard while it runs (about two minutes): the mouse
route moves the cursor and clicks. Prints JSON; progress on stderr. Leaves the
Brush selected, as Paint starts (E4). The Brush is picked from the Brushes
button's menu: SelectionItem.Select on the button does not switch tools.
"""
from __future__ import annotations

import argparse
import ctypes
import json
import math
import sys
import time
from ctypes import wintypes

import e2_background_paste as e2  # stand-in window, input site
import e6_paste_speed as e6  # typed user32, SendInput structures, send_keys
import e11_display_area as e11  # also sets DPI awareness
import win32con
import win32gui
from paintctl import core
from PIL import ImageGrab
from pywinauto.findwindows import ElementAmbiguousError, ElementNotFoundError
from pywinauto.timings import TimeoutError as WaitTimeoutError

CELL = 250
CANVAS = (2000, 1000)  # 8 x 4 cells
_u32 = e6._user32

# --- mouse: SendInput, absolute ---------------------------------------------------------
_MOVE, _LDOWN, _LUP, _ABS, _VDESK = 0x0001, 0x0002, 0x0004, 0x8000, 0x4000


def _virtual_screen() -> tuple[int, int, int, int]:
    gsm = ctypes.windll.user32.GetSystemMetrics
    return gsm(76), gsm(77), gsm(78), gsm(79)  # x, y, width, height


def _cursor() -> tuple[int, int]:
    p = wintypes.POINT()
    ctypes.windll.user32.GetCursorPos(ctypes.byref(p))
    return p.x, p.y


class Mouse:
    name = "mouse"

    def __init__(self):
        self.vx, self.vy, self.vw, self.vh = _virtual_screen()

    def _abs(self, x: int, y: int) -> tuple[int, int]:
        # ceil(x * 65536 / w) maps back to exactly x when Windows floors
        return (((x - self.vx) * 65536 + self.vw - 1) // self.vw,
                ((y - self.vy) * 65536 + self.vh - 1) // self.vh)

    def _event(self, x: int, y: int, flags: int) -> e6._INPUT:
        ev = e6._INPUT()
        ev.type = 0  # INPUT_MOUSE
        nx, ny = self._abs(x, y)
        ev.u.mi = e6._MOUSEINPUT(nx, ny, 0, flags | _MOVE | _ABS | _VDESK, 0, 0)
        return ev

    def send(self, events: list[e6._INPUT]) -> None:
        arr = (e6._INPUT * len(events))(*events)
        if _u32.SendInput(len(events), arr, ctypes.sizeof(e6._INPUT)) != len(events):
            raise ctypes.WinError(ctypes.get_last_error())

    def down(self, x, y):
        self.send([self._event(x, y, 0), self._event(x, y, _LDOWN)])

    def move(self, x, y):
        self.send([self._event(x, y, 0)])

    def up(self, x, y):
        self.send([self._event(x, y, _LUP)])

    def batch(self, pts):
        """The whole stroke as one SendInput call: no time between points."""
        evs = [self._event(*pts[0], 0), self._event(*pts[0], _LDOWN)]
        evs += [self._event(x, y, 0) for x, y in pts[1:]]
        evs.append(self._event(*pts[-1], _LUP))
        self.send(evs)


# --- pen: synthetic pointer injection ---------------------------------------------------
class POINTER_INFO(ctypes.Structure):
    _fields_ = (("pointerType", wintypes.DWORD), ("pointerId", ctypes.c_uint32),
                ("frameId", ctypes.c_uint32), ("pointerFlags", ctypes.c_uint32),
                ("sourceDevice", wintypes.HANDLE), ("hwndTarget", wintypes.HWND),
                ("ptPixelLocation", wintypes.POINT), ("ptHimetricLocation", wintypes.POINT),
                ("ptPixelLocationRaw", wintypes.POINT),
                ("ptHimetricLocationRaw", wintypes.POINT),
                ("dwTime", wintypes.DWORD), ("historyCount", ctypes.c_uint32),
                ("InputData", ctypes.c_int32), ("dwKeyStates", wintypes.DWORD),
                ("PerformanceCount", ctypes.c_uint64), ("ButtonChangeType", ctypes.c_int))


class POINTER_PEN_INFO(ctypes.Structure):
    _fields_ = (("pointerInfo", POINTER_INFO), ("penFlags", ctypes.c_uint32),
                ("penMask", ctypes.c_uint32), ("pressure", ctypes.c_uint32),
                ("rotation", ctypes.c_uint32), ("tiltX", ctypes.c_int32),
                ("tiltY", ctypes.c_int32))


class POINTER_TOUCH_INFO(ctypes.Structure):
    _fields_ = (("pointerInfo", POINTER_INFO), ("touchFlags", ctypes.c_uint32),
                ("touchMask", ctypes.c_uint32), ("rcContact", wintypes.RECT),
                ("rcContactRaw", wintypes.RECT), ("orientation", ctypes.c_uint32),
                ("pressure", ctypes.c_uint32))


class _PTI(ctypes.Union):
    _fields_ = (("touchInfo", POINTER_TOUCH_INFO), ("penInfo", POINTER_PEN_INFO))


class POINTER_TYPE_INFO(ctypes.Structure):
    _fields_ = (("type", wintypes.DWORD), ("u", _PTI))


PT_PEN = 3
F_INRANGE, F_INCONTACT, F_FIRSTBUTTON = 0x2, 0x4, 0x10
F_DOWN, F_UPDATE, F_UP = 0x10000, 0x20000, 0x40000
PEN_MASK_PRESSURE = 0x1
_u32.CreateSyntheticPointerDevice.restype = wintypes.HANDLE
_u32.CreateSyntheticPointerDevice.argtypes = (wintypes.DWORD, wintypes.ULONG, wintypes.DWORD)
_u32.InjectSyntheticPointerInput.restype = wintypes.BOOL
_u32.InjectSyntheticPointerInput.argtypes = (wintypes.HANDLE, ctypes.POINTER(POINTER_TYPE_INFO),
                                             ctypes.c_uint32)
_u32.DestroySyntheticPointerDevice.argtypes = (wintypes.HANDLE,)


class Pen:
    name = "pen"

    def __init__(self):
        self.dev = _u32.CreateSyntheticPointerDevice(PT_PEN, 1, 3)  # POINTER_FEEDBACK_NONE
        if not self.dev:
            raise ctypes.WinError(ctypes.get_last_error())
        self.vx, self.vy, _, _ = _virtual_screen()
        self.pressure = 512

    def _inject(self, x: int, y: int, flags: int, button_change: int = 0) -> None:
        pti = POINTER_TYPE_INFO()
        pti.type = PT_PEN
        pen = pti.u.penInfo
        pen.pointerInfo.pointerType = PT_PEN
        pen.pointerInfo.pointerId = 0
        pen.pointerInfo.pointerFlags = flags
        pen.pointerInfo.ptPixelLocation = wintypes.POINT(x - self.vx, y - self.vy)
        pen.pointerInfo.ButtonChangeType = button_change
        pen.penMask = PEN_MASK_PRESSURE
        pen.pressure = self.pressure if flags & F_INCONTACT else 0
        if not _u32.InjectSyntheticPointerInput(self.dev, ctypes.byref(pti), 1):
            raise ctypes.WinError(ctypes.get_last_error())

    def down(self, x, y):
        self._inject(x, y, F_INRANGE | F_UPDATE)  # hover in first
        self._inject(x, y, F_INRANGE | F_INCONTACT | F_FIRSTBUTTON | F_DOWN, 1)

    def move(self, x, y):
        self._inject(x, y, F_INRANGE | F_INCONTACT | F_FIRSTBUTTON | F_UPDATE)

    def up(self, x, y):
        self._inject(x, y, F_INRANGE | F_UP, 2)
        self._inject(x, y, F_UPDATE)  # out of range

    def close(self):
        _u32.DestroySyntheticPointerDevice(self.dev)


# --- posted mouse messages to the input site ----------------------------------------------
class Posted:
    name = "posted"

    def __init__(self, paint: int):
        self.hwnd = e2.input_site(paint)

    def _post(self, msg: int, x: int, y: int, wparam: int) -> None:
        cx, cy = win32gui.ScreenToClient(self.hwnd, (x, y))
        win32gui.PostMessage(self.hwnd, msg, wparam, (cy & 0xFFFF) << 16 | (cx & 0xFFFF))

    def down(self, x, y):
        self._post(win32con.WM_MOUSEMOVE, x, y, 0)
        self._post(win32con.WM_LBUTTONDOWN, x, y, win32con.MK_LBUTTON)

    def move(self, x, y):
        self._post(win32con.WM_MOUSEMOVE, x, y, win32con.MK_LBUTTON)

    def up(self, x, y):
        self._post(win32con.WM_LBUTTONUP, x, y, 0)


# --- figures and analysis ---------------------------------------------------------------------
def circle(cx: int, cy: int, r: int = 95, step: float = 4.0, degrees: float = 350):
    n = max(2, int(2 * math.pi * r * degrees / 360 / step))
    return [(round(cx + r * math.cos(math.radians(degrees * i / n))),
             round(cy + r * math.sin(math.radians(degrees * i / n)))) for i in range(n + 1)]


def raster(pts) -> set[tuple[int, int]]:
    """The ideal polyline through pts, one pixel per step."""
    out = set()
    for (x0, y0), (x1, y1) in zip(pts, pts[1:]):
        n = max(abs(x1 - x0), abs(y1 - y0), 1)
        for i in range(n + 1):
            out.add((round(x0 + (x1 - x0) * i / n), round(y0 + (y1 - y0) * i / n)))
    return out


def grab(box: tuple[int, int, int, int]) -> tuple[set, tuple[int, int]]:
    img = ImageGrab.grab(bbox=box, all_screens=True).convert("L")
    w, h = img.size
    data = img.tobytes()
    return {(box[0] + i % w, box[1] + i // w) for i, v in enumerate(data) if v < 128}, (w, h)


def _dist(sources: set, cap: int = 20) -> dict:
    """Chebyshev distance from the nearest source, up to cap."""
    dist = {s: 0 for s in sources}
    frontier = list(sources)
    for d in range(1, cap + 1):
        nxt = []
        for x, y in frontier:
            for dx in (-1, 0, 1):
                for dy in (-1, 0, 1):
                    q = (x + dx, y + dy)
                    if q not in dist:
                        dist[q] = d
                        nxt.append(q)
        frontier = nxt
    return dist


def analyse(ink: set, ideal: set, cap: int = 20) -> dict:
    if not ink:
        return {"ink_px": 0, "coverage": 0.0}
    near_ink = _dist(ink, cap)
    best = None
    for dy in range(-4, 5):
        for dx in range(-4, 5):
            hit = sum(1 for x, y in ideal if near_ink.get((x + dx, y + dy), 99) <= 1)
            key = (hit, -(abs(dx) + abs(dy)))
            if best is None or key > best[0]:
                best = (key, dx, dy)
    _, dx, dy = best
    shifted = {(x + dx, y + dy) for x, y in ideal}
    gaps = [near_ink.get(p, cap + 1) for p in shifted]
    near_path = _dist(shifted, cap)
    stray = [near_path.get(p, cap + 1) for p in ink]
    return {"ink_px": len(ink),
            "coverage": round(sum(1 for g in gaps if g <= 1) / len(gaps), 3),
            "max_gap_px": max(gaps),
            "stray_share": round(sum(1 for s in stray if s > 2) / len(stray), 3),
            "max_stray_px": max(stray),
            "width_px": round(len(ink) / len(ideal), 2),
            "offset": [dx, dy]}


# --- running strokes --------------------------------------------------------------------------
def _wait_until(t: float) -> None:
    while True:
        left = t - time.perf_counter()
        if left <= 0:
            return
        time.sleep(left if left > 0.002 else 0)


def stroke(route, pts, interval_ms: float, live_box=None) -> dict:
    """Draw pts as one stroke. With live_box, pause half way with the button
    down and report how much of the first half already shows."""
    out = {}
    t0 = time.perf_counter()
    if interval_ms == 0 and hasattr(route, "batch"):
        route.batch(pts)
    else:
        route.down(*pts[0])
        half = len(pts) // 2
        for i, p in enumerate(pts[1:], 1):
            _wait_until(t0 + i * interval_ms / 1000)
            route.move(*p)
            if live_box and i == half:
                time.sleep(0.3)
                ink, _ = grab(live_box)
                first = raster(pts[:half + 1])
                out["live_first_half"] = analyse(ink, first)["coverage"]
                t0 += time.perf_counter() - t0 - i * interval_ms / 1000
        route.up(*pts[-1])
    took = time.perf_counter() - t0
    out["points"] = len(pts)
    out["seconds"] = round(took, 3)
    out["px_per_s"] = round(len(raster(pts)) / took) if took > 0 else None
    return out


class Board:
    """Cells on the canvas, one per test."""

    def __init__(self, paint: int):
        c = core.info(paint)["canvas_on_screen"]
        self.left, self.top = c["left"], c["top"]
        self.cols = CANVAS[0] // CELL
        self.next = 0

    def cell(self) -> tuple[int, int, tuple[int, int, int, int]]:
        k = self.next
        self.next += 1
        x0 = self.left + (k % self.cols) * CELL
        y0 = self.top + (k // self.cols) * CELL
        return x0 + CELL // 2, y0 + CELL // 2, (x0 + 5, y0 + 5, x0 + CELL - 5, y0 + CELL - 5)


def circle_test(board, route, interval_ms, live=False) -> dict:
    cx, cy, box = board.cell()
    pts = circle(cx, cy)
    res = stroke(route, pts, interval_ms, live_box=box if live else None)
    time.sleep(0.5)
    ink, _ = grab(box)
    res.update(analyse(ink, raster(pts)))
    return res


def dashes_test(board, route, gap_ms) -> dict:
    cx, cy, box = board.cell()
    drawn, starts = 0, []
    t0 = time.perf_counter()
    for row in range(4):
        for col in range(3):
            x = box[0] + 20 + col * 75
            y = box[1] + 30 + row * 55
            pts = [(x, y), (x + 15, y + 4), (x + 30, y)]
            starts.append(pts)
            route.down(*pts[0])
            route.move(*pts[1])
            route.move(*pts[2])
            route.up(*pts[2])
            if gap_ms:
                time.sleep(gap_ms / 1000)
    took = time.perf_counter() - t0
    time.sleep(0.5)
    ink, _ = grab(box)
    for pts in starts:
        if analyse(ink, raster(pts))["coverage"] >= 0.5:
            drawn += 1
    return {"dashes": len(starts), "drawn": drawn, "seconds": round(took, 3)}


PRESSURE_BRUSHES = ("Brush", "Calligraphy pen", "Natural pencil", "Watercolor brush", "Oil brush")


def pressure_test(board, pen, paint) -> dict:
    """Two pen strokes per brush, light (50) and hard (1000): ink rows across
    the middle of each stroke, i.e. its width."""
    out = {}
    for brush in PRESSURE_BRUSHES:
        tool = select_brush(paint, brush)
        cx, cy, box = board.cell()
        rows = {}
        for i, p in enumerate((50, 1000)):
            y = box[1] + 50 + i * 100
            pen.pressure = p
            stroke(pen, [(box[0] + 20 + 10 * k, y) for k in range(21)], 8)
            time.sleep(0.4)
            img = ImageGrab.grab(bbox=(box[0] + 60, y - 30, box[0] + 180, y + 31),
                                 all_screens=True).convert("L")
            w = img.size[0]
            rows[p] = len({i // w for i, v in enumerate(img.tobytes()) if v < 250})
        out[brush] = {"tool": tool, "width_px_light": rows[50], "width_px_hard": rows[1000]}
    pen.pressure = 512
    return out


def mouse_brush_width(board, mouse) -> dict:
    cx, cy, box = board.cell()
    y = box[1] + 40
    stroke(mouse, [(box[0] + 20 + 10 * k, y) for k in range(21)], 8)
    time.sleep(0.4)
    ink, _ = grab((cx - 1, y - 30, cx + 2, y + 31))
    return {"width_px": len({py for _, py in ink})}


def active_tool(paint: int) -> str:
    """The canvas is named "Using <tool> tool on Canvas". Right after a tool
    change two such elements can exist for a moment, so retry, then take the
    first."""
    spec = core._uia_window(paint).child_window(auto_id="image", title_re="Using .* on Canvas")
    for _ in range(10):
        try:
            return spec.wrapper_object().element_info.name
        except ElementAmbiguousError:
            time.sleep(0.1)
    first = core._uia_window(paint).child_window(
        auto_id="image", title_re="Using .* on Canvas", found_index=0)
    return first.wrapper_object().element_info.name


def select_brush(paint: int, brush: str = "Brush") -> str:
    """Select a brush. SelectionItem.Select on the Brushes button does not switch
    tools; picking from its menu does. Right after Paint starts the menu may not
    open on the first try (E13), so expand again before giving up."""
    win = core._uia_window(paint)
    for attempt in range(3):
        win.child_window(auto_id="BrushesSplitButton").wrapper_object().expand()
        time.sleep(0.5 + 0.5 * attempt)
        try:
            win.child_window(title=brush, control_type="MenuItem").wait(
                "exists", timeout=2).invoke()
            break
        except (ElementNotFoundError, WaitTimeoutError):
            if attempt == 2:
                raise
    time.sleep(0.4)
    return active_tool(paint)


# --- main ---------------------------------------------------------------------------------------
TESTS = ("circle", "live", "dashes", "posted", "background", "pressure")


def main() -> int:
    p = argparse.ArgumentParser(description=__doc__,
                                formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--monitor", default=r"\\.\DISPLAY1")
    p.add_argument("--tests", default=",".join(TESTS))
    a = p.parse_args()
    tests = a.tests.split(",")
    mon = next(m for m in e11.monitors() if m["device"] == a.monitor)
    cursor_home = _cursor()
    out: dict = {"monitor": {"device": mon["device"], "dpi": mon["dpi"]}, "canvas": CANVAS}
    paint = pen = standin = None

    def log(name, fn):
        try:
            res = fn()
        except Exception as exc:  # noqa: BLE001 - record and go on with the next test
            res = {"error": f"{type(exc).__name__}: {exc}"}
        out[name] = res
        print(f"{name}: {json.dumps(res)}", file=sys.stderr, flush=True)

    try:
        paint = e11.open_maximised(mon, *CANVAS)
        board = Board(paint)
        mouse = Mouse()
        try:
            pen = Pen()
        except OSError as exc:
            out["pen_error"] = str(exc)
        core.focus(paint)
        e6.send_keys((ord("P"), False), (ord("P"), True))  # Pencil
        time.sleep(0.4)
        pencil = core._uia_window(paint).child_window(auto_id="PencilTool").wrapper_object()
        out["pencil_selected"] = pencil.iface_toggle.CurrentToggleState == 1

        if "circle" in tests:
            for ms in (0, 1, 4, 8, 16):
                log(f"mouse circle {ms} ms", lambda: circle_test(board, mouse, ms))
            if pen:
                before = _cursor()
                for ms in (0, 1, 4, 8, 16):
                    log(f"pen circle {ms} ms", lambda: circle_test(board, pen, ms))
                out["pen moved the cursor"] = _cursor() != before
        if "live" in tests:
            log("mouse live 8 ms", lambda: circle_test(board, mouse, 8, live=True))
            if pen:
                log("pen live 8 ms", lambda: circle_test(board, pen, 8, live=True))
        if "dashes" in tests:
            for ms in (0, 10):
                log(f"mouse dashes gap {ms} ms", lambda: dashes_test(board, mouse, ms))
                if pen:
                    log(f"pen dashes gap {ms} ms", lambda: dashes_test(board, pen, ms))
        if "posted" in tests:
            core.focus(paint)
            log("posted, Paint in front", lambda: circle_test(board, Posted(paint), 4))
        if "background" in tests:
            w = mon["work"]
            standin = e2.StandIn(w.left + 200, w.top + 20, 900, 160)
            for route in [Posted(paint)] + ([pen] if pen else []):
                core.focus(standin.hwnd)
                time.sleep(0.3)

                def bg(route=route):
                    res = circle_test(board, route, 8)
                    fg = win32gui.GetForegroundWindow()
                    res["in_front_after"] = ("stand-in" if fg == standin.hwnd else
                                             "Paint" if fg == paint else hex(fg))
                    return res
                log(f"{route.name}, stand-in in front", bg)
            standin.close()
            standin = None
            core.focus(paint)
        if "pressure" in tests:
            if pen:
                log("pen pressure", lambda: pressure_test(board, pen, paint))
            out["tool for mouse width"] = select_brush(paint)
            log("mouse, Brush", lambda: mouse_brush_width(board, mouse))
    finally:
        if pen:
            pen.close()
        if standin:
            standin.close()
        ctypes.windll.user32.SetCursorPos(*cursor_home)
        if paint:
            if "pressure" not in tests:  # leave the Brush selected, as Paint starts
                try:
                    select_brush(paint)
                except Exception:  # noqa: BLE001
                    pass
            core.close(paint, discard=True)
    json.dump(out, sys.stdout, indent=2)
    print()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
