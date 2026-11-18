"""E20: does a C# live clipboard object serve Paint like the Python one?

The presenter will be C# (research 05). Its clipboard object must do what
DoomPaint's (Python) does: serve PNG slides to Paint from a live OLE data
object, a new object per slide (E7), with the clipboard-history opt-out (E8).
This harness drives poc/dotnet/E20Clipboard (C#) over stdin and compares:

- python:   E1's data object (the POC's, as a baseline)
- winforms: WinForms' DataObject with Clipboard.SetDataObject(obj, copy: false)
- own:      our own IDataObject (built-in COM interop) with OleSetClipboard

For each slide: offer it, send Ctrl+V, wait for its probe colour on screen,
commit with `S`. Recorded: offer time, Ctrl+V to Paint's first GetData of the
PNG (from the C# log, same clock), Ctrl+V to on screen, misses (Paint's error
dialog is closed and counted), and whether Win+V history gained anything.

Build the C# side first:

    dotnet build -c Release poc/dotnet/E20Clipboard

Run from poc/paintctl, so paintctl's venv is used:

    uv run python ../experiments/e20_csharp_clipboard.py [--n 200] [--variants python,winforms,own]

Hands off while it runs (a few minutes). Prints JSON; progress on stderr.
"""
from __future__ import annotations

import argparse
import json
import queue
import statistics
import struct
import subprocess
import sys
import tempfile
import threading
import time
from pathlib import Path

import e1_formats as e1
import e6_paste_speed as e6
import e8_clipboard_history as e8
import e11_display_area as e11
from paintctl import core
from PIL import Image, ImageDraw

EXE = Path(__file__).resolve().parents[1] / "dotnet/E20Clipboard/bin/Release/net9.0-windows/E20Clipboard.exe"
SIZE = (1854, 875)  # the 1920x1200 projector's slide (E11)


class CSharpClipboard:
    def __init__(self):
        self.p = subprocess.Popen([str(EXE)], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                  text=True, bufsize=1)
        self.replies: queue.Queue = queue.Queue()
        self.calls: list[tuple[str, str, float]] = []  # (method, format, t)
        threading.Thread(target=self._read, daemon=True).start()

    def _read(self):
        for line in self.p.stdout:
            parts = line.split()
            if parts and parts[0] == "call":
                self.calls.append((parts[2], parts[3], float(parts[4])))
            else:
                self.replies.put(line.strip())

    def offer(self, variant: str, png: Path) -> float:
        self.calls.clear()
        self.p.stdin.write(f"offer {variant} {png}\n")
        self.p.stdin.flush()
        reply = self.replies.get(timeout=10)
        if not reply.startswith("ok"):
            raise RuntimeError(reply)
        return float(reply.split()[1])

    def close(self):
        try:
            self.p.stdin.write("quit\n")
            self.p.stdin.flush()
            self.p.wait(5)
        except Exception:  # noqa: BLE001
            self.p.kill()


def slide(k: int) -> tuple[Image.Image, tuple[int, int, int]]:
    """A light slide with a few shapes and a probe square in a colour unique to k."""
    w, h = SIZE
    img = Image.new("RGB", SIZE, (245, 245, 240))
    d = ImageDraw.Draw(img)
    d.rectangle((80, 60, w - 80, 140), fill=(40, 60, 120))
    for i in range(5):
        d.rectangle((120, 220 + i * 100, 120 + 300 + (k * 37 + i * 91) % 900, 260 + i * 100),
                    fill=(60 + 30 * i, 120, 160))
    rgb = e6.colour(k)
    d.rectangle((w - 300, h - 300, w - 100, h - 100), fill=rgb)
    return img, rgb


def run_variant(variant: str, n: int, paint: int, screen, tmp: Path) -> dict:
    c = core.info(paint)["canvas_on_screen"]
    probe = (c["left"] + SIZE[0] - 200, c["top"] + SIZE[1] - 200)
    cs = CSharpClipboard() if variant != "python" else None
    py = e1.MultiFormatClipboard() if variant == "python" else None
    offer_ms, getdata_ms, visible_ms, misses, dialogs = [], [], [], 0, 0
    try:
        for i in range(n):
            img, rgb = slide(1000 * len(variant) + i)
            png = e1.enc_png(img)
            if cs:
                path = tmp / "slide.png"
                path.write_bytes(png)
                offer_ms.append(round(cs.offer(variant, path) * 1000, 2))
            else:
                t = time.perf_counter()
                py.offer([(e1.CF_PNG, png), (e8.EXCLUDE, struct.pack("<I", 0))])
                offer_ms.append(round((time.perf_counter() - t) * 1000, 2))
            time.sleep(0.05)
            t0 = time.perf_counter()
            e6.send_keys(*e6.CTRL_V)
            seen = None
            while time.perf_counter() - t0 < 3:
                if screen.pixel(*probe) == rgb:
                    seen = time.perf_counter()
                    break
            if seen is None:
                misses += 1
                if core.dismiss_clipboard_error(paint, 1.0):
                    dialogs += 1
            else:
                visible_ms.append(round((seen - t0) * 1000, 1))
            calls = cs.calls if cs else [(x["call"], x["format"] or "", x["t"]) for x in py.log]
            first = next((t for m, f, t in calls if m == "GetData" and f == "PNG"), None)
            if first is not None:
                getdata_ms.append(round((first - t0) * 1000, 2))
            time.sleep(0.15)
            e6.send_keys(*e6.KEY_S)
            time.sleep(0.15)
            if (i + 1) % 50 == 0:
                print(f"  {variant}: {i + 1}/{n}", file=sys.stderr, flush=True)
        last_calls = sorted({(m, f) for m, f, _ in (cs.calls if cs else
                             [(x["call"], x["format"] or "", x["t"]) for x in py.log])})
    finally:
        if cs:
            cs.close()
        if py:
            py.stop()

    def stats(v):
        return {"median": statistics.median(v), "p95": sorted(v)[int(len(v) * 0.95) - 1],
                "max": max(v), "n": len(v)} if v else None
    return {"offer ms": stats(offer_ms), "Ctrl+V to GetData PNG ms": stats(getdata_ms),
            "Ctrl+V to on screen ms": stats(visible_ms), "missed": misses,
            "error dialogs closed": dialogs, "calls on the last object": last_calls}


def main() -> int:
    p = argparse.ArgumentParser(description=__doc__,
                                formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--monitor", default=r"\\.\DISPLAY1")
    p.add_argument("--n", type=int, default=200)
    p.add_argument("--variants", default="python,winforms,own")
    a = p.parse_args()
    if not EXE.exists():
        raise SystemExit(f"build the C# side first: dotnet build -c Release {EXE.parents[3]}")
    mon = next(m for m in e11.monitors() if m["device"] == a.monitor)
    before = {it["id"] for it in e8.history()["items"]}
    out: dict = {"slide": list(SIZE)}
    screen = e6.ScreenProbe()
    paint = e11.open_maximised(mon, *SIZE)
    try:
        core.focus(paint)
        e6.send_keys(*e6.KEY_S)
        with tempfile.TemporaryDirectory() as td:
            for v in a.variants.split(","):
                n = a.n if v != "python" else min(a.n, 50)
                res = run_variant(v, n, paint, screen, Path(td))
                out[v] = res
                print(f"{v}: {json.dumps(res)}", file=sys.stderr, flush=True)
    finally:
        screen.close()
        core.dismiss_clipboard_error(paint, 0.5)
        core.close(paint, discard=True)
    out["new Win+V history items"] = len({it["id"] for it in e8.history()["items"]} - before)
    json.dump(out, sys.stdout, indent=2)
    print()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
