"""E27: what the loading painting needs from real Paint (docs/loading-painting.md).

The designer's concept was simulated in a browser. This checks the open
points in Paint itself, maximised at 100%, drawing with the synthetic pen:

- brush width: the Brush at several size-slider values, width measured;
- the flood: a Brush cloud left open by a gap, a closed rectangle and a
  closed ring, then the bucket (Light turquoise) clicked inside the cloud.
  Does the colour leak through the gap into the whole sky, do the closed
  shapes stay white inside, and what do the stroke edges look like (pixels
  that are neither white, black nor the fill colour)?
- the text tool: the controls its toolbar offers (looking for an opaque
  background switch and the font size), and how a typed text with Backspace
  comes out. The font is only read, not changed (Paint keeps it, E16);
- the white box: a white text box, and a white rectangle with a thin outline,
  each before the flood: does either stay white?

Run from poc/paintctl:

    uv run python ../experiments/e27_painting_checks.py [--snapshot out.png]

Hands off the mouse and keyboard while it runs (about a minute). Prints JSON.
"""
from __future__ import annotations

import argparse
import json
import sys
import time
from pathlib import Path

import e4_uia_survey as e4
import e6_paste_speed as e6
import e11_display_area as e11
import e13_presenter as e13  # canvas_origin
import e15_draw as e15
import e16_tools as e16
from paintctl import core
from PIL import ImageGrab

CANVAS = (2000, 1000)
TURQ = (153, 217, 234)  # Light turquoise


def grab(origin, box):
    ox, oy = origin
    return ImageGrab.grab(bbox=(ox + box[0], oy + box[1], ox + box[2], oy + box[3]),
                          all_screens=True).convert("RGB")


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--monitor", default=r"\\.\DISPLAY1")
    ap.add_argument("--snapshot", type=Path)
    a = ap.parse_args()
    mon = next(m for m in e11.monitors() if m["device"] == a.monitor)
    out: dict = {}
    pen = e16.PEN = e15.Pen()
    paint = e11.open_maximised(mon, *CANVAS)
    font_found = None
    try:
        core.focus(paint)
        e16.key("S")
        ox, oy, _, _ = e13.canvas_origin(paint)

        def at(x, y):
            return ox + x, oy + y

        def stroke(pts, ms=3):
            e15.stroke(pen, [at(x, y) for x, y in pts], ms)

        # --- brush width per size-slider value ---------------------------------------
        e15.select_brush(paint, "Brush")
        widths = {}
        for i, v in enumerate((0, 1, 2, 3, 4, 6, 8, 12, 20)):
            e16.slider(paint, "Size").SetValue(v)
            time.sleep(0.2)
            x0, y = 60 + i * 110, 60
            stroke([(x0 + k * 8, y) for k in range(11)], 3)
            time.sleep(0.2)
            col = grab((ox, oy), (x0 + 40, y - 40, x0 + 41, y + 41))
            widths[v] = sum(1 for yy in range(col.height) if col.getpixel((0, yy)) != (255, 255, 255))
        out["Brush width by size"] = widths

        # --- shapes for the flood, drawn with the Brush at size 4 ----------------------
        e16.slider(paint, "Size").SetValue(4)
        e16.pick_colour(paint, "Black")
        # an open cloud: a bumpy arc whose ends leave a 40 px gap at the bottom left
        cx, cy, cw, chh = 1500, 260, 300, 140
        pts = [(cx - cw / 2 + 40, cy + chh / 2), (cx + cw / 2, cy + chh / 2)]
        import math
        for t in [k / 60 for k in range(61)]:
            pts.append((cx + cw / 2 - t * cw,
                        cy + chh / 2 - chh * math.sin(math.pi * t) * 0.9
                        - abs(math.sin(4 * math.pi * t)) * chh * 0.6))
        stroke([(round(x), round(y)) for x, y in pts], 3)
        # a closed rectangle (like the progress frame)
        rect = [(200, 700), (900, 700), (900, 850), (200, 850), (200, 700)]
        stroke([(round(x0 + (x1 - x0) * k / 40), round(y0 + (y1 - y0) * k / 40))
                for (x0, y0), (x1, y1) in zip(rect, rect[1:]) for k in range(41)], 2)
        # a closed ring (like a letter's counter)
        ring = [(600 + 80 * math.cos(t / 30 * 2 * math.pi), 450 + 80 * math.sin(t / 30 * 2 * math.pi))
                for t in range(31)]
        stroke([(round(x), round(y)) for x, y in ring], 3)

        # --- the text tool -----------------------------------------------------------
        e16._text_box(paint, at(1300, 800))  # screen coordinates
        font_found = e16.current_font(e16._font_combo(paint))
        before = {(w.element_info.control_type, w.element_info.name, w.element_info.automation_id)
                  for w in e16.ui(paint).descendants()}
        controls = [e4.describe(w) for w in e16.ui(paint).descendants()
                    if w.element_info.control_type in ("Button", "ComboBox", "CheckBox", "RadioButton",
                                                       "ToggleButton", "MenuItem")
                    and (w.element_info.name or w.element_info.automation_id)]
        out["text toolbar controls"] = sorted({f"{c['type']}: {c['name'] or c['id']} "
                                               f"[{','.join(c['patterns'])}]"
                                               + (f" ({c['toggle_state']})" if 'toggle_state' in c else "")
                                               for c in controls
                                               if any(k in (c['name'] + c['id']).lower() for k in
                                                      ("font", "size", "bold", "fill", "background",
                                                       "transparent", "opaque", "text", "italic",
                                                       "underline", "strike"))})
        e16.type_text("18.11.62")
        e6.send_keys((0x08, False), (0x08, True), (0x08, False), (0x08, True))
        e16.type_text("26")
        time.sleep(0.3)
        e16._close_box(paint)
        e16.key("S")
        # a white rectangle with a thin outline (Rectangle shape, solid white fill)
        e15.select_brush(paint, "Brush")
        item = e16.ui(paint).child_window(title="Rectangle", control_type="ListItem").wrapper_object()
        e16.tap(item)
        e16.pick_colour(paint, "Light gray", slot=1)
        e16.pick_colour(paint, "White", slot=2)
        e16.set_menu(paint, "Shape fill", "Solid fill")
        stroke([(1650, 780), (1750, 830), (1850, 880)], 8)
        time.sleep(0.3)
        e16.key("S")
        e16.set_menu(paint, "Shape fill", "No fill")

        # --- the flood -----------------------------------------------------------------
        e16.key("B")  # Fill with color
        e16.pick_colour(paint, "Light turquoise")
        time.sleep(0.3)
        tap = at(cx, cy + 10)
        e15.stroke(pen, [tap, tap], 2)
        time.sleep(1.0)
        e16.key("S")
        time.sleep(0.4)
        img = grab((ox, oy), (0, 0, CANVAS[0], CANVAS[1]))
        if a.snapshot:
            img.save(a.snapshot)
        px = img.getpixel
        out["flood"] = {
            "inside the cloud": px((cx, cy + 10)),
            "sky far from the cloud (leaked)": px((100, 980)),
            "inside the closed rectangle": px((550, 775)),
            "inside the closed ring": px((600, 450)),
            "inside the outlined white box": px((1750, 830)),
        }
        # edge pixels: neither white, black, the fill colour, nor near-black stroke colours
        colours = img.crop((1300, 150, 1700, 380)).getcolors(1 << 16) or []
        halo = sum(n for n, c in colours if c not in ((255, 255, 255), TURQ) and max(c) > 60)
        out["flood"]["halo pixels around the cloud (400x230 area)"] = halo
        out["flood"]["text box area after the flood"] = px((1310, 830))
    finally:  # the font is only read, never changed, so nothing to restore
        pen.close()
        core.close(paint, discard=True)
    out["text font in use"] = font_found
    json.dump(out, sys.stdout, indent=2, default=str)
    print()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
