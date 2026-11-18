"""E16: can a script pick Paint's tools, colours and sizes, and does Paint keep them?

For the loading painting (E15 draws with the synthetic pen). Paint maximised,
in front; UI Automation (UIA) and keys select things, the pen draws, and each
choice is checked by what Paint reports and by what lands on the canvas:

- tools: single-letter keys, and UIA Toggle on the Tools buttons; the canvas's
  UIA name says which tool is active ("Using Pencil tool on Canvas").
- brushes: invoking the Brushes menu's items (E15).
- palette: UIA SelectionItem on each of the 20 colours, into Colour 1 and
  Colour 2; each one drawn with the Pencil and its RGB read back.
- size: the drawing tools' Size slider (UIA RangeValue), stroke width measured.
- shapes: select a shape, set outline and fill styles, drag it with the pen,
  commit with `S`.
- text: the Text tool, a box dragged with the pen, a font chosen, text typed as
  Unicode key events, committed by a pen tap outside the box or by a tool
  change through UIA.
- persistence: what Paint remembers after close and relaunch. Tool, colours,
  sizes and shape styles reset, but the Text tool's font is kept. The font
  found at the start is put back (even if the run fails), and everything is
  checked after one more relaunch.

Not used: Edit colors. It opens a dialog inside Paint's window; invoking the
button through UIA blocks until the dialog closes, and walking Paint's UIA tree
hangs while it is open. The classic palette is enough for a crappy painting.

Run from poc/paintctl, so paintctl's venv is used:

    uv run python ../experiments/e16_tools.py [--monitor \\\\.\\DISPLAY1]

Hands off the mouse and keyboard while it runs (about three minutes). Prints
JSON; progress on stderr.
"""
from __future__ import annotations

import argparse
import ctypes
import json
import sys
import time

import e6_paste_speed as e6  # send_keys, ScreenProbe
import e11_display_area as e11  # also sets DPI awareness
import e15_draw as e15  # Pen, Board, stroke, grab, active_tool, select_brush
from paintctl import core
from PIL import ImageGrab

CANVAS = (2000, 1000)
KEYS = "PBTEIZS"  # single-letter tool shortcuts to try (E14 lists some)
TOOLS = ("Pencil", "Fill", "Text", "Eraser", "Color picker", "Magnifier")
CANVAS_NAME = {"Fill": "Fill with color", "Color picker": "Pick color"}  # canvas says
BRUSHES = ("Brush", "Calligraphy brush", "Calligraphy pen", "Airbrush", "Oil brush",
           "Crayon", "Marker", "Natural pencil", "Watercolor brush")
PALETTE = ("Black", "Gray", "Dark red", "Red", "Orange", "Yellow", "Green", "Turquoise",
           "Indigo", "Purple", "White", "Light gray", "Brown", "Rose", "Gold",
           "Light yellow", "Lime", "Light turquoise", "Blue-gray", "Lavender")
SHAPES = ("Line", "Oval", "Rectangle", "Five-point star", "Heart")


# --- UIA helpers ------------------------------------------------------------------------------
PEN: e15.Pen | None = None  # set in main


def ui(paint: int):
    return core._uia_window(paint)


def tap(wrapper) -> None:
    """A synthetic pen tap on a control's centre: clicks without moving the mouse."""
    r = wrapper.rectangle()
    x, y = (r.left + r.right) // 2, (r.top + r.bottom) // 2
    e15.stroke(PEN, [(x, y), (x, y)], 2)
    time.sleep(0.3)


def choose(wrapper, action, done) -> str:
    """Try a UIA action; if it fails or done() is still false, a pen tap.
    Returns what worked."""
    try:
        action(wrapper)
        time.sleep(0.3)
        if done():
            return "uia"
    except Exception:  # noqa: BLE001 - e.g. the pattern is not supported
        pass
    tap(wrapper)
    return "pen tap" if done() else "neither"


def key(ch: str) -> None:
    e6.send_keys((ord(ch), False), (ord(ch), True))
    time.sleep(0.3)


def type_text(text: str) -> None:
    """Characters as Unicode key events (KEYEVENTF_UNICODE), one SendInput call."""
    evs = []
    for ch in text:
        for up in (False, True):
            ev = e6._INPUT()
            ev.type = 1  # INPUT_KEYBOARD
            ev.u.ki = e6._KEYBDINPUT(0, ord(ch), 0x0004 | (0x0002 if up else 0), 0, 0)
            evs.append(ev)
    arr = (e6._INPUT * len(evs))(*evs)
    e6._user32.SendInput(len(evs), arr, ctypes.sizeof(e6._INPUT))
    time.sleep(0.3)


def colour_slot(paint: int, slot: int):
    return ui(paint).child_window(title_re=f"Color {slot}:.*", control_type="RadioButton") \
        .wrapper_object()


def colours(paint: int) -> dict:
    return {f"colour{s}": colour_slot(paint, s).element_info.name.split(": ", 1)[1]
            for s in (1, 2)}


PICKED_BY: dict[str, int] = {}


def pick_colour(paint: int, name: str, slot: int = 1) -> str:
    colour_slot(paint, slot).select()
    time.sleep(0.2)
    try:
        item = ui(paint).child_window(title=name, control_type="ListItem").wrapper_object()
        how = choose(item, lambda w: w.select(),
                     lambda: colour_slot(paint, slot).element_info.name.endswith(": " + name))
        PICKED_BY[how] = PICKED_BY.get(how, 0) + 1
        return colour_slot(paint, slot).element_info.name
    finally:
        if slot != 1:
            colour_slot(paint, 1).select()  # draw with colour 1 again
            time.sleep(0.2)


def slider(paint: int, name: str):
    """The drawing tools' Size / Opacity sliders (only there while one is active)."""
    return ui(paint).child_window(title=name, control_type="Slider").wrapper_object() \
        .iface_range_value


def menu_state(paint: int, button: str) -> str | None:
    """Which item of a drop-down menu (Shape outline / Shape fill) is toggled on."""
    win = ui(paint)
    b = win.child_window(title=button, control_type="Button").wrapper_object()
    b.expand()
    time.sleep(0.5)
    on = None
    try:
        for item in win.descendants(control_type="MenuItem"):
            try:
                if item.iface_toggle.CurrentToggleState == 1:
                    on = item.element_info.name
            except Exception:  # noqa: BLE001 - items without Toggle
                pass
    finally:
        b.collapse()
        time.sleep(0.3)
    return on


def set_menu(paint: int, button: str, item: str) -> str | None:
    win = ui(paint)
    b = win.child_window(title=button, control_type="Button").wrapper_object()
    b.expand()
    time.sleep(0.5)
    win.child_window(title=item, control_type="MenuItem").wrapper_object().invoke()
    time.sleep(0.4)
    return menu_state(paint, button)


def _text_box(paint: int, xy: tuple[int, int]) -> None:
    key("T")
    x0, y0 = xy
    e15.stroke(PEN, [(x0, y0), (x0 + 110, y0 + 40), (x0 + 220, y0 + 80)], 8)
    time.sleep(0.8)


def _close_box(paint: int) -> None:
    """Close the box by choosing another tool through UIA. A pen tap outside
    also commits, but if the font list is still open the tap only closes the
    list, and with the Text tool a tap on empty canvas opens a new box."""
    ui(paint).child_window(title="Pencil", control_type="Button").wrapper_object().toggle()
    time.sleep(0.5)


def _font_combo(paint: int):
    return ui(paint).child_window(auto_id="FontComboBox", control_type="ComboBox",
                                  found_index=0).wrapper_object()


def current_font(combo) -> str | None:
    try:
        sel = combo.iface_selection.GetCurrentSelection()
        return ", ".join(sel.GetElement(i).CurrentName for i in range(sel.Length)) or None
    except Exception:  # noqa: BLE001
        return None


def set_font(paint: int, font: str) -> str | None:
    """With a text box open, pick the font from the open list by its exact name
    and check. Typing the name and Enter is not reliable: the box completes
    "Calibri" to "Calibri Light" in some runs. The list can hold several items
    of one name, so try each. The box can report the old font for a moment
    after a change; a new text box (or a relaunch) shows the real one."""
    combo = _font_combo(paint)
    for _ in range(3):
        combo.expand()
        time.sleep(0.6)
        items = [w for w in ui(paint).descendants(control_type="ListItem")
                 if w.element_info.name == font]
        for item in items:
            try:
                item.iface_scroll_item.ScrollIntoView()
                item.select()
            except Exception:  # noqa: BLE001
                continue
            time.sleep(0.4)
            if current_font(combo) == font:
                break
        if current_font(combo) != font and not items:
            # Not realised in the (virtualised) list: type it. Delete drops an
            # autocompleted suffix (" Light") before Enter accepts the name.
            type_text(font)
            e6.send_keys((0x2E, False), (0x2E, True))  # VK_DELETE
            type_text(chr(13))
            time.sleep(0.4)
        try:
            combo.collapse()
        except Exception:  # noqa: BLE001
            pass
        time.sleep(0.3)
        if current_font(combo) == font:
            return font
    return current_font(combo)


def text_font(paint: int, xy, new: str | None = None) -> dict:
    """Paint remembers the Text tool's font across sessions, so read it (and
    optionally change it) in an empty text box, then close the box."""
    _text_box(paint, xy)
    out = {"font": current_font(_font_combo(paint))}
    if new:
        out["set_to"] = set_font(paint, new)
    _close_box(paint)
    return out


def state(paint: int) -> dict:
    """What Paint shows now. Reads the tool first: reading the Pencil's size
    selects the Pencil."""
    s = {"tool": e15.active_tool(paint)}
    s.update(colours(paint))
    s["shape_outline"] = menu_state(paint, "Shape outline")
    s["shape_fill"] = menu_state(paint, "Shape fill")
    key("P")
    rv = slider(paint, "Size")
    s["pencil_size"] = rv.CurrentValue
    s["pencil_opacity"] = slider(paint, "Opacity").CurrentValue
    return s


# --- canvas checks ---------------------------------------------------------------------------
def stroke_width(x: int, y: int, top: int, bottom: int) -> int:
    """The run of ink rows through (x, y) in a 1 px column between top and bottom."""
    col = ImageGrab.grab(bbox=(x, top, x + 1, bottom), all_screens=True).convert("L").tobytes()
    near = [j for j in range(y - top - 3, y - top + 4) if 0 <= j < len(col) and col[j] < 250]
    if not near:
        return 0
    i = near[0]
    lo = hi = i
    while lo > 0 and col[lo - 1] < 250:
        lo -= 1
    while hi < len(col) - 1 and col[hi + 1] < 250:
        hi += 1
    return hi - lo + 1


def ink_box(box) -> list | None:
    img = ImageGrab.grab(bbox=box, all_screens=True).convert("L").point(lambda v: 255 if v < 250 else 0)
    b = img.getbbox()
    return [box[0] + b[0], box[1] + b[1], box[0] + b[2], box[1] + b[3]] if b else None


# --- the tests ---------------------------------------------------------------------------------
def test_keys(paint: int) -> dict:
    out = {}
    for ch in KEYS:
        key(ch)
        out[ch] = e15.active_tool(paint)
    return out


def test_tool_buttons(paint: int) -> dict:
    out = {}
    for name in TOOLS:
        b = ui(paint).child_window(title=name, control_type="Button").wrapper_object()
        want = CANVAS_NAME.get(name, name).lower()
        how = choose(b, lambda w: w.toggle(),
                     lambda: f" {want} " in f" {e15.active_tool(paint).lower()} ")
        out[name] = {"tool": e15.active_tool(paint), "by": how}
    key("S")
    return out


def test_brushes(paint: int) -> dict:
    return {name: e15.select_brush(paint, name) for name in BRUSHES}


def test_size(paint: int, board, pen) -> dict:
    key("P")
    rv = slider(paint, "Size")
    out = {"pencil_size_range": [rv.CurrentMinimum, rv.CurrentMaximum],
           "opacity_range": [slider(paint, "Opacity").CurrentMinimum,
                             slider(paint, "Opacity").CurrentMaximum],
           "width_by_size": {}}
    top, bottom = board.top + 2, board.top + CANVAS[1] - 2
    for size in (rv.CurrentMinimum, 5, 20, 45, rv.CurrentMaximum):
        cx, cy, box = board.cell()
        rv.SetValue(size)
        time.sleep(0.3)
        e15.stroke(pen, [(box[0] + 20 + 10 * k, cy) for k in range(21)], 2)
        time.sleep(0.3)
        out["width_by_size"][str(size)] = {"slider_reads": rv.CurrentValue,
                                           "width_px": stroke_width(cx, cy, top, bottom)}
    return out


def test_palette(paint: int, board, pen, probe) -> dict:
    key("P")
    slider(paint, "Size").SetValue(10)
    time.sleep(0.3)
    out = {}
    cells = [board.cell(), board.cell()]
    for i, name in enumerate(PALETTE):
        _, _, box = cells[i // 10]
        x = box[0] + 25 + (i % 10 % 5) * 45
        y = box[1] + 40 + (i % 10 // 5) * 60
        reported = pick_colour(paint, name)
        e15.stroke(pen, [(x, y), (x + 15, y), (x + 30, y)], 2)
        time.sleep(0.25)
        out[name] = {"colour1": reported.split(": ", 1)[1], "rgb": probe.pixel(x + 15, y)}
    return out


def test_shapes(paint: int, board, pen, probe) -> dict:
    out = {}
    pick_colour(paint, "Yellow", slot=2)
    for name in SHAPES:
        _, _, box = board.cell()
        item = ui(paint).child_window(title=name, control_type="ListItem").wrapper_object()
        how = choose(item, lambda w: w.select(),
                     lambda: name.lower() in e15.active_tool(paint).lower())
        if how == "neither":  # the toggle button inside the list item
            btn = ui(paint).child_window(title=name, control_type="Button").wrapper_object()
            how = "button " + choose(btn, lambda w: w.toggle(),
                                     lambda: name.lower() in e15.active_tool(paint).lower())
        res = {"tool": e15.active_tool(paint), "by": how}
        if name == "Rectangle":
            res["outline"] = set_menu(paint, "Shape outline", "Crayon")
            res["fill"] = set_menu(paint, "Shape fill", "Solid fill")
        x0, y0, x1, y1 = box[0] + 40, box[1] + 40, box[2] - 40, box[3] - 40
        drag = [(round(x0 + (x1 - x0) * k / 12), round(y0 + (y1 - y0) * k / 12)) for k in range(13)]
        e15.stroke(pen, drag, 8)
        time.sleep(0.4)
        before = ink_box(box)
        key("S")
        time.sleep(0.4)
        after, later = ink_box(box), None
        time.sleep(0.6)
        later = ink_box(box)
        res.update({"drag": [x0, y0, x1, y1], "ink_before_commit": before,
                    "ink_after_commit": after, "steady_after_commit": after == later,
                    "centre_rgb": probe.pixel((x0 + x1) // 2, (y0 + y1) // 2)})
        if name == "Rectangle":
            set_menu(paint, "Shape outline", "Solid outline")
            set_menu(paint, "Shape fill", "No fill")
        out[name] = res
    pick_colour(paint, "White", slot=2)
    return out


def test_text(paint: int, board, pen) -> dict:
    """Two text boxes: Comic Sans MS committed by a pen tap outside the box, and
    the default font committed by choosing another tool through UIA."""
    out = {}
    for label, font, commit in (("tap outside", "Comic Sans MS", "tap"),
                                ("tool change through UIA", None, "uia")):
        _, _, box = board.cell()
        key("T")
        x0, y0 = box[0] + 10, box[1] + 60
        e15.stroke(pen, [(x0, y0), (x0 + 110, y0 + 40), (x0 + 220, y0 + 80)], 8)
        time.sleep(0.8)
        res = {"tool": e15.active_tool(paint)}
        if font:
            res["font_reported"] = set_font(paint, font)
            # back into the text box to type
            tap_xy = (x0 + 20, y0 + 20)
            e15.stroke(pen, [tap_xy, tap_xy], 2)
            time.sleep(0.4)
        type_text("Hello Paint")
        res["ink_while_editing"] = ink_box(box)
        if commit == "tap":
            e15.stroke(pen, [(box[0] + 200, box[3] - 10), (box[0] + 200, box[3] - 10)], 2)
            time.sleep(0.6)
            key("S")
        else:
            ui(paint).child_window(title="Pencil", control_type="Button").wrapper_object().toggle()
            time.sleep(0.6)
            res["tool_after_commit"] = e15.active_tool(paint)
            key("S")
        time.sleep(0.4)
        res["ink_after_commit"] = ink_box(box)
        out[label] = res
    return out


def font_spot(paint: int) -> tuple[int, int]:
    """Where to open an empty text box: the canvas's bottom right, away from
    the test cells."""
    c = core.info(paint)["canvas_on_screen"]
    return c["left"] + 1500, c["top"] + 780


def restore(paint: int, found: dict) -> None:
    """Put back the settings the run changed, as found at the start."""
    if found.get("font"):
        text_font(paint, font_spot(paint), new=found["font"])
    set_menu(paint, "Shape outline", found["shape_outline"])
    set_menu(paint, "Shape fill", found["shape_fill"])
    key("P")
    slider(paint, "Size").SetValue(found["pencil_size"])
    slider(paint, "Opacity").SetValue(found["pencil_opacity"])
    pick_colour(paint, found["colour2"], slot=2)
    pick_colour(paint, found["colour1"], slot=1)
    tool = found["tool"].removeprefix("Using ").removesuffix(" tool on Canvas")
    if tool in BRUSHES:
        e15.select_brush(paint, tool)
    elif tool == "Select":
        key("S")


def main() -> int:
    p = argparse.ArgumentParser(description=__doc__,
                                formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--monitor", default=r"\\.\DISPLAY1")
    a = p.parse_args()
    mon = next(m for m in e11.monitors() if m["device"] == a.monitor)
    global PEN
    out: dict = {}
    pen = PEN = e15.Pen()
    probe = e6.ScreenProbe()
    paint = None
    found: dict = {}
    restored = False

    def log(name, fn):
        try:
            res = fn()
        except Exception as exc:  # noqa: BLE001 - record and go on
            res = {"error": f"{type(exc).__name__}: {exc}"}
        out[name] = res
        print(f"{name}: {json.dumps(res)}", file=sys.stderr, flush=True)
        return res

    try:
        paint = e11.open_maximised(mon, *CANVAS)
        core.focus(paint)
        found.update(log("found at launch", lambda: state(paint)))
        found["font"] = text_font(paint, font_spot(paint))["font"]
        out["found at launch"] = found
        board = e15.Board(paint)
        log("keys", lambda: test_keys(paint))
        log("tool buttons", lambda: test_tool_buttons(paint))
        log("brushes", lambda: test_brushes(paint))
        log("palette", lambda: test_palette(paint, board, pen, probe))
        log("shapes", lambda: test_shapes(paint, board, pen, probe))
        log("text", lambda: test_text(paint, board, pen))
        log("size", lambda: test_size(paint, board, pen))  # last: wide strokes

        # persistence: a distinctive state, then close and relaunch
        def distinctive():
            set_menu(paint, "Shape outline", "Crayon")
            set_menu(paint, "Shape fill", "Solid fill")
            key("P")
            slider(paint, "Size").SetValue(12)
            pick_colour(paint, "Yellow", slot=2)
            pick_colour(paint, "Red", slot=1)
            s = state(paint)  # selects the Pencil, so the brush goes last
            s["tool"] = e15.select_brush(paint, "Oil brush")
            return s
        log("set before relaunch", distinctive)
        core.close(paint, discard=True)
        paint = e11.open_maximised(mon, *CANVAS)
        core.focus(paint)
        log("after relaunch", lambda: state(paint) | text_font(paint, font_spot(paint)))
        log("restore", lambda: restore(paint, found) or state(paint))
        restore(paint, found)  # state() selected the Pencil again
        core.close(paint, discard=True)
        paint = e11.open_maximised(mon, *CANVAS)
        core.focus(paint)
        log("after restore and relaunch",
            lambda: state(paint) | text_font(paint, font_spot(paint)))
        restore(paint, found)
        restored = True
    finally:
        if paint:
            core.close(paint, discard=True)
        if found.get("font") and not restored:  # the font outlives the session
            try:
                paint = e11.open_maximised(mon, *CANVAS)
                core.focus(paint)
                text_font(paint, font_spot(paint), new=found["font"])
            finally:
                core.close(paint, discard=True)
        pen.close()
        probe.close()
    out["colours picked by"] = PICKED_BY
    json.dump(out, sys.stdout, indent=2)
    print()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
