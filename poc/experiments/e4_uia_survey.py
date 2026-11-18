"""E4: what can UI Automation drive in Paint?

Opens maximised Paint and records, for each control the presenter might want,
its control type, AutomationId, name, class and the UIA patterns it supports.
Menus and drop-downs are opened through UIA ExpandCollapse and their items are
found by diffing the UIA tree before and after, then closed again. The Layers
panel is toggled open and shut. Nothing else is invoked: menu items are only
read, never clicked.

Privacy: the File menu can list recently opened files. Item names that look
like a file name or path are replaced by "[file]", and no File submenu is
opened. Paint's account button is redacted by paintctl.

Run from poc/paintctl, so paintctl's venv is used:

    uv run python ../experiments/e4_uia_survey.py [--monitor \\\\.\\DISPLAY1]

UIA actions bring Paint to the front (E2). Prints JSON; progress on stderr.
"""
from __future__ import annotations

import argparse
import json
import re
import sys
import time

import e11_display_area as e11  # same folder; also sets DPI awareness
from paintctl import core

PATTERNS = {10000: "Invoke", 10001: "Selection", 10002: "Value", 10003: "RangeValue",
            10004: "Scroll", 10005: "ExpandCollapse", 10010: "SelectionItem",
            10015: "Toggle", 10018: "LegacyIAccessible"}
_FILEISH = re.compile(r"[\\/]|\.\w{2,4}$")


def patterns(wrapper) -> list[str]:
    el = wrapper.element_info.element
    out = []
    for pid, name in PATTERNS.items():
        try:
            if el.GetCurrentPattern(pid):
                out.append(name)
        except Exception:
            pass
    return out


def describe(wrapper, redact_files: bool = False) -> dict:
    ei = wrapper.element_info
    name = core._redact(ei.name or "")
    if redact_files and _FILEISH.search(name):
        name = "[file]"
    row = {"type": ei.control_type, "id": ei.automation_id or "", "name": name,
           "class": ei.class_name or "", "patterns": patterns(wrapper)}
    if "Toggle" in row["patterns"]:
        try:
            row["toggle_state"] = {0: "off", 1: "on", 2: "indeterminate"}.get(
                wrapper.iface_toggle.CurrentToggleState)
        except Exception:
            pass
    return row


def snapshot(win) -> dict:
    """Every element by a stable-ish key, so a before/after diff shows what opened."""
    out = {}
    for w in win.descendants():
        ei = w.element_info
        try:
            r = ei.rectangle
            key = (ei.control_type, ei.automation_id, ei.name, r.left, r.top)
        except Exception:
            continue
        out[key] = w
    return out


def opened_by(win, action, settle: float = 0.6, redact_files: bool = False) -> list[dict]:
    before = snapshot(win)
    action()
    time.sleep(settle)
    after = snapshot(win)
    new = [after[k] for k in after if k not in before]
    rows = [describe(w, redact_files) for w in new]
    # keep the interesting ones: drop bare images/text that only decorate items
    return [r for r in rows if r["type"] not in ("Image",) and (r["name"] or r["id"])]


def main() -> int:
    p = argparse.ArgumentParser(description=__doc__,
                                formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--monitor", default=r"\\.\DISPLAY1")
    a = p.parse_args()
    mon = next(m for m in e11.monitors() if m["device"] == a.monitor)
    out: dict = {}
    paint = e11.open_maximised(mon, 1600, 1000)
    try:
        win = core._uia_window(paint)
        core.focus(paint)

        # --- the menus -------------------------------------------------------------
        out["menus"] = {}
        for menu in ("File", "Edit", "View"):
            item = win.child_window(title=menu, control_type="MenuItem").wrapper_object()
            out["menus"][menu] = {"item": describe(item),
                                  "items": opened_by(win, item.expand,
                                                     redact_files=menu == "File")}
            if menu == "View":  # one level of submenus (View only: File may list files)
                for sub in [r for r in out["menus"][menu]["items"]
                            if "ExpandCollapse" in r["patterns"] and r["type"] == "MenuItem"]:
                    w = win.child_window(title=sub["name"], control_type="MenuItem").wrapper_object()
                    out["menus"][f"{menu} > {sub['name']}"] = {
                        "items": opened_by(win, w.expand)}
                    try:
                        w.collapse()
                    except Exception:
                        pass
            try:
                item.collapse()
            except Exception:
                pass
            time.sleep(0.4)
            print(f"menu {menu}: {len(out['menus'][menu]['items'])} items", file=sys.stderr, flush=True)

        # --- drop-downs on the toolbar ----------------------------------------------
        out["dropdowns"] = {}
        for label, crit in (("Select (selection options)", dict(title="Select", control_type="RadioButton")),
                            ("Rotate", dict(auto_id="RotateDropdown", control_type="Button")),
                            ("Flip", dict(auto_id="Flip", control_type="Button"))):
            w = win.child_window(**crit).wrapper_object()
            entry = {"control": describe(w)}
            try:
                entry["items"] = opened_by(win, w.expand)
                w.collapse()
            except Exception as exc:  # noqa: BLE001
                entry["error"] = f"{type(exc).__name__}: {exc}"
            out["dropdowns"][label] = entry
            time.sleep(0.4)
            print(f"dropdown {label}: {len(entry.get('items', []))} items", file=sys.stderr, flush=True)

        # --- the Layers panel -----------------------------------------------------------
        layers_group = win.child_window(title="Layers", control_type="Group").wrapper_object()
        toggle = [c for c in layers_group.children() if c.element_info.control_type == "Button"][0]
        entry = {"control": describe(toggle)}
        try:
            entry["panel"] = opened_by(win, toggle.toggle, settle=1.0)
            toggle.toggle()  # shut it again
        except Exception as exc:  # noqa: BLE001
            entry["error"] = f"{type(exc).__name__}: {exc}"
        out["layers"] = entry
        print(f"layers panel: {len(entry.get('panel', []))} elements", file=sys.stderr, flush=True)

        # --- zoom ------------------------------------------------------------------------
        slider = win.child_window(auto_id="ZoomSliderControl", control_type="Slider").wrapper_object()
        rv = slider.iface_range_value
        combo = win.child_window(auto_id="ZoomValuesComboBox", control_type="ComboBox").wrapper_object()
        out["zoom"] = {
            "slider": {**describe(slider), "min": rv.CurrentMinimum, "max": rv.CurrentMaximum,
                       "small_change": rv.CurrentSmallChange, "large_change": rv.CurrentLargeChange},
            "combo": describe(combo),
            "buttons": [describe(win.child_window(title=t, control_type="Button").wrapper_object())
                        for t in ("Fit to window", "Zoom in", "Zoom out")],
        }
        try:
            out["zoom"]["combo_values"] = [r["name"] for r in opened_by(win, combo.expand)
                                           if r["type"] == "ListItem"]
            combo.collapse()
        except Exception as exc:  # noqa: BLE001
            out["zoom"]["combo_error"] = str(exc)

        # --- everything with an AutomationId in the main tree -------------------------------
        out["with_automation_id"] = sorted(
            {(d["type"], d["id"], d["name"], ",".join(d["patterns"]))
             for d in (describe(w) for w in win.descendants()) if d["id"]})
    finally:
        core.close(paint, discard=True)
    json.dump(out, sys.stdout, indent=2, ensure_ascii=False)
    print()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
