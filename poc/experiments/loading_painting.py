"""The loading painting, The sky leaks (docs/loading-painting.md), drawn live in
Paint with the synthetic pen (E15) and Paint's own tools (E16, E27).

The pen writes the title, draws a frame and fills it in step with the slides
that are really ready, decorates while there is time, floods the sky through a
gap in the cloud with the bucket, and signs with a typo. It stops at once when
`stop()` returns true (the first key press of the show).

Deviation from the browser concept, from E27: the signature with its opaque
white box is typed after the flood, not before. Paint's bucket floods every
connected white pixel, so a white box typed earlier would turn blue as well.
"""
from __future__ import annotations

import json
import math
import random
import tempfile
import time
from dataclasses import dataclass
from datetime import date
from pathlib import Path

import e10_keys as e10
import e13_presenter as e13  # glyph_contours
import e15_draw as e15
import e16_tools as e16
from PIL import Image, ImageChops, ImageDraw, ImageFilter, ImageGrab

TITLE_COLOURS = ["Indigo", "Red", "Purple", "Dark red", "Black"]
# for the bucket in the title's letters: the palette without White, Black,
# the greys and the sky's Light turquoise
LETTER_COLOURS = ["Dark red", "Red", "Rose", "Orange", "Gold", "Yellow", "Green", "Lime",
                  "Turquoise", "Indigo", "Blue-gray", "Purple", "Lavender", "Brown"]


def key(vk: int) -> None:
    """A key tagged as the presenter's own, so the keyboard hook lets it through (E10)."""
    e10.send([(vk, False), (vk, True)], e10.OWN_TAG)
    time.sleep(0.25)


@dataclass
class Progress:
    done: int
    total: int


class Interrupted(Exception):
    pass


class LoadingPainting:
    def __init__(self, paint: int, origin: tuple[int, int], size: tuple[int, int], pen,
                 title: str, progress, prep_estimate: float, stop=lambda: False,
                 seed: int | None = None):
        self.paint, self.origin, self.size, self.pen = paint, origin, size, pen
        self.title_text = title
        self.progress = progress  # () -> Progress
        self.stop = stop
        self.rng = random.Random(seed)
        self.W = 1280.0  # design space: 1280 wide, height in the canvas's shape
        self.H = 1280.0 * size[1] / size[0]
        self.s = size[0] / 1280.0  # design px -> canvas px
        self.u = self.H / 720.0
        self.P = prep_estimate
        self.fast = prep_estimate < 6
        self.with_sun = prep_estimate >= 9
        self.tool = None  # (brush, colour, size)
        self.filled = 0
        self.t0 = time.perf_counter()
        self.log: list[str] = []
        self.right = self.rng.random() < 0.5  # sun on the right, cloud on the left
        H = self.H
        self.sun_c = ((0.87 if self.right else 0.13) * self.W, 0.17 * H)
        self.sun_r = 0.075 * H
        self.cloud_c = ((0.22 if self.right else 0.78) * self.W, 0.14 * H)
        self.frame = (0.12 * self.W, 0.75 * H, 0.88 * self.W, 0.86 * H)

    # --- drawing primitives ------------------------------------------------------------
    def check(self):
        if self.stop():
            raise Interrupted

    def note(self, what: str):
        self.log.append(f"{time.perf_counter() - self.t0:5.1f}s {what}")

    def at(self, x: float, y: float) -> tuple[int, int]:
        return round(self.origin[0] + x * self.s), round(self.origin[1] + y * self.s)

    def dense(self, pts, step: float = 2.0):
        """Points every `step` canvas px (Paint draws straight segments; short
        steps keep curves round)."""
        out = [pts[0]]
        for (x0, y0), (x1, y1) in zip(pts, pts[1:]):
            n = max(1, math.ceil(math.hypot(x1 - x0, y1 - y0) * self.s / step))
            out += [(x0 + (x1 - x0) * j / n, y0 + (y1 - y0) * j / n) for j in range(1, n + 1)]
        return out

    def wob(self, pts, amp: float, fq: float = 1.0):
        a, b = self.rng.random() * 6, self.rng.random() * 6
        return [(x + amp * (math.sin(i * 0.11 * fq + a) + 0.5 * math.sin(i * 0.37 * fq + b)),
                 y + amp * (math.cos(i * 0.13 * fq + b) + 0.5 * math.sin(i * 0.29 * fq + a)))
                for i, (x, y) in enumerate(pts)]

    def strokes(self, paths, budget: float):
        """Draw design-space paths within `budget` seconds. The pen needs at
        least 1 ms between frames (E15), so long paths are thinned to fit.
        Paint drops the last segment of a stroke (E27): the end point is sent
        twice."""
        screen = [[self.at(x, y) for x, y in p] for p in paths if len(p) > 1]
        n = sum(len(p) for p in screen)
        if not n:
            return
        ms = budget * 1000 / n
        keep = 1
        while ms < 1.0:
            keep += 1
            ms = budget * 1000 / (n / keep)
        for p in screen:
            self.check()
            q = p[::keep]
            if q[-1] != p[-1]:
                q.append(p[-1])
            q.append(q[-1])
            e15.stroke(self.pen, q, ms)

    def use(self, colour: str, width: float = 5.0):
        """The Brush in a colour, `width` design px wide (Brush width = size + 1, E27)."""
        size = max(0, round(width * self.u * self.s) - 1)
        self.check()
        if not self.tool or self.tool[0] != "Brush":
            e15.select_brush(self.paint, "Brush")
            self.tool = ("Brush", None, None)
        if self.tool[1] != colour:
            e16.pick_colour(self.paint, colour)
        if self.tool[2] != size:
            e16.slider(self.paint, "Size").SetValue(size)
        self.tool = ("Brush", colour, size)

    # --- the elements ------------------------------------------------------------------------
    def title(self):
        colour = self.title_colour = self.rng.choice(TITLE_COLOURS)
        self.use(colour)
        W, H, u = self.W, self.H, self.u
        box = (0.08 * W, 0.22 * H, 0.92 * W, 0.58 * H)
        chains = self._wrap(self.title_text, box)
        xs = [x for c in chains for x, _ in c]
        ys = [y for c in chains for _, y in c]
        bw, bh = max(xs) - min(xs), max(ys) - min(ys)
        k = min((box[2] - box[0]) / bw, (box[3] - box[1]) / bh)
        ox = box[0] + ((box[2] - box[0]) - bw * k) / 2 - min(xs) * k
        oy = box[1] + ((box[3] - box[1]) - bh * k) / 2 - min(ys) * k
        # the letters' true outlines, before the shake, for the bucket later
        self.title_shapes = [[(ox + x * k, oy + y * k) for x, y in c] for c in chains]
        self.title_em = 200 * k * self.s  # the font's 200 px, in canvas px
        paths, pen_at = [], None
        for c in chains:
            dx, dy = (self.rng.random() - 0.5) * 2 * u, (self.rng.random() - 0.5) * 2 * u
            p = self.wob([(ox + x * k + dx, oy + y * k + dy) for x, y in c[::2]], 1.5 * u, 1.3)
            p = self.dense(p + [p[0], p[1]])  # shaken first, then closed with an overshoot
            if pen_at and self.rng.random() < 0.12 and math.dist(pen_at, p[0]) < 0.12 * W:
                paths[-1] += self.dense([pen_at, p[0]]) + p  # the pen forgot to lift
            else:
                paths.append(p)
            pen_at = p[-1]
        self.note(f"title in {colour}")
        self.strokes(paths, 2.0 if self.fast else 3.0)

    def _wrap(self, text: str, box) -> list:
        """The title's outlines (bold Comic Sans, from the installed font),
        wrapped into as many lines (up to 4) as makes the letters biggest in
        the box; each line centred on x = 0, lines 260 font px apart."""
        words = text.split() or ["…"]
        bw, bh = box[2] - box[0], box[3] - box[1]
        best = None
        for n_lines in range(1, min(4, len(words)) + 1):
            per = math.ceil(len(words) / n_lines)
            lines = [" ".join(words[i:i + per]) for i in range(0, len(words), per)]
            outs = [e13.glyph_contours(line, px=200) for line in lines]
            spans = [(min(x for c in o for x, _ in c), max(x for c in o for x, _ in c)) for o in outs]
            scale = min(bw / max(b - a for a, b in spans), bh / (len(lines) * 260))
            if best is None or scale > best[0]:
                best = (scale, outs, spans)
        _, outs, spans = best
        return [[(x - (a + b) / 2, y + i * 260) for x, y in c]
                for i, (o, (a, b)) in enumerate(zip(outs, spans)) for c in o]

    def sun(self):
        self.use("Orange")
        (sx, sy), sr, R = self.sun_c, self.sun_r, self.rng
        ring = []
        a = 0.0
        while a <= 2 * math.pi * 1.12:
            rr = sr * (1 + (R.random() - 0.5) * 0.12)
            ring.append((sx + math.cos(a) * rr, sy + math.sin(a) * rr))
            a += 0.25
        scribble = []
        for _ in range(16):
            a, rr = R.random() * 2 * math.pi, sr * (0.55 + R.random() * 0.4)
            scribble.append((sx + math.cos(a) * rr, sy + math.sin(a) * rr))
        rays = []
        n = 7 + int(R.random() * 3)
        for i in range(n):
            a = i * 2 * math.pi / n + (R.random() - 0.5) * 0.5
            r1, r2 = sr * (1.2 + R.random() * 0.3), sr * (1.6 + R.random() * 0.7)
            b = a + (R.random() - 0.5) * 0.3
            ray = [(sx + math.cos(a) * r1, sy + math.sin(a) * r1), (sx + math.cos(b) * r2, sy + math.sin(b) * r2)]
            rays.append(self.dense(ray))
            if R.random() < 0.3:  # drawn twice
                rays.append(self.dense([(x + 3 * self.u, y + 2 * self.u) for x, y in ray]))
        self.note("sun")
        self.strokes([self.dense(ring), self.dense(scribble)] + rays, 1.8)

    def cloud(self):
        self.use("Black")
        (cx, cy), u, R = self.cloud_c, self.u, self.rng
        cw, ch, gap = 0.14 * self.W, 0.065 * self.H, 16 * u
        bumps = 3 + int(R.random() * 3)
        pts = [(cx - cw / 2 + gap, cy + ch / 2 + (R.random() - 0.5) * 4 * u), (cx + cw / 2, cy + ch / 2)]
        for i in range(51):
            t = i / 50
            pts.append((cx + cw / 2 - t * cw,
                        cy + ch / 2 - ch * math.sin(math.pi * t) * 0.9
                        - abs(math.sin(bumps * math.pi * t)) * ch * 0.6))
        self.note("cloud, left open")
        self.strokes([self.dense(self.wob(pts, 2 * u, 2))], 0.9)

    def frame_lines(self):
        self.use("Black")
        x0, y0, x1, y1 = self.frame
        u, R = self.u, self.rng
        j = lambda: (R.random() - 0.5) * 8 * u  # noqa: E731
        corners = [(x0 - 8 * u, y0 + j()), (x1 + 10 * u, y0 + j()), (x1 + j(), y0 - 6 * u),
                   (x1 + j(), y1 + 8 * u), (x1 + 6 * u, y1 + j()), (x0 + j(), y1 + j()),
                   (x0 + j(), y1 + 6 * u), (x0 + j(), y0 - 10 * u)]
        self.note("frame")
        self.strokes([self.dense(self.wob(self.dense(corners, 12), 1.5 * u))], 0.4 if self.fast else 0.6)

    def fill_to(self, k: int, total: int):
        """Lime zigzag up to slide k of total: one bite of 1/total per slide."""
        if k <= self.filled:
            return
        self.use("Lime", 8.0)
        x0, y0, x1, y1 = self.frame
        u, R = self.u, self.rng
        iw = x1 - x0 - 12 * u
        xa, xb = x0 + 6 * u + iw * self.filled / total, x0 + 6 * u + iw * k / total
        pts, x, up = [], xa, True
        while x <= xb:
            pts.append((x, (y0 + (6 - R.random() * 12) * u) if up else (y1 - (6 - R.random() * 12) * u)))
            x += (3 + R.random() * 6) * u
            up = not up
        if len(pts) < 2:
            pts.append((xb, y1 - 6 * u))
        self.note(f"fill: slides {self.filled + 1}-{k} of {total}")
        self.strokes([self.dense(pts)], max(0.12, 1.4 * (k - self.filled) / total))
        self.filled = k

    def catch_up(self):
        p = self.progress()
        if p.done > self.filled:
            self.fill_to(p.done, p.total)

    def birds(self):
        self.use("Black", 4.0)
        paths = []
        for fx, fy in ((0.5, 0.09), (0.57, 0.14), (0.53, 0.2)):
            if self.rng.random() < 0.3:
                continue
            x, y = (fx if self.right else 1 - fx) * self.W, fy * self.H
            k = (0.7 + self.rng.random() * 0.7) * self.u
            paths.append(self.dense([(x - 16 * k, y - 4 * k), (x - 7 * k, y - 8 * k), (x, y + 2 * k),
                                     (x + 8 * k, y - 9 * k), (x + 17 * k, y - 2 * k)]))
        self.note("birds")
        self.strokes(paths, 0.5)

    def finale(self, cloud: bool):
        """The bucket in the cloud: it leaks through the gap and floods the sky."""
        self.check()
        key(ord("B"))  # Fill with color (tagged: B is the show's black-screen key)
        e16.pick_colour(self.paint, "Light turquoise")
        self.tool = None
        x, y = (self.cloud_c[0], self.cloud_c[1] + 0.005 * self.H) if cloud else (0.5 * self.W, 0.08 * self.H)
        sx, sy = self.at(x, y)
        self.note("bucket: ...hmm")
        self.pen._inject(sx, sy, e15.F_INRANGE | e15.F_UPDATE)  # hover over the spot
        time.sleep(0.3 if self.fast else 0.8)
        self.check()
        e15.stroke(self.pen, [(sx, sy), (sx, sy)], 2)
        self.note("leaks!" if cloud else "fills the sky")
        time.sleep(0.5)

    def letters(self):
        """With the bucket still in hand: fill 1 to 4 of the title's letters,
        chosen at random, each in its own random colour."""
        self.check()
        if not getattr(self, "title_shapes", None):
            return
        n = self.rng.randint(1, 4)
        colours = self.rng.sample([c for c in LETTER_COLOURS if c != self.title_colour], n)
        self.note(f"letters in {', '.join(colours)}")
        picked: list[tuple[int, int]] = []
        for spot in self._letter_spots():
            if len(picked) == n:
                break
            if any(math.dist(spot, p) < 0.45 * self.title_em for p in picked):
                continue  # likely the same letter
            if not self._white(spot):
                continue  # filled already: a letter that touches one just filled
            self.check()
            e16.pick_colour(self.paint, colours[len(picked)])
            e15.stroke(self.pen, [spot, spot], 2)
            picked.append(spot)
            time.sleep(0.3)

    def _letter_spots(self) -> list[tuple[int, int]]:
        """Screen points well inside a letter's body (not in its holes) that
        are still white after the flood, in random order. One screen read of
        the title's area, compared at half resolution with the letters' true
        outlines (even-odd, so holes are out), shrunk by the shake and half the
        stroke."""
        pts = [self.at(x, y) for c in self.title_shapes for x, y in c]
        x0, y0 = min(x for x, _ in pts) - 4, min(y for _, y in pts) - 4
        x1, y1 = max(x for x, _ in pts) + 4, max(y for _, y in pts) + 4
        shot = ImageGrab.grab(bbox=(x0, y0, x1, y1), all_screens=True).convert("L")
        half = (shot.width // 2, shot.height // 2)
        white = shot.resize(half, Image.NEAREST).point(lambda v: 255 if v >= 245 else 0)
        body = Image.new("L", half, 0)
        for c in self.title_shapes:
            m = Image.new("L", half, 0)
            ImageDraw.Draw(m).polygon([((x - x0) / 2, (y - y0) / 2)
                                       for x, y in (self.at(*p) for p in c)], fill=255)
            body = ImageChops.difference(body, m)  # xor: a hole cancels its letter
        for _ in range(math.ceil(5 * self.u * self.s / 2)):  # the shake + half the stroke
            body = body.filter(ImageFilter.MinFilter(3))
        for _ in range(2):  # and 4 px of white around the spot
            white = white.filter(ImageFilter.MinFilter(3))
        ok = ImageChops.multiply(body, white)
        px = ok.load()
        spots = [(x0 + 2 * x, y0 + 2 * y) for y in range(0, ok.height, 3)
                 for x in range(0, ok.width, 3) if px[x, y]]
        self.rng.shuffle(spots)
        return spots

    @staticmethod
    def _white(spot: tuple[int, int]) -> bool:
        x, y = spot
        lo, _ = ImageGrab.grab(bbox=(x - 2, y - 2, x + 3, y + 3), all_screens=True) \
            .convert("L").getextrema()
        return lo >= 245

    def signature(self):
        """Today's date in Comic Sans, in an opaque white box, with a typo
        (the last two digits swapped) put right with Backspace."""
        self.check()
        d = date.today()
        dd, mm, yy = f"{d.day:02d}", f"{d.month:02d}", f"{d.year % 100:02d}"
        # The text is about 6% of the canvas height (larger than the concept's
        # 3.3%, the user's call). The box is drawn to fit "dd.mm.yy" at that
        # size, in the bottom right corner: the committed box keeps the size
        # it was drawn at.
        em = 0.06 * self.size[1]  # canvas px
        x0, y0 = self.at(0.985 * self.W - 4.2 * em / self.s, 0.99 * self.H - 1.6 * em / self.s)
        x1, y1 = self.at(0.985 * self.W, 0.99 * self.H)
        key(ord("T"))
        e15.stroke(self.pen, [(x0, y0), ((x0 + x1) // 2, (y0 + y1) // 2), (x1, y1)], 8)
        time.sleep(0.6)
        remember_text_settings(self.paint)
        e16.set_font(self.paint, "Comic Sans MS")
        size = set_font_size(self.paint, round(em * 0.75))  # points = px * 0.75 at 100% zoom
        self.note(f"{e16.current_font(e16._font_combo(self.paint))} {size}")
        fill = background_fill(self.paint)
        if fill.iface_toggle.CurrentToggleState != 1:
            fill.toggle()
        e16.pick_colour(self.paint, "White", slot=2)  # the box's colour
        e16.pick_colour(self.paint, "Black", slot=1)
        e15.stroke(self.pen, [(x0 + 6, y0 + 6), (x0 + 6, y0 + 6)], 2)  # back into the box
        time.sleep(0.3)
        self.note("signature with a typo")
        e16.type_text(f"{dd}.{mm}.{yy[1]}{yy[0]}")
        time.sleep(0.4)
        key(0x08)
        key(0x08)  # Backspace twice (tagged: Backspace is a show key)
        e16.type_text(yy)
        time.sleep(0.3)
        commit_box(self.paint, self.pen, self.at(0.40 * self.W, 0.94 * self.H))
        self.tool = None

    def restore_text_settings(self, xy: tuple[int, int] | None = None) -> None:
        """Paint keeps the Text font across sessions (E16): put it back, and
        switch Background fill off again. Call before Paint closes."""
        restore_text_settings(self.paint, xy or self.at(200, 200))


    # --- the whole painting ----------------------------------------------------------------
    def run(self) -> dict:
        """Paint until the finale and the signature are done, or `stop()`."""
        result = {"fast": self.fast, "sun": self.with_sun, "interrupted": False}
        try:
            self.title()
            if self.with_sun:
                self.sun()
            cloud = not self.fast
            if cloud:
                self.cloud()
            self.frame_lines()
            self.catch_up()  # the fill starts once the frame is there (the concept)
            # decorations while the time lasts (only the birds before the finale)
            if not self.fast:
                p = self.progress()
                elapsed = time.perf_counter() - self.t0
                est = (p.total - p.done) * elapsed / p.done if p.done else self.P - elapsed
                if p.done < p.total and est >= 2.5 - 1:
                    self.birds()
                    self.catch_up()
            # wait at the frame, one bite per slide that becomes ready
            while True:
                p = self.progress()
                if p.done > self.filled:
                    self.fill_to(p.done, p.total)
                if self.filled >= p.total:
                    break
                self.check()
                time.sleep(0.05)
            self.finale(cloud)
            self.letters()
            self.signature()
            key(ord("S"))  # the Select tool: no brush panel over the canvas
        except Interrupted:
            result["interrupted"] = True
            key(ord("S"))
        result["seconds"] = round(time.perf_counter() - self.t0, 1)
        result["steps"] = self.log
        return result


# --- the user's Text settings ---------------------------------------------------------------
# Paint keeps the Text tool's font and font size across sessions (E16, E27),
# and the signature changes both. The originals are written to a file before
# the change, so that a run that dies half way is put right by the next one,
# instead of the next one taking Comic Sans for the original.
STATE = Path(tempfile.gettempdir()) / "paintctl" / "text-settings.json"


def remember_text_settings(paint: int) -> None:
    """Record the user's font and size (a text box must be open), unless an
    earlier, interrupted run already did."""
    STATE.parent.mkdir(exist_ok=True)
    if not STATE.exists():
        STATE.write_text(json.dumps({"font": e16.current_font(e16._font_combo(paint)),
                                     "size": current_font_size(paint)}), encoding="utf-8")


def commit_box(paint: int, pen, xy: tuple[int, int]) -> None:
    """Commit an open text box: a pen tap on the canvas outside it, at screen
    point xy, then another tool. Choosing another tool straight away is not
    reliable (E27): it committed the text in some runs and dropped it in
    others. The tap commits; with the Text tool it also opens a new, empty
    box at xy, which the tool change then drops."""
    e15.stroke(pen, [xy, xy], 2)
    time.sleep(0.4)
    e16._close_box(paint)


def background_fill(paint: int):
    """The Text tool's Background fill switch (UIA lists it twice)."""
    return e16.ui(paint).child_window(title="Background fill", control_type="CheckBox",
                                      found_index=0).wrapper_object()


def restore_text_settings(paint: int, xy: tuple[int, int]) -> bool:
    """Put back the font and size recorded in STATE and switch Background fill
    off, in an empty text box at screen point xy. Returns whether there was
    anything to restore."""
    if not STATE.exists():
        return False
    saved = json.loads(STATE.read_text(encoding="utf-8"))
    e16._text_box(paint, xy)
    if saved.get("font"):
        e16.set_font(paint, saved["font"])
    if str(saved.get("size") or "").isdigit():
        set_font_size(paint, saved["size"])
    fill = background_fill(paint)
    if fill.iface_toggle.CurrentToggleState == 1:
        fill.toggle()
    e16._close_box(paint)
    STATE.unlink()
    return True


def font_size_combo(paint: int):
    """The Text tool's font size box (UIA lists it twice)."""
    return e16.ui(paint).child_window(auto_id="FontSizeComboBox", control_type="ComboBox",
                                      found_index=0).wrapper_object()


def current_font_size(paint: int) -> str | None:
    try:
        return font_size_combo(paint).iface_value.CurrentValue
    except Exception:  # noqa: BLE001
        return None


def set_font_size(paint: int, pt: int | str) -> str | None:
    """With a text box open: pick the listed size nearest to `pt` from below
    (the smallest when none is). Its UIA Value pattern accepts a value but Paint
    ignores it, and typing into the box did not take either (E27); selecting
    the list item does. Returns the size Paint then reports."""
    combo = font_size_combo(paint)
    combo.expand()
    time.sleep(0.5)
    items = {int(w.element_info.name): w for w in combo.descendants(control_type="ListItem")
             if w.element_info.name.isdigit()}
    if items:
        fits = [s for s in items if s <= int(pt)]
        items[max(fits) if fits else min(items)].select()
        time.sleep(0.4)
    try:
        combo.collapse()
    except Exception:  # noqa: BLE001
        pass
    return current_font_size(paint)
