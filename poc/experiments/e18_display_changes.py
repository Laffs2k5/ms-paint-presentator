"""E18: display changes in the middle of a show, end to end.

Starts `presenter present` on one display, switches displays off and on with
poc/dotnet/E18Displays (Windows' own display configuration API, what Windows
does when a cable is pulled), answers the screen picker through UI
Automation as the speaker would, and reports what the presenter did.

Scenarios (--scenario):
  back      the presenting display off for --seconds, then back; the picker
            is answered with that display once it is listed again
  internal  only the laptop's own panel for --seconds; the presenting
            display is picked again once it is back
  other     the presenting display off for --seconds; another display
            (--other) is picked while it is away, and again when it is back
  mode      the presenting display at --mode (e.g. 1920x1080) for --seconds;
            it is picked at once, and again when its resolution is back
  scale     the presenting display's scaling one step up for --seconds; it is
            picked at once, and again when the scaling is back
  clone     duplicate (Win+P) for --seconds; the presenting display is picked
            whenever it is listed, again when the layout is back

Needs the presenter and E18Displays built. Drives the real screen: run only
when the screen is free. Prints JSON.

    python e18_display_changes.py --deck <deck> --display \\\\.\\DISPLAY2 --scenario back
"""
from __future__ import annotations

import argparse
import json
import re
import subprocess
import sys
import threading
import time
from pathlib import Path

from pywinauto import Desktop

ROOT = Path(__file__).resolve().parents[2]
PRESENTER = ROOT / "src/Presenter.Cli/bin/Debug/net9.0-windows10.0.19041.0/presenter.exe"
DISPLAYS = ROOT / "poc/dotnet/E18Displays/bin/Release/net9.0-windows/E18Displays.exe"


def display_index(device: str) -> int:
    """E18Displays' number for a GDI device name."""
    out = subprocess.run([str(DISPLAYS), "list"], capture_output=True, text=True, check=True).stdout
    for line in out.splitlines():
        m = re.match(r"(\d+)\.\s+(\S+)", line)
        if m and m.group(2).lower() == device.lower():
            return int(m.group(1))
    raise SystemExit(f"{device} is not an active display:\n{out}")


def answer_picker(device: str, timeout: float, log: list, times: int = 1) -> None:
    """Wait for the screen picker, then until it lists `device`, and pick it;
    `times` pickers in a row (a display that leaves and comes back changes
    the displays twice)."""
    for n in range(times):
        if n:
            time.sleep(2)  # the previous picker closing
        # a later picker comes when the change is undone, or not at all (the
        # display was picked only once it was back): no long wait for it
        _answer_once(device, timeout if n == 0 else min(timeout, 35), log)


def _answer_once(device: str, timeout: float, log: list) -> None:
    picker = Desktop(backend="uia").window(auto_id="ScreenPicker")
    t0 = time.time()
    if not picker.exists(timeout=timeout):
        log.append("the picker never came up")
        return
    log.append(f"picker up after {time.time() - t0:.1f} s")
    while time.time() - t0 < timeout:
        try:
            for b in picker.descendants(control_type="Button"):
                if b.element_info.element.CurrentHelpText == device:
                    time.sleep(1.0)  # a speaker reads before choosing
                    b.invoke()
                    log.append(f"picked {device} {time.time() - t0:.1f} s after the change")
                    return
        except Exception:  # noqa: BLE001 - the picker rebuilds its list while we look
            pass
        time.sleep(0.5)
    log.append(f"{device} was never listed")


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--deck", required=True)
    ap.add_argument("--display", required=True, help=r"the presenting display, e.g. \\.\DISPLAY2")
    ap.add_argument("--scenario", choices=["back", "internal", "other", "mode", "scale", "clone"], default="back")
    ap.add_argument("--other", help="the display to pick in the 'other' scenario")
    ap.add_argument("--mode", default="1920x1080", help="the resolution in the 'mode' scenario")
    ap.add_argument("--seconds", type=float, default=12)
    ap.add_argument("--soak", type=int, default=40, help="random speaker keys during the show")
    ap.add_argument("--cycles", type=int, default=1, help="how many times to make the change, one after the other")
    ap.add_argument("--log", help="keep the presenter's whole log in this file")
    a = ap.parse_args()

    target = a.other if a.scenario == "other" else a.display
    n = str(display_index(a.display))
    change = {
        "back": ["detach", n], "other": ["detach", n], "internal": ["internal"], "clone": ["clone"],
        "mode": ["mode", n, a.mode], "scale": ["scale", n, "+1"],
    }[a.scenario] + ["--for", str(a.seconds)]
    presenter = subprocess.Popen([str(PRESENTER), "present", a.deck, "--monitor", a.display, "--no-painting",
                                  "--verify", "--soak", str(a.soak)],
                                 stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, encoding="utf-8")
    log: list[str] = []
    lines: list[str] = []

    def read_stderr():
        for line in presenter.stderr:
            lines.append(line.rstrip())
    reader = threading.Thread(target=read_stderr, daemon=True)
    reader.start()
    while not any("Ready:" in l for l in lines):
        if presenter.poll() is not None:
            raise SystemExit("the presenter ended before the show started:\n" + "\n".join(lines))
        time.sleep(0.2)
    time.sleep(4)  # a few slides first
    # once during the change and once when it is undone, unless the display is
    # away the whole time ('back', 'internal'): then only once it is back
    times = 1 if a.scenario in ("back", "internal") else 2
    t_change = time.time()
    for cycle in range(a.cycles):
        if cycle:
            time.sleep(8)  # the show carries on for a while between changes
        picker = threading.Thread(target=answer_picker, args=(target, a.seconds + 60, log, times), daemon=True)
        picker.start()
        switched = subprocess.run([str(DISPLAYS), *change], capture_output=True, text=True)
        log.append(f"displays: {' '.join(change)} -> {switched.stdout.strip().splitlines()[-1] if switched.stdout.strip() else switched.stderr.strip()}")
        picker.join(timeout=180)
    out = presenter.stdout.read()  # not communicate(): it would take stderr from the reader
    presenter.wait(timeout=300)
    reader.join(timeout=5)
    if a.log:
        Path(a.log).write_text("\n".join(lines), encoding="utf-8")
    summary = json.loads(out[out.index("{"):]) if "{" in out else {}
    verified = summary.get("verified", {})
    print(json.dumps({
        "scenario": a.scenario, "presenting on": a.display, "picked": target,
        "harness": log,
        "presenter log": [l for l in lines if any(k in l for k in ("display", "resum", "again", "Paint", "picker", "skipped"))],
        "resumed": summary.get("resumed"),
        "slide changes": len(summary.get("slide changes", [])),
        "key to slide ms": summary.get("key to slide ms"),
        "verified min": verified.get("min"),
        "seconds from change to the end": round(time.time() - t_change, 1),
    }, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
