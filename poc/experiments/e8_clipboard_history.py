"""E8: keep slides out of Windows clipboard history (Win+V) and cloud sync.

Windows skips clipboard history and cloud sync for clipboard content that
also carries one of these registered formats (learn.microsoft.com, "Clipboard
Formats", Cloud Clipboard and Clipboard History Formats):
- ExcludeClipboardContentFromMonitorProcessing: any data; no history, no sync
- CanIncludeInClipboardHistory: DWORD 0; no history
- CanUploadToCloudClipboard: DWORD 0; no cloud sync

For each case a PNG slide is put on the clipboard as a new OLE data object
(E1/E7) with or without those formats, left there a few seconds, then Win+V
history is read through clipboard_history.ps1 (ids, times and format names
only, never contents). The slide is also pasted into Paint to check the extra
formats do not break pasting. Data-object calls before the paste come from
other processes (the history service, clipboard managers) and are logged.

The control case (no opt-out) adds one item to the user's clipboard history,
and the script leaves it there. Do NOT delete it through the WinRT API: the
one run that called `Clipboard.DeleteItemFromHistory` on that single item ended
with the whole history (25 items) gone, most likely because of that call.
Remove it by hand in Win+V if needed.

Run from poc/paintctl, so paintctl's venv is used:

    uv run python ../experiments/e8_clipboard_history.py [--monitor \\\\.\\DISPLAY1]

Hands off while it runs (about a minute). Prints JSON; progress on stderr.
"""
from __future__ import annotations

import argparse
import json
import struct
import subprocess
import sys
import time
from pathlib import Path

import e1_formats as e1  # same folder: PNG encoder, OLE clipboard object
import e6_paste_speed as e6
import e11_display_area as e11
import win32clipboard
from paintctl import core

HELPER = Path(__file__).with_name("clipboard_history.ps1")
EXCLUDE = win32clipboard.RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing")
IN_HISTORY = win32clipboard.RegisterClipboardFormat("CanIncludeInClipboardHistory")
TO_CLOUD = win32clipboard.RegisterClipboardFormat("CanUploadToCloudClipboard")
DWORD0 = struct.pack("<I", 0)

CASES = {
    "control: no opt-out": [],
    "ExcludeClipboardContentFromMonitorProcessing": [(EXCLUDE, DWORD0)],
    "CanIncludeInClipboardHistory=0 + CanUploadToCloudClipboard=0":
        [(IN_HISTORY, DWORD0), (TO_CLOUD, DWORD0)],
    "all three": [(EXCLUDE, DWORD0), (IN_HISTORY, DWORD0), (TO_CLOUD, DWORD0)],
}


def history(delete: str | None = None) -> dict:
    cmd = ["powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", str(HELPER)]
    if delete:
        cmd += ["-Delete", delete]
    out = subprocess.run(cmd, capture_output=True, text=True, timeout=60)
    return json.loads(out.stdout)


def main() -> int:
    p = argparse.ArgumentParser(description=__doc__,
                                formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--monitor", default=r"\\.\DISPLAY1")
    p.add_argument("--format", choices=("png", "dib"), default="png",
                   help="slide format: PNG (registered) or CF_DIB (standard)")
    p.add_argument("--settle", type=float, default=3.0,
                   help="seconds the slide stays on the clipboard before history is read")
    a = p.parse_args()
    w, h = 1600, 1000
    mon = next(m for m in e11.monitors() if m["device"] == a.monitor)
    base = e11.test_pattern(w, h)
    clip = e1.MultiFormatClipboard()
    screen = e6.ScreenProbe()
    start = history()
    out: dict = {"format": a.format,
                 "history_enabled": start["history_enabled"], "status": start["status"],
                 "items_at_start": len(start["items"]), "cases": {}}
    paint = None
    try:
        paint = e11.open_maximised(mon, w, h)
        c = core.info(paint)["canvas_on_screen"]
        probe = (c["left"] + e6.probe_points(w, h)[0][0] + e6._PROBE // 2,
                 c["top"] + e6.probe_points(w, h)[0][1] + e6._PROBE // 2)
        core.focus(paint)
        for i, (name, extra) in enumerate(CASES.items()):
            before = {it["id"] for it in history()["items"]}
            colour = e6.colour(700 + i)
            slide = e1.solid_probe(base, colour)
            data = ((e1.CF_PNG, e1.enc_png(slide)) if a.format == "png"
                    else (e1.CF_DIB, e1.enc_dib24(slide)))
            clip.offer([data] + extra)
            t0 = time.perf_counter()
            time.sleep(a.settle)
            calls_before_paste = [(round((x["t"] - t0) * 1000), x["call"], x["format"])
                                  for x in clip.log]
            after = history()["items"]
            new = [it for it in after if it["id"] not in before]
            ms, dialog = e1.paste_and_commit(paint, screen, [probe], colour)
            row = {"new_history_items": len(new),
                   "new_item_formats": [it["formats"] for it in new],
                   "reads_before_paste": calls_before_paste,
                   "paste_visible_ms": ms, "error_dialog": dialog}
            out["cases"][name] = row
            print(f"{name}: new history items {len(new)} {row['new_item_formats']}, "
                  f"paste {ms} ms, reads before paste {calls_before_paste}",
                  file=sys.stderr, flush=True)
    finally:
        clip.stop()
        screen.close()
        if paint:
            core.dismiss_clipboard_error(paint, 0.5)
            core.close(paint, discard=True)
    out["items_at_end"] = len(history()["items"])
    json.dump(out, sys.stdout, indent=2)
    print()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
