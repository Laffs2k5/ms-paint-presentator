"""E17: where does a paste smaller than the canvas land, and can it be moved?

For the loading painting's fallback (and handy anyway): stamping ready-made
pieces at a chosen spot. A stamp is a solid-colour PNG offered as a new OLE
clipboard object (E1, E7) and pasted with Ctrl+V, Paint maximised and in
front, canvas 2000 x 1000 at 100% (it fits the viewport, so nothing scrolls).
Positions are found on screen by the stamp's colour and given in canvas pixels,
measured from the canvas's white area found before any paste.

- landing: where a stamp lands on a blank canvas, and with a selection
  rectangle made first (Select tool, pen drag).
- keys: arrow keys (plain, Shift, Ctrl) on the floating paste: how far each
  moves it.
- drag: a pen drag from inside the floating paste: does it land exactly where
  dragged?
- alpha: a stamp with transparent corners dragged over a coloured background
  and committed: do the corners stay see-through, and is nothing left behind?
- cost: Ctrl+V to on screen for stamps from 100 x 100 to the full canvas.

Each paste is committed with `S` (E14). Run from poc/paintctl:

    uv run python ../experiments/e17_place_paste.py [--monitor \\\\.\\DISPLAY1]

Hands off the mouse and keyboard while it runs (about a minute). Prints JSON;
progress on stderr.
"""
from __future__ import annotations

import argparse
import json
import statistics
import struct
import sys
import time

import e1_formats as e1  # PNG encoder, OLE clipboard
import e6_paste_speed as e6  # send_keys, ScreenProbe, colour()
import e8_clipboard_history as e8  # history opt-out format
import e11_display_area as e11  # also sets DPI awareness
import e15_draw as e15  # synthetic pen
from paintctl import core
from PIL import Image, ImageChops, ImageDraw, ImageGrab

CANVAS = (2000, 1000)
_PAD = 6
VK = {"right": 0x27, "down": 0x28, "shift": 0x10, "ctrl": 0x11}


class Stage:
    """Paint, the clipboard, the pen and the canvas's exact place on screen."""

    def __init__(self, mon: dict):
        self.paint = e11.open_maximised(mon, *CANVAS)
        core.focus(self.paint)
        e6.send_keys(*e6.KEY_S)  # Select tool: no brush panel over the canvas
        time.sleep(0.4)
        c = core.info(self.paint)["canvas_on_screen"]
        box = (c["left"] - _PAD, c["top"] - _PAD,
               c["left"] + c["width"] + _PAD, c["top"] + c["height"] + _PAD)
        # The canvas is the long white run across a row and down a column at a
        # quarter of the way in: the resize handles (white squares on the
        # corners and edge midpoints) would widen a plain white bounding box.
        white = self._mask(ImageGrab.grab(bbox=box, all_screens=True), (255, 255, 255))
        w, h = white.size
        px = white.load()
        left, right = _longest_run([px[x, h // 4] for x in range(w)])
        top, bottom = _longest_run([px[w // 4, y] for y in range(h)])
        self.x0, self.y0 = box[0] + left, box[1] + top
        self.canvas_px = [right - left + 1, bottom - top + 1]
        self.clip = e1.MultiFormatClipboard()
        self.pen = e15.Pen()
        self.screen = e6.ScreenProbe()

    @staticmethod
    def _mask(img: Image.Image, rgb) -> Image.Image:
        solid = Image.new("RGB", img.size, rgb)
        return ImageChops.difference(img.convert("RGB"), solid).convert("L") \
            .point(lambda v: 255 if v == 0 else 0)

    def find(self, rgb) -> list[int] | None:
        """Bounding box of a colour on the canvas, in canvas pixels."""
        img = ImageGrab.grab(bbox=(self.x0, self.y0, self.x0 + CANVAS[0],
                                   self.y0 + CANVAS[1]), all_screens=True)
        return list(b) if (b := self._mask(img, rgb).getbbox()) else None

    def wait_for(self, rgb, timeout: float = 3.0) -> list[int] | None:
        end = time.perf_counter() + timeout
        while time.perf_counter() < end:
            if box := self.find(rgb):
                time.sleep(0.2)
                return self.find(rgb)
        return None

    def offer(self, img: Image.Image) -> None:
        self.clip.offer([(e1.CF_PNG, e1.enc_png(img)), (e8.EXCLUDE, struct.pack("<I", 0))])

    def paste(self, img: Image.Image) -> None:
        self.offer(img)
        time.sleep(0.2)
        e6.send_keys(*e6.CTRL_V)

    def commit(self) -> None:
        e6.send_keys(*e6.KEY_S)
        time.sleep(0.5)

    def at(self, x: int, y: int) -> tuple[int, int]:
        """Canvas pixel to screen pixel."""
        return self.x0 + x, self.y0 + y

    def drag(self, frm: tuple[int, int], to: tuple[int, int], steps: int = 20) -> None:
        (x0, y0), (x1, y1) = self.at(*frm), self.at(*to)
        pts = [(round(x0 + (x1 - x0) * k / steps), round(y0 + (y1 - y0) * k / steps))
               for k in range(steps + 1)]
        e15.stroke(self.pen, pts, 8)
        time.sleep(0.4)

    def close(self) -> None:
        self.clip.stop()
        self.pen.close()
        self.screen.close()
        core.close(self.paint, discard=True)


def _longest_run(values: list[int]) -> tuple[int, int]:
    """First and last index of the longest run of non-zero values."""
    best, start = (0, -1), None
    for i, v in enumerate(values + [0]):
        if v and start is None:
            start = i
        elif not v and start is not None:
            if i - start > best[1] - best[0] + 1:
                best = (start, i - 1)
            start = None
    return best


def solid(w: int, h: int, k: int) -> tuple[Image.Image, tuple[int, int, int]]:
    rgb = e6.colour(k)
    return Image.new("RGB", (w, h), rgb), rgb


# --- tests ----------------------------------------------------------------------------------
def test_landing(st: Stage) -> dict:
    out = {}
    img, rgb = solid(300, 200, 1)
    st.paste(img)
    out["blank canvas: floating"] = st.wait_for(rgb)
    st.commit()
    out["blank canvas: committed"] = st.find(rgb)
    # a selection first: Select tool, pen drag from (900, 500) to (1100, 650)
    st.drag((900, 500), (1100, 650))
    img, rgb = solid(300, 200, 2)
    st.paste(img)
    out["selection at 900,500 first: floating"] = st.wait_for(rgb)
    st.commit()
    out["selection at 900,500 first: committed"] = st.find(rgb)
    return out


def test_keys(st: Stage) -> dict:
    img, rgb = solid(200, 150, 3)
    st.paste(img)
    out = {"start": st.wait_for(rgb)}
    for label, mods, vk, n in (("Right x10", (), "right", 10), ("Down x10", (), "down", 10),
                               ("Shift+Right x3", ("shift",), "right", 3),
                               ("Ctrl+Right x3", ("ctrl",), "right", 3)):
        for _ in range(n):
            ev = [(VK[m], False) for m in mods] + [(VK[vk], False), (VK[vk], True)] + \
                 [(VK[m], True) for m in reversed(mods)]
            e6.send_keys(*ev)
            time.sleep(0.05)
        time.sleep(0.4)
        out[label] = st.find(rgb)
    st.commit()
    out["committed"] = st.find(rgb)
    return out


def test_drag(st: Stage) -> dict:
    img, rgb = solid(200, 150, 4)
    st.paste(img)
    start = st.wait_for(rgb)
    out = {"start": start}
    if not start:
        return out
    cx, cy = (start[0] + start[2]) // 2, (start[1] + start[3]) // 2
    st.drag((cx, cy), (cx + 1300, cy + 650))
    out["after drag by +1300,+650"] = st.find(rgb)
    st.commit()
    out["committed"] = st.find(rgb)
    out["want"] = [start[0] + 1300, start[1] + 650, start[2] + 1300, start[3] + 650]
    return out


def test_alpha(st: Stage) -> dict:
    bg, bg_rgb = solid(*CANVAS, 5)
    st.paste(bg)
    st.wait_for(bg_rgb)
    st.commit()
    red = (237, 28, 36)
    img = Image.new("RGBA", (200, 200), (0, 0, 0, 0))
    ImageDraw.Draw(img).ellipse((20, 20, 180, 180), fill=red + (255,))
    st.paste(img)
    start = st.wait_for(red)
    out = {"start": start}
    if not start:
        return out
    cx, cy = (start[0] + start[2]) // 2, (start[1] + start[3]) // 2
    st.drag((cx, cy), (cx + 800, cy + 400))
    st.commit()
    circle = st.find(red)
    out["committed circle"] = circle
    if circle:
        left, top = circle[0] - 20, circle[1] - 20  # the stamp's own corner
        out["stamp corner pixel"] = st.screen.pixel(*st.at(left + 3, top + 3))
        out["circle centre pixel"] = st.screen.pixel(*st.at(left + 100, top + 100))
    out["where it landed first, after the move"] = st.screen.pixel(*st.at(cx, cy))
    out["background"] = bg_rgb
    return out


def test_cost(st: Stage, n: int) -> dict:
    out = {}
    k = 10
    for w, h in ((100, 100), (400, 300), (1000, 600), CANVAS):
        times = []
        for _ in range(n):
            k += 1
            img, rgb = solid(w, h, k)
            st.offer(img)
            time.sleep(0.3)
            px = st.at(w // 2, h // 2)
            t0 = time.perf_counter()
            e6.send_keys(*e6.CTRL_V)
            while time.perf_counter() - t0 < 3:
                if st.screen.pixel(*px) == rgb:
                    times.append(round((time.perf_counter() - t0) * 1000, 1))
                    break
            time.sleep(0.2)
            st.commit()
        out[f"{w}x{h}"] = {"ms": times, "median": statistics.median(times) if times else None}
    return out


def main() -> int:
    p = argparse.ArgumentParser(description=__doc__,
                                formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--monitor", default=r"\\.\DISPLAY1")
    p.add_argument("--cost-n", type=int, default=5)
    a = p.parse_args()
    mon = next(m for m in e11.monitors() if m["device"] == a.monitor)
    out: dict = {}
    st = Stage(mon)
    out["canvas found"] = {"screen_origin": [st.x0, st.y0], "size": st.canvas_px}

    def log(name, fn):
        try:
            res = fn()
        except Exception as exc:  # noqa: BLE001 - record and go on
            res = {"error": f"{type(exc).__name__}: {exc}"}
        out[name] = res
        print(f"{name}: {json.dumps(res)}", file=sys.stderr, flush=True)

    try:
        for name, fn in (("landing", test_landing), ("keys", test_keys), ("drag", test_drag),
                         ("alpha", test_alpha)):
            log(name, lambda fn=fn: fn(st))
            st.close()
            st = Stage(mon)  # a fresh canvas for each test
        log("cost", lambda: test_cost(st, a.cost_n))
    finally:
        core.dismiss_clipboard_error(st.paint, 0.5)
        st.close()
    json.dump(out, sys.stdout, indent=2)
    print()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
