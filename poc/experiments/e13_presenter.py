"""E13: an end-to-end mini presenter, to judge how it feels.

The plan's presenter design, end to end, on one screen:

1. Predict Paint's viewport on the screen with E11's rule, open Paint once,
   maximised at 100%, with a canvas of the slide size for it: the largest
   reference shape (E12) that fits viewport - 66 px with viewport - slide even
   (E11). The viewport is then measured; if the prediction was off, Paint is
   reopened at the right size.
2. Render the deck (e13_deck.md, Marp) at that size in the background: a
   fixed design canvas at a scale factor, one row cropped or padded if the
   rounding is off (E12). Edge runs headless with a throwaway profile kept in
   the work folder. Rendered slides are cached per deck and size.
3. Meanwhile, draw the loading painting live in Paint with the synthetic pen
   (E15) and Paint's own tools (E16): an airbrushed sun, a progress-bar frame
   drawn with the Rectangle tool, the deck's title as wobbly hand-drawn
   letters (an installed font's outlines, taken at runtime, random every run),
   and the bar coloured in with the crayon as the slides really render. The
   painting stays until the first key.
4. The show: a keyboard hook takes the show keys while Paint is in front
   (E10). Each slide is a new PNG clipboard object (E1, E7, opted out of
   clipboard history, E8), pasted with Ctrl+V and committed with `S` (E14),
   both tagged as the presenter's own keys. Keys pressed while a slide is
   still pasting are combined, so a burst of Next jumps straight to the last.
   B / W black or white screen (again to come back), a number and Enter jumps,
   Esc ends and closes Paint without saving. The loading painting is slide 0:
   Left from slide 1, Home, or 0 + Enter go back to it. Every slide change is timed, key
   press to slide on screen.

Not in this version: the screen picker (Tk does not load in the ARM64 Python,
so it will be a small Win32 window; it needs two displays to test, see E18),
the presenter view and the cache's clean-up.

Needs the Node tools from E12 in --work. Run from poc/paintctl:

    uv run python ../experiments/e13_presenter.py --work <work>          # you drive it
    uv run python ../experiments/e13_presenter.py --work <work> --auto   # scripted keys

Prints JSON at the end; progress on stderr.
"""
from __future__ import annotations

import argparse
import ctypes
import hashlib
import io
import json
import math
import os
import random
import shutil
import statistics
import struct
import subprocess
import sys
import threading
import time
from ctypes import wintypes
from pathlib import Path

import e1_formats as e1  # OLE clipboard, PNG
import e6_paste_speed as e6  # ScreenProbe, key events
import e8_clipboard_history as e8  # history opt-out
import e10_keys as e10  # keyboard hook
import e11_display_area as e11  # monitors, Paint maximised (DPI awareness)
import e12_render as e12  # Edge, the reference shape, Marp's design canvas
import e15_draw as e15  # synthetic pen
import e16_tools as e16  # tools, colours, sizes
import e17_place_paste as e17  # finding the canvas on screen
import win32gui
from paintctl import core
from PIL import Image, ImageChops, ImageGrab

DECK = Path(__file__).with_name("e13_deck.md")
MARGIN = 66  # E11: viewport - slide that always fits


def log(msg: str) -> None:
    print(msg, file=sys.stderr, flush=True)


# --- size ----------------------------------------------------------------------------------------
def slide_size(vp_w: int, vp_h: int) -> tuple[int, int]:
    """The largest reference shape inside viewport - 66, keeping viewport - slide even."""
    rw, rh = e12.REF
    aw, ah = vp_w - MARGIN, vp_h - MARGIN
    w = min(aw, math.floor(ah * rw / rh))
    h = min(ah, round(w * rh / rw))
    w -= (vp_w - w) % 2
    h -= (vp_h - h) % 2
    return w, h


def predicted_viewport(mon: dict) -> tuple[int, int]:
    """E11's rule for maximised Paint at 100%: the work area's width, and its
    height less 210.5 px x the display scale, rounded half up."""
    w = mon["work"]
    return w.width, w.height - math.floor(210.5 * mon["dpi"] / 96 + 0.5)


def settled_canvas(paint: int, timeout: float = 6.0) -> dict:
    """The canvas rectangle once it stops moving."""
    last, end = None, time.perf_counter() + timeout
    while time.perf_counter() < end:
        c = core.info(paint)["canvas_on_screen"]
        if c and c == last:
            return c
        last = c
        time.sleep(0.3)
    return last


def centred_canvas(paint: int, tries: int = 3) -> dict:
    """Wait until the canvas sits centred in the viewport. Right after launch
    Paint can leave the canvas scrolled down by hundreds of pixels and centre
    it only later (seen: 510 px, mid-painting), so a steady position is not
    enough. If it stays off centre, set the zoom to fit and back to 100%
    again, which re-centres it (E11)."""
    for attempt in range(tries):
        end = time.perf_counter() + 3.0
        while time.perf_counter() < end:
            i = core.info(paint)
            c, vp = i["canvas_on_screen"], i["viewport"]
            if c and vp:
                want = (vp["left"] + (vp["width"] - c["width"]) // 2,
                        vp["top"] + (vp["height"] - c["height"]) // 2)
                if abs(c["left"] - want[0]) <= 2 and abs(c["top"] - want[1]) <= 2:
                    return c
            time.sleep(0.2)
        log(f"canvas not centred (at {c['left']},{c['top']}, want {want}): re-centring")
        core.set_zoom(paint, "fit")
        core.set_zoom(paint, 100)
    return settled_canvas(paint)


def canvas_origin(paint: int) -> tuple[int, int, int, int]:
    """The canvas's top-left on screen, from its white area (as E17), once
    the canvas is centred. Needs a blank canvas. Also returns the offset from
    UIA's canvas rectangle, so the origin can be found again later, when the
    canvas is no longer blank (UIA's rectangle can be a pixel off, E11)."""
    c = centred_canvas(paint)
    pad = 6
    box = (c["left"] - pad, c["top"] - pad, c["left"] + c["width"] + pad, c["top"] + c["height"] + pad)
    white = e17.Stage._mask(ImageGrab.grab(bbox=box, all_screens=True), (255, 255, 255))
    w, h = white.size
    px = white.load()
    left, _ = e17._longest_run([px[x, h // 4] for x in range(w)])
    top, _ = e17._longest_run([px[w // 4, y] for y in range(h)])
    x, y = box[0] + left, box[1] + top
    return x, y, x - c["left"], y - c["top"]


# --- rendering ----------------------------------------------------------------------------------
class Render(threading.Thread):
    """Marp in the background; `done` counts finished slides as they appear."""

    def __init__(self, work: Path, size: tuple[int, int]):
        super().__init__(daemon=True)
        self.work, self.size = work, size
        text = DECK.read_text(encoding="utf-8")
        self.total = text.split("\n---\n", 1)[1].count("\n---\n") + 1  # after the front matter
        key = hashlib.sha1(text.encode()).hexdigest()[:12]
        self.dir = work / "e13-cache" / f"{key}-{size[0]}x{size[1]}"
        self.done, self.files, self.error, self.cached, self.seconds = 0, [], None, False, None

    def run(self):
        t0 = time.perf_counter()
        try:
            self._render()
        except Exception as exc:  # noqa: BLE001
            self.error = f"{type(exc).__name__}: {exc}"
        self.seconds = round(time.perf_counter() - t0, 1)

    def _render(self):
        out = self.dir / "out"
        files = sorted(out.glob("deck.*.png")) if out.exists() else []
        if len(files) == self.total and (self.dir / "complete").exists():
            self.files, self.done, self.cached = files, self.total, True
            return
        shutil.rmtree(self.dir, ignore_errors=True)
        out.mkdir(parents=True)
        dw, dh = e12.MARP_DESIGN
        (self.dir / "theme.css").write_text(
            f"/* @theme presentator */\n@import 'default';\nsection {{ width: {dw}px; height: {dh}px; }}\n",
            encoding="utf-8")
        shutil.copy(DECK, self.dir / "deck.md")
        profiles = self.work / "browser-profiles"
        profiles.mkdir(exist_ok=True)
        env = {**os.environ, "TEMP": str(profiles), "TMP": str(profiles)}
        scale = self.size[0] / dw
        p = subprocess.Popen([str(self.work / "node_modules/.bin/marp.cmd"), "deck.md", "--html",
                              "--theme-set", "theme.css", "--browser", "edge",
                              "--browser-path", str(e12.EDGE), "--images", "png",
                              "--image-scale", f"{scale:.6f}", "-o", "out/deck.png"],
                             cwd=self.dir, env=env, stdout=subprocess.DEVNULL,
                             stderr=subprocess.PIPE, text=True, encoding="utf-8", errors="replace")
        while p.poll() is None:
            self.done = min(len(list(out.glob("deck.*.png"))), self.total - 1)
            time.sleep(0.1)
        if p.returncode:
            raise RuntimeError(p.stderr.read()[-500:])
        files = sorted(out.glob("deck.*.png"))
        for f in files:  # crop or pad the odd row of rounding (E12)
            img = Image.open(f).convert("RGB")
            if img.size != self.size:
                fixed = Image.new("RGB", self.size, img.getpixel((0, img.height - 1)))
                fixed.paste(img.crop((0, 0, min(img.width, self.size[0]),
                                      min(img.height, self.size[1]))), (0, 0))
                fixed.save(f)
        (self.dir / "complete").write_text("ok")
        self.files, self.done = files, self.total


# --- hand-drawn letters ------------------------------------------------------------------------------
_gdi = ctypes.WinDLL("gdi32", use_last_error=True)
_gdi.CreateCompatibleDC.restype = wintypes.HDC
_gdi.CreateCompatibleDC.argtypes = (wintypes.HDC,)
_gdi.CreateFontW.restype = wintypes.HFONT
_gdi.CreateFontW.argtypes = (ctypes.c_int,) * 5 + (wintypes.DWORD,) * 8 + (wintypes.LPCWSTR,)
_gdi.SelectObject.restype = wintypes.HGDIOBJ
_gdi.SelectObject.argtypes = (wintypes.HDC, wintypes.HGDIOBJ)
_gdi.SetBkMode.argtypes = (wintypes.HDC, ctypes.c_int)
for _f in ("BeginPath", "EndPath", "FlattenPath"):
    getattr(_gdi, _f).argtypes = (wintypes.HDC,)
_gdi.TextOutW.argtypes = (wintypes.HDC, ctypes.c_int, ctypes.c_int, wintypes.LPCWSTR, ctypes.c_int)
_gdi.GetPath.argtypes = (wintypes.HDC, ctypes.POINTER(wintypes.POINT),
                         ctypes.POINTER(ctypes.c_ubyte), ctypes.c_int)
_gdi.DeleteObject.argtypes = (wintypes.HGDIOBJ,)
_gdi.DeleteDC.argtypes = (wintypes.HDC,)


def glyph_contours(text: str, font: str = "Comic Sans MS", px: int = 200) -> list[list[tuple]]:
    """The text's outlines as polylines, from an installed font at runtime
    (GDI path, flattened): nothing of the font ships with the repo."""
    dc = _gdi.CreateCompatibleDC(None)
    hf = _gdi.CreateFontW(-px, 0, 0, 0, 700, 0, 0, 0, 1, 0, 0, 4, 0, font)
    old = _gdi.SelectObject(dc, hf)
    _gdi.SetBkMode(dc, 1)  # TRANSPARENT
    _gdi.BeginPath(dc)
    _gdi.TextOutW(dc, 0, 0, text, len(text))
    _gdi.EndPath(dc)
    _gdi.FlattenPath(dc)
    n = _gdi.GetPath(dc, None, None, 0)
    pts, types = (wintypes.POINT * n)(), (ctypes.c_ubyte * n)()
    _gdi.GetPath(dc, pts, types, n)
    _gdi.SelectObject(dc, old)
    _gdi.DeleteObject(hf)
    _gdi.DeleteDC(dc)
    contours, cur = [], []
    for p, t in zip(pts, types):
        if t & ~1 == 6:  # PT_MOVETO
            if len(cur) > 1:
                contours.append(cur)
            cur = [(p.x, p.y)]
        else:
            cur.append((p.x, p.y))
        if t & 1 and cur:  # PT_CLOSEFIGURE
            cur.append(cur[0])
    if len(cur) > 1:
        contours.append(cur)
    return contours


def wobble(contour, rng: random.Random, amp: float, step: float = 5.0):
    """Resample every `step` px and add a shaky hand: a random offset per
    stroke, two slow waves, and a small overshoot past the end."""
    pts = [contour[0]]
    for (x0, y0), (x1, y1) in zip(contour, contour[1:]):
        n = max(1, int(math.hypot(x1 - x0, y1 - y0) / step))
        pts += [(x0 + (x1 - x0) * k / n, y0 + (y1 - y0) * k / n) for k in range(1, n + 1)]
    if len(pts) > 2:  # overshoot along the last direction
        (xa, ya), (xb, yb) = pts[-2], pts[-1]
        pts += [(xb + (xb - xa) * k, yb + (yb - ya) * k) for k in (1, 2)]
    dx, dy = rng.uniform(-amp, amp), rng.uniform(-amp, amp)
    fx, fy = rng.uniform(0, 6.3), rng.uniform(0, 6.3)
    out = []
    for i, (x, y) in enumerate(pts):
        s = i * step
        out.append((x + dx + 0.6 * amp * math.sin(s / 70 + fx),
                    y + dy + 0.6 * amp * math.sin(s / 53 + fy)))
    return out


# --- the loading painting -----------------------------------------------------------------------------
class Painter:
    def __init__(self, paint: int, origin: tuple[int, int], size: tuple[int, int], pen):
        self.paint, self.origin, self.size, self.pen = paint, origin, size, pen
        self.rng = random.Random()
        self.tool = None
        w, h = size
        self.bar = (round(w * 0.12), round(h * 0.78), round(w * 0.88), round(h * 0.86))
        self.filled = self.bar[0] + 4

    def at(self, x, y):
        return round(self.origin[0] + x), round(self.origin[1] + y)

    def stroke(self, pts, ms: float = 3):
        e15.stroke(self.pen, [self.at(x, y) for x, y in pts], ms)

    def use(self, brush: str, colour: str, size: int):
        if self.tool != (brush, colour, size):
            e15.select_brush(self.paint, brush)
            e16.pick_colour(self.paint, colour)
            e16.slider(self.paint, "Size").SetValue(size)
            self.tool = (brush, colour, size)

    def sun(self):
        """A scribbled crayon sun with wonky rays, top right."""
        w, h = self.size
        cx, cy, r = w * 0.9, h * 0.14, min(w, h) * 0.07
        self.use("Crayon", "Orange", 22)
        turns = 5
        spiral = [(cx + r * t / (turns * 6.283) * math.cos(t) + self.rng.uniform(-2, 2),
                   cy + r * t / (turns * 6.283) * math.sin(t) + self.rng.uniform(-2, 2))
                  for t in [k * 0.2 for k in range(int(turns * 6.283 / 0.2) + 1)]]
        self.stroke(spiral, 3)
        for k in range(9):  # rays
            a = k * 2 * math.pi / 9 + self.rng.uniform(-0.25, 0.25)
            r0, r1 = 1.3 * r, (1.9 + self.rng.uniform(-0.2, 0.3)) * r
            self.stroke([(cx + r0 * math.cos(a), cy + r0 * math.sin(a)),
                         (cx + (r0 + r1) / 2 * math.cos(a + 0.05), cy + (r0 + r1) / 2 * math.sin(a + 0.05)),
                         (cx + r1 * math.cos(a), cy + r1 * math.sin(a))], 12)

    def bar_frame(self):
        x0, y0, x1, y1 = self.bar
        item = e16.ui(self.paint).child_window(title="Rectangle", control_type="ListItem").wrapper_object()
        e16.tap(item)  # E16: shapes by pen tap
        e16.pick_colour(self.paint, "Black")
        self.tool = None
        self.stroke([(x0 + (x1 - x0) * k / 10, y0 + (y1 - y0) * k / 10) for k in range(11)], 8)
        time.sleep(0.3)
        e16.key("S")  # commit the shape

    def fill_to(self, frac: float):
        x0, y0, x1, y1 = self.bar
        target = x0 + 4 + (x1 - x0 - 8) * frac
        if target <= self.filled + 2:
            return
        self.use("Crayon", "Lime", 12)
        pts, x, up = [], self.filled, True
        while x < target:
            pts.append((x, (y0 + 6) if up else (y1 - 6)))
            x += self.rng.uniform(7, 12)
            up = not up
        pts.append((target, y1 - 6))
        self.stroke(pts, 3)
        self.filled = target

    def title(self, text: str, progress) -> int:
        """Draw the title stroke by stroke; every few strokes, colour in the bar
        up to the render's progress. Returns the number of strokes."""
        w, h = self.size
        raw = glyph_contours(text)
        xs = [x for c in raw for x, _ in c]
        ys = [y for c in raw for _, y in c]
        bw, bh = max(xs) - min(xs), max(ys) - min(ys)
        box = (w * 0.08, h * 0.2, w * 0.82, h * 0.6)
        s = min((box[2] - box[0]) / bw, (box[3] - box[1]) / bh)
        ox = box[0] + ((box[2] - box[0]) - bw * s) / 2 - min(xs) * s
        oy = box[1] + ((box[3] - box[1]) - bh * s) / 2 - min(ys) * s
        colour = self.rng.choice(["Indigo", "Red", "Purple", "Turquoise", "Dark red"])
        strokes = [wobble([(ox + x * s, oy + y * s) for x, y in c], self.rng, amp=max(4.0, w / 350))
                   for c in raw]
        every = max(1, len(strokes) // 4)
        for i, st in enumerate(strokes):
            self.use("Crayon", colour, 10)
            self.stroke(st, 3 if progress() < 1 else 1)  # hurry once the slides are ready
            if (i + 1) % every == 0:
                self.fill_to(progress())
        return len(strokes)


# --- the show ------------------------------------------------------------------------------------------
class Show:
    def __init__(self, paint, origin, size, files, on_screen: Image.Image, debug_dir=None):
        self.paint, self.origin, self.size = paint, origin, size
        self.debug_dir = debug_dir
        self.on_screen = on_screen.convert("RGB")  # what the canvas shows now
        self.rng = random.Random(1)
        # index 0 is the loading painting (a snapshot of it), 1..n the slides:
        # the speaker can go back to the painting like to any slide
        self.pngs = [e1.enc_png(self.on_screen)] + [f.read_bytes() for f in files]
        self.images = [self.on_screen] + [Image.open(io.BytesIO(b)).convert("RGB")
                                          for b in self.pngs[1:]]
        self.blank = {}
        for name, rgb in (("black", (0, 0, 0)), ("white", (255, 255, 255))):
            img = Image.new("RGB", size, rgb)
            self.blank[name] = (e1.enc_png(img), img)
        self.clip = e1.MultiFormatClipboard()
        self.screen = e6.ScreenProbe()
        self.idx = 0  # the loading painting is on the canvas
        self.blanked = None
        self.changes = []  # (from, to, ms, keys combined)

    def probes(self, img: Image.Image, n: int = 3) -> list[tuple[int, int]]:
        """Up to n canvas points where the new slide differs from what is on
        screen, worked out in memory. Each screen read waits for a display
        refresh (E6), so poll as few points as possible."""
        diff = ImageChops.difference(self.on_screen, img).convert("L").point(lambda v: 255 if v > 8 else 0)
        box = diff.getbbox()
        if not box:
            return []
        found = []
        for _ in range(400):
            p = (self.rng.randrange(box[0], box[2]), self.rng.randrange(box[1], box[3]))
            if diff.getpixel(p):
                found.append(p)
                if len(found) == n:
                    break
        return found

    def paste(self, png: bytes, img: Image.Image) -> float | None:
        ox, oy = self.origin
        pts = self.probes(img)
        self.clip.offer([(e1.CF_PNG, png), (e8.EXCLUDE, struct.pack("<I", 0))])
        e10.send(list(e6.CTRL_V), e10.OWN_TAG)
        seen = None
        end = time.perf_counter() + 2.5
        while pts and time.perf_counter() < end:
            hits = sum(1 for x, y in pts if self.screen.pixel(ox + x, oy + y) == img.getpixel((x, y)))
            if hits * 3 >= len(pts) * 2:  # two of three: a border or toolbar may cover one
                seen = time.perf_counter()
                break
        if not pts:
            time.sleep(0.3)
        if seen is None and self.debug_dir:  # what was on the canvas instead (canvas only)
            n = len(list(self.debug_dir.glob("missed-*.png")))
            ImageGrab.grab(bbox=(ox, oy, ox + self.size[0], oy + self.size[1]), all_screens=True) \
                .save(self.debug_dir / f"missed-{n}.png")
            full = ImageGrab.grab(all_screens=True)
            full.resize((full.width // 2, full.height // 2)).save(self.debug_dir / f"missed-{n}-screen.png")
            log(f"Paint window {win32gui.GetWindowRect(self.paint)}, "
                f"placement {win32gui.GetWindowPlacement(self.paint)[1]}, "
                f"canvas {core.info(self.paint).get('canvas_on_screen')}")
            log(f"missed: probes {pts}, want {[img.getpixel(p) for p in pts]}, "
                f"see {[self.screen.pixel(ox + x, oy + y) for x, y in pts]}")
        e10.send(list(e6.KEY_S), e10.OWN_TAG)  # commit (E14)
        self.on_screen = img
        if seen is None and pts and core.dismiss_clipboard_error(self.paint, 0.5):
            log("closed Paint's clipboard-error dialog")
        return seen

    def run(self, hook) -> None:
        n = len(self.pngs)
        digits = ""
        while True:
            batch = [hook.events.get()]
            time.sleep(0.01)
            batch += hook.drain()
            t_key = batch[0][2]
            target, blank, end = self.idx, None, False
            for action, vk, _ in batch:
                if self.blanked and action not in ("end",):
                    blank = "back"
                elif action == "end":
                    end = True
                elif action.startswith("digit"):
                    digits += action[-1]
                elif action == "next" and vk == 0x0D and digits:  # 0 is the painting
                    target, digits = min(int(digits), n - 1), ""
                elif action == "next":
                    target = min(target + 1, n - 1)
                elif action == "prev":
                    target = max(target - 1, 0)
                elif action == "first":  # Home: back to the painting
                    target = 0
                elif action == "last":
                    target = n - 1
                elif action in ("black", "white"):
                    blank = action
            if end:
                return
            if blank in ("black", "white"):
                png, img = self.blank[blank]
                to = blank
            elif blank == "back" or target != self.idx:
                png, img, to = self.pngs[target], self.images[target], target
            else:
                continue
            seen = self.paste(png, img)
            ms = round((seen - t_key) * 1000) if seen else None
            self.changes.append({"from": self.blanked or self.idx, "to": to, "ms": ms,
                                 "keys": len(batch)})
            log(f"slide {self.blanked or self.idx} -> {to}: {ms} ms ({len(batch)} key(s))")
            if blank in ("black", "white"):
                self.blanked = blank
            else:
                self.blanked, self.idx = None, target

    def close(self):
        self.clip.stop()
        self.screen.close()


def auto_keys(stop: threading.Event) -> None:
    """Scripted 'user' keys (tagged as the user, E10) to test the show."""
    script = [(0x27, 1.2), (0x27, 1.2), (0x20, 1.2), (0x25, 1.2), (0x23, 1.2),
              (0x24, 1.2),                # Home: the painting (0)
              (0x33, 0.2), (0x0D, 1.2),   # 3 + Enter: slide 3
              (0x30, 0.2), (0x0D, 1.2),   # 0 + Enter: the painting
              (0x27, 1.2), (0x25, 1.2),   # Right to slide 1, Left back to the painting
              (0x42, 1.2), (0x42, 1.2),
              (0x27, 0.05), (0x27, 0.05), (0x27, 0.05), (0x27, 1.5), (0x1B, 0)]
    time.sleep(1.5)
    for vk, wait in script:
        if stop.is_set():
            return
        e10.tap(vk)
        time.sleep(wait)


def main() -> int:
    p = argparse.ArgumentParser(description=__doc__,
                                formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--work", required=True, type=Path)
    p.add_argument("--monitor", default=None, help="default: the primary screen")
    p.add_argument("--auto", action="store_true", help="drive the show with scripted keys")
    p.add_argument("--snapshot", type=Path, help="save the finished loading painting (canvas only)")
    p.add_argument("--debug-dir", type=Path, help="save the canvas when a paste is not seen")
    a = p.parse_args()
    work = a.work.resolve()
    mons = e11.monitors()
    mon = next(m for m in mons if (m["device"] == a.monitor if a.monitor else m["primary"]))
    out: dict = {"screen": {"device": mon["device"], "dpi": mon["dpi"]}}

    # pre-flight
    class SPS(ctypes.Structure):
        _fields_ = (("ac", ctypes.c_ubyte), ("flag", ctypes.c_ubyte), ("pct", ctypes.c_ubyte),
                    ("sys", ctypes.c_ubyte), ("life", wintypes.DWORD), ("full", wintypes.DWORD))
    sps = SPS()
    ctypes.windll.kernel32.GetSystemPowerStatus(ctypes.byref(sps))
    out["on mains power"] = sps.ac == 1
    if sps.ac != 1:
        log("pre-flight: on battery; Paint is slower and less steady (E6). Plug in the charger.")
    log("pre-flight: switch Do not disturb on, or notifications will show over the slides (E19).")
    ctypes.windll.kernel32.SetThreadExecutionState(0x80000003)  # keep the display awake

    pen = e15.Pen()
    e16.PEN = pen
    paint = None
    try:
        predicted = predicted_viewport(mon)
        size = slide_size(*predicted)
        render = Render(work, size)
        render.start()
        paint = e11.open_maximised(mon, *size)
        vp = core.info(paint)["viewport"]
        out["viewport predicted"] = list(predicted)
        out["viewport"] = [vp["width"], vp["height"]]
        out["Paint launches"] = 1
        if slide_size(vp["width"], vp["height"]) != size:  # the prediction was off: reopen
            log(f"viewport {vp['width']}x{vp['height']}, not {predicted[0]}x{predicted[1]} as predicted")
            core.close(paint, discard=True)
            size = slide_size(vp["width"], vp["height"])
            render.join(120)
            render = Render(work, size)
            render.start()
            paint = e11.open_maximised(mon, *size)
            out["Paint launches"] = 2
        out["slide size"] = list(size)
        log(f"viewport {vp['width']}x{vp['height']}, slide {size[0]}x{size[1]}")
        core.focus(paint)
        e16.key("S")
        ox, oy, dx, dy = canvas_origin(paint)
        origin = (ox, oy)
        out["origin offset from UIA"] = [dx, dy]
        t0 = time.perf_counter()
        painter = Painter(paint, origin, size, pen)
        painter.sun()
        painter.bar_frame()
        strokes = painter.title("Paint Presentator", lambda: render.done / render.total)
        render.join(120)
        painter.fill_to(1.0)
        e16.key("S")  # the Select tool: no brush panel, and pastes commit with S
        out["painting"] = {"seconds": round(time.perf_counter() - t0, 1), "title strokes": strokes}
        out["render"] = {"seconds": render.seconds, "slides": render.total, "cached": render.cached,
                         "error": render.error}
        log(f"painting done in {out['painting']['seconds']} s; render {out['render']}")
        time.sleep(0.5)
        c = settled_canvas(paint)  # has the canvas moved since it was measured?
        now = (c["left"] + dx, c["top"] + dy)  # UIA's rectangle plus the measured offset
        if now != origin:
            log(f"canvas moved from {origin} to {now}")
            out["canvas moved during the painting"] = [list(origin), list(now)]
            origin = now
        painting = ImageGrab.grab(bbox=(*origin, origin[0] + size[0], origin[1] + size[1]),
                                  all_screens=True)  # the canvas only
        if a.snapshot:  # for a look at the painting
            painting.save(a.snapshot)
        if render.error:
            raise RuntimeError(render.error)
        show = Show(paint, origin, size, render.files, painting, a.debug_dir)
        hook = e10.KeyHook({paint})
        stop = threading.Event()
        if a.auto:
            threading.Thread(target=auto_keys, args=(stop,), daemon=True).start()
        else:
            log("Ready: Right, Space or Enter for the first slide; Esc ends.")
        try:
            show.run(hook)
        finally:
            stop.set()
            hook.stop()
            show.close()
        ms = [c["ms"] for c in show.changes if c["ms"] is not None]
        out["slide changes"] = show.changes
        if ms:
            out["key to slide ms"] = {"median": statistics.median(ms), "max": max(ms), "n": len(ms)}
    finally:
        ctypes.windll.kernel32.SetThreadExecutionState(0x80000000)
        pen.close()
        if paint:
            core.close(paint, discard=True)
        profiles = work / "browser-profiles"
        out["leftover Edge processes with our profile"] = e12.leftover_edge(profiles)
        shutil.rmtree(profiles, ignore_errors=True)
    json.dump(out, sys.stdout, indent=2)
    print()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
