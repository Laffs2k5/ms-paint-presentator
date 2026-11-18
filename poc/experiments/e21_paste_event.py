"""E21: tell that a paste has landed from a UI Automation event, not the screen.

Each screen read waits for a display refresh (E6), and E13 showed how reading
many pixels slows the show. Paint enables its Crop button while a selection
exists, so a paste turns it on and the `S` commit turns it off again.
poc/dotnet/E21Watch (C#, FlaUI) subscribes to that button's IsEnabled and
prints every change with the same clock as perf_counter.

Per slide (1854x875, a new PNG clipboard object as in the POC): Ctrl+V, poll
one probe pixel until the slide's colour shows, wait, `S`. Compared: Ctrl+V to
pixel, Ctrl+V to "Crop enabled", their difference, and `S` to "Crop
disabled". Also whether subscribing takes the focus from Paint (UIA actions
do, E2).

Build the C# side first (dotnet build -c Release poc/dotnet/E21Watch). Run
from poc/paintctl:

    uv run python ../experiments/e21_paste_event.py [--n 30]

Hands off while it runs (under a minute). Prints JSON; progress on stderr.
"""
from __future__ import annotations

import argparse
import json
import queue
import statistics
import struct
import subprocess
import sys
import threading
import time
from pathlib import Path

import e1_formats as e1
import e6_paste_speed as e6
import e8_clipboard_history as e8
import e11_display_area as e11
import e20_csharp_clipboard as e20  # slide(): a slide with a probe square
import win32gui
from paintctl import core

EXE = Path(__file__).resolve().parents[1] / "dotnet/E21Watch/bin/Release/net9.0-windows/E21Watch.exe"


# (name, source, is "on", is "off") for each candidate signal
SIGNALS = [
    ("UIA event: Crop enabled", "uia", lambda d: d.startswith("CropButton=True"),
     lambda d: d.startswith("CropButton=False")),
    ("UIA poll: Crop enabled", "poll", lambda d: d == "True", lambda d: d == "False"),
    ("WinEvent: popup shown", "winevent", lambda d: d.startswith("show:"),
     lambda d: d.startswith("hide:")),
]


def stats(v):
    v = [x for x in v if x is not None]
    return {"median": round(statistics.median(v), 1), "min": round(min(v), 1),
            "max": round(max(v), 1), "n": len(v)} if v else None


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--monitor", default=r"\\.\DISPLAY1")
    ap.add_argument("--n", type=int, default=30)
    ap.add_argument("--dump", type=Path, help="write each paste's times and all raw signals here")
    a = ap.parse_args()
    if not EXE.exists():
        raise SystemExit(f"build the C# side first: dotnet build -c Release {EXE.parent.parents[2]}")
    mon = next(m for m in e11.monitors() if m["device"] == a.monitor)
    paint = e11.open_maximised(mon, *e20.SIZE)
    screen = e6.ScreenProbe()
    clip = e1.MultiFormatClipboard()
    events: list[tuple[str, str, float]] = []  # (source, detail, t)
    ready: queue.Queue = queue.Queue()
    w = None
    out: dict = {}
    try:
        core.focus(paint)
        e6.send_keys(*e6.KEY_S)
        time.sleep(0.3)
        w = subprocess.Popen([str(EXE), str(paint)], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                             text=True, bufsize=1)

        def read():
            for line in w.stdout:
                parts = line.split()
                if parts[0] == "ready":
                    ready.put(parts[1])
                elif len(parts) == 3:
                    events.append((parts[0], parts[1], float(parts[2])))

        threading.Thread(target=read, daemon=True).start()
        out["Crop enabled at start"] = ready.get(timeout=30)
        time.sleep(0.5)
        out["Paint still in front after subscribing"] = win32gui.GetForegroundWindow() == paint
        c = core.info(paint)["canvas_on_screen"]
        probe = (c["left"] + e20.SIZE[0] - 200, c["top"] + e20.SIZE[1] - 200)
        rows = []
        for i in range(a.n):
            img, rgb = e20.slide(7000 + i)
            clip.offer([(e1.CF_PNG, e1.enc_png(img)), (e8.EXCLUDE, struct.pack("<I", 0))])
            time.sleep(0.1)
            t0 = time.perf_counter()
            e6.send_keys(*e6.CTRL_V)
            t_px = None
            while time.perf_counter() - t0 < 3:
                if screen.pixel(*probe) == rgb:
                    t_px = time.perf_counter()
                    break
            time.sleep(0.25)
            t_s = time.perf_counter()
            e6.send_keys(*e6.KEY_S)
            time.sleep(0.5)
            row = {"pixel": (t_px - t0) * 1000 if t_px else None}
            for name, src, on_test, off_test in SIGNALS:
                on = next((t for so, d, t in events if so == src and on_test(d) and t0 < t < t_s), None)
                off = next((t for so, d, t in events if so == src and off_test(d) and t > t_s), None)
                row[name] = ((on - t0) * 1000 if on else None,
                             (on - t_px) * 1000 if on and t_px else None,
                             (off - t_s) * 1000 if off else None)
            row["times"] = (t0, t_px, t_s)
            rows.append(row)
        out["Ctrl+V to slide pixel ms"] = stats([r["pixel"] for r in rows])
        for name, *_ in SIGNALS:
            out[name] = {"Ctrl+V to signal ms": stats([r[name][0] for r in rows]),
                         "signal minus pixel ms": stats([r[name][1] for r in rows]),
                         "S to off signal ms": stats([r[name][2] for r in rows]),
                         "pastes without it": sum(1 for r in rows if r[name][0] is None),
                         "commits without the off signal": sum(1 for r in rows if r[name][2] is None)}
        if a.dump:
            a.dump.write_text(json.dumps({"pastes": [r["times"] for r in rows], "events": events}))
        out["poll cost per IsEnabled read"] = sorted({d for so, d, _ in events if so == "pollcost"})[-3:]
        out["distinct signals seen"] = sorted({f"{so} {d}" for so, d, _ in events if so != "pollcost"})[:40]
    finally:
        if w:
            w.stdin.close()
            w.wait(5)
        clip.stop()
        screen.close()
        core.close(paint, discard=True)
    json.dump(out, sys.stdout, indent=2)
    print()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
