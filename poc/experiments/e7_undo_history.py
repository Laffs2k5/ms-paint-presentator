"""E7: can Paint's undo stack serve as slide history?

Paste N distinct slides (PNG, E1) into maximised Paint at 100% zoom, committing
each with `S` (E14), and record Paint's memory after each. Then press Ctrl+Z
until the history runs out, then Ctrl+Y until it runs out, recording after each
key which slide is on screen (every slide carries probe squares in its own
colour), how long the change took, and memory every few steps. A few undone
and redone slides are compared with their source pixel for pixel, to catch
selection marks coming back with the undo.

Run from poc/paintctl, so paintctl's venv is used:

    uv run python ../experiments/e7_undo_history.py [--monitor \\\\.\\DISPLAY1] [--size 2716x1434] [-n 50]

Paint is in front and gets SendInput keys: hands off while it runs (a few
minutes). Prints JSON; progress on stderr.
"""
from __future__ import annotations

import argparse
import json
import sys
import time

import e1_formats as e1  # same folder: PNG clipboard object
import e6_paste_speed as e6
import e11_display_area as e11
import e14_commit_paste as e14
import win32api
import win32con
import win32process
from paintctl import core
from PIL import ImageGrab

_VK_Z, _VK_Y = 0x5A, 0x59
CTRL_Z = ((e6._VK_CONTROL, False), (_VK_Z, False), (_VK_Z, True), (e6._VK_CONTROL, True))
CTRL_Y = ((e6._VK_CONTROL, False), (_VK_Y, False), (_VK_Y, True), (e6._VK_CONTROL, True))
_OFFSET = 300  # probe colours unique to this experiment


def memory_mb(pid: int) -> dict:
    h = win32api.OpenProcess(win32con.PROCESS_QUERY_LIMITED_INFORMATION
                             | win32con.PROCESS_VM_READ, False, pid)
    try:
        m = win32process.GetProcessMemoryInfo(h)
    finally:
        win32api.CloseHandle(h)
    return {"private_mb": round(m["PagefileUsage"] / 2**20),
            "working_set_mb": round(m["WorkingSetSize"] / 2**20)}


def battery() -> str:
    try:
        import ctypes

        class SPS(ctypes.Structure):
            _fields_ = [("ACLineStatus", ctypes.c_byte), ("BatteryFlag", ctypes.c_byte),
                        ("BatteryLifePercent", ctypes.c_byte), ("Reserved1", ctypes.c_byte),
                        ("BatteryLifeTime", ctypes.c_ulong),
                        ("BatteryFullLifeTime", ctypes.c_ulong)]
        s = SPS()
        ctypes.windll.kernel32.GetSystemPowerStatus(ctypes.byref(s))
        return {0: "battery", 1: "mains"}.get(s.ACLineStatus, "unknown")
    except Exception:
        return "unknown"


def main() -> int:
    p = argparse.ArgumentParser(description=__doc__,
                                formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--monitor", default=r"\\.\DISPLAY1")
    p.add_argument("--size", default="2716x1434")
    p.add_argument("-n", type=int, default=50, help="slides to paste")
    p.add_argument("--step-timeout", type=float, default=1.2,
                   help="seconds to wait for a Ctrl+Z/Ctrl+Y to change the slide")
    a = p.parse_args()
    w, h = (int(v) for v in a.size.lower().split("x"))
    mon = next(m for m in e11.monitors() if m["device"] == a.monitor)
    base = e11.test_pattern(w, h)
    colours = [e6.colour(k + _OFFSET) for k in range(a.n)]
    index_of = {c: k for k, c in enumerate(colours)}
    index_of[(255, 255, 255)] = -1  # blank canvas
    print(f"encoding {a.n} PNG slides...", file=sys.stderr, flush=True)
    pngs = [e1.enc_png(e1.solid_probe(base, c)) for c in colours]
    clip = e1.MultiFormatClipboard()
    screen = e6.ScreenProbe()
    out: dict = {"size": [w, h], "n": a.n, "power": battery()}
    paint = None
    try:
        paint = e11.open_maximised(mon, w, h)
        _, pid = win32process.GetWindowThreadProcessId(paint)
        info = core.info(paint)
        c = info["canvas_on_screen"]
        P = e11._PAD
        box = (c["left"] - P, c["top"] - P, c["left"] + w + P, c["top"] + h + P)
        probe = (c["left"] + e6.probe_points(w, h)[0][0] + e6._PROBE // 2,
                 c["top"] + e6.probe_points(w, h)[0][1] + e6._PROBE // 2)
        core.focus(paint)
        out["memory_start"] = memory_mb(pid)

        # --- paste N slides ---------------------------------------------------------
        pastes = []
        offset = None
        for k, png in enumerate(pngs):
            # a new data object per slide: Paint pastes a cached copy of a PNG
            # that was only swapped in place (CF_DIB in place is re-read)
            clip.offer([(e1.CF_PNG, png)])
            ms, dialog = e1.paste_and_commit(paint, screen, [probe], colours[k])
            if offset is None and ms is not None:  # align once, for pixel checks
                grab = ImageGrab.grab(bbox=box, all_screens=True)
                offset = e11.compare(grab, e1.solid_probe(base, colours[k]).convert("RGB"))
                out["first_slide_pixel_identical"] = offset["identical"]
                offset = offset["offset_from_uia_rect"]
            pastes.append({"k": k, "visible_ms": ms, "error_dialog": dialog,
                           **memory_mb(pid)})
            print(f"paste {k}: {ms} ms, dialog={dialog}, {pastes[-1]['private_mb']} MB private",
                  file=sys.stderr, flush=True)
        out["pastes"] = pastes
        time.sleep(2)
        out["memory_after_pastes_idle"] = memory_mb(pid)

        def clean(k: int) -> int | None:
            if offset is None or k < 0:
                return None
            n, _ = e14._diff_at(ImageGrab.grab(bbox=box, all_screens=True),
                                e1.solid_probe(base, colours[k]).convert("RGB"), *offset)
            return n

        # --- walk the history -------------------------------------------------------
        def walk(keys, label: str, checks: int = 3) -> list[dict]:
            steps = []
            current = index_of.get(screen.pixel(*probe))
            no_change = 0
            checked = 0
            while no_change < 3 and len(steps) < 3 * a.n + 10:
                t0 = time.perf_counter()
                e6.send_keys(*keys)
                shown, ms = current, None
                while time.perf_counter() - t0 < a.step_timeout:
                    now = index_of.get(screen.pixel(*probe), "other")
                    if now != current:
                        shown, ms = now, round((time.perf_counter() - t0) * 1000, 1)
                        break
                row = {"step": len(steps) + 1, "slide": shown, "ms": ms}
                if ms is None:
                    no_change += 1
                else:
                    no_change = 0
                    if checked < checks and isinstance(shown, int) and shown >= 0:
                        time.sleep(0.4)
                        row["diff_px"] = clean(shown)
                        checked += 1
                if len(steps) % 10 == 0:
                    row.update(memory_mb(pid))
                steps.append(row)
                current = shown
                print(f"{label} {row['step']}: slide {shown} after {ms} ms"
                      + (f", diff {row['diff_px']} px" if "diff_px" in row else ""),
                      file=sys.stderr, flush=True)
            return steps

        out["undo"] = walk(CTRL_Z, "undo")
        out["memory_after_undo"] = memory_mb(pid)
        out["redo"] = walk(CTRL_Y, "redo")
        out["memory_after_redo"] = memory_mb(pid)
    finally:
        clip.stop()
        screen.close()
        if paint:
            core.dismiss_clipboard_error(paint, 0.5)
            core.close(paint, discard=True)
    json.dump(out, sys.stdout, indent=2)
    print()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
