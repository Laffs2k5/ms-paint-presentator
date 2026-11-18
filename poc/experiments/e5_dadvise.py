"""E5: does anything call DAdvise on our clipboard data object?

IDataObject::DAdvise lets a consumer subscribe to data-change notifications.
If Paint used it, a presenter could perhaps signal "new slide" that way. Our
data object (E1) logs every IDataObject call. This script runs it through the
phases of a presentation and reports every call per phase, with DAdvise
refused (OLE_E_ADVISENOTSUPPORTED, as in E1-E8) and accepted (the subscriber
is kept and a connection id returned; nothing is ever sent to it).

Phases: slide offered (other processes look), pasted into Paint (Ctrl+V, `S`),
Paint loses focus to another window and gets it back, pasted again.

Variants: refuse + opt-out, accept + opt-out, accept without opt-out. The last
lets the clipboard history service in (E8) and so adds one test slide to the
user's Win+V history; it is left there (see E8 on deleting).

Run from poc/paintctl, so paintctl's venv is used:

    uv run python ../experiments/e5_dadvise.py [--monitor \\\\.\\DISPLAY1]

Hands off while it runs (under a minute). Prints JSON; progress on stderr.
"""
from __future__ import annotations

import argparse
import json
import struct
import sys
import time
from collections import Counter

import e1_formats as e1  # same folder: the logging OLE data object
import e2_background_paste as e2  # stand-in window
import e6_paste_speed as e6
import e8_clipboard_history as e8
import e11_display_area as e11
from paintctl import core

VARIANTS = {
    "DAdvise refused, history opt-out": dict(accept=False, optout=True),
    "DAdvise accepted, history opt-out": dict(accept=True, optout=True),
    "DAdvise accepted, no opt-out": dict(accept=True, optout=False),
}


def main() -> int:
    p = argparse.ArgumentParser(description=__doc__,
                                formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--monitor", default=r"\\.\DISPLAY1")
    a = p.parse_args()
    w, h = 1600, 1000
    mon = next(m for m in e11.monitors() if m["device"] == a.monitor)
    base = e11.test_pattern(w, h)
    clip = e1.MultiFormatClipboard()
    screen = e6.ScreenProbe()
    out: dict = {"variants": {}}
    paint = standin = None
    try:
        paint = e11.open_maximised(mon, w, h)
        info = core.info(paint)
        c, vp = info["canvas_on_screen"], info["viewport"]
        probe = (c["left"] + e6.probe_points(w, h)[0][0] + e6._PROBE // 2,
                 c["top"] + e6.probe_points(w, h)[0][1] + e6._PROBE // 2)
        standin = e2.StandIn(mon["work"].left + 200, mon["work"].top + 20, 900, 160)
        core.focus(paint)
        for i, (name, v) in enumerate(VARIANTS.items()):
            colour = e6.colour(800 + i)
            formats = [(e1.CF_DIB, e1.enc_dib24(e1.solid_probe(base, colour)))]
            if v["optout"]:
                formats.append((e8.EXCLUDE, struct.pack("<I", 0)))
            phases = []

            def mark(label):
                phases.append((label, len(clip.log)))

            clip.offer(formats, accept_advise=v["accept"])
            mark("offered")
            time.sleep(3)
            mark("pasted")
            ms, dialog = e1.paste_and_commit(paint, screen, [probe], colour)
            time.sleep(1)
            mark("focus away and back")
            core.focus(standin.hwnd)
            time.sleep(1)
            core.focus(paint)
            time.sleep(1)
            mark("pasted again")
            e1.paste_and_commit(paint, screen, [probe], colour)
            time.sleep(1)
            mark("end")
            log = list(clip.log)
            per_phase = {}
            for (label, start), (_, end) in zip(phases, phases[1:]):
                per_phase[label] = dict(Counter(
                    f"{x['call']} {x['format'] or ''}".strip() for x in log[start:end]))
            advises = [{"format": x["format"], "advf": x.get("advf")}
                       for x in log if x["call"] == "DAdvise"]
            out["variants"][name] = {"paste_visible_ms": ms, "error_dialog": dialog,
                                     "dadvise_calls": advises, "calls_per_phase": per_phase}
            print(f"{name}: DAdvise calls {advises}; per phase {per_phase}",
                  file=sys.stderr, flush=True)
    finally:
        clip.stop()
        screen.close()
        if standin:
            standin.close()
        if paint:
            core.dismiss_clipboard_error(paint, 0.5)
            core.close(paint, discard=True)
    json.dump(out, sys.stdout, indent=2)
    print()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
