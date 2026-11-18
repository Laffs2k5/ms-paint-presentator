"""E19: what can pop up over the presentation, and what can the presenter do?

- notification state: SHQueryUserNotificationState (documented), which the
  pre-flight check could read.
- a toast over Paint: Paint maximised and in front, a test toast is shown
  (toast.ps1, as Windows PowerShell's app), and the corner where toasts appear
  is compared before and after. Also lists new top-level windows. The toast is
  removed from the notification history afterwards.
- presentation mode: Windows' presentation settings (PresentationSettings.exe
  /start, a documented tool on laptops) while the same toast is shown again:
  does the state change, is the toast held back? /stop afterwards.
- Paint at launch: popups (WinUI popup windows) visible after Paint starts,
  and whether they overlap the canvas.
- keep awake: SetThreadExecutionState(ES_CONTINUOUS | ES_DISPLAY_REQUIRED |
  ES_SYSTEM_REQUIRED) accepted. (`powercfg /requests` would show it, but needs
  an elevated prompt.)

Screen grabs stay in memory: only counts of changed pixels are reported, since
other notifications could show up in them.

Run from poc/paintctl, so paintctl's venv is used:

    uv run python ../experiments/e19_interruptions.py [--monitor \\\\.\\DISPLAY1]

Hands off while it runs (about a minute). Prints JSON; progress on stderr.
"""
from __future__ import annotations

import argparse
import ctypes
import json
import subprocess
import sys
import time
from ctypes import wintypes
from pathlib import Path

import e11_display_area as e11  # also sets DPI awareness
import win32gui
from paintctl import core
from PIL import ImageChops, ImageGrab

HELPER = Path(__file__).with_name("toast.ps1")
QUNS = {1: "not present (screen saver, locked, or fast user switching)",
        2: "busy (a full-screen app)", 3: "running a Direct3D full-screen app",
        4: "presentation mode", 5: "accepts notifications", 6: "quiet time",
        7: "a Windows Store app is running"}


def notification_state() -> str:
    state = ctypes.c_int()
    hr = ctypes.windll.shell32.SHQueryUserNotificationState(ctypes.byref(state))
    return QUNS.get(state.value, f"unknown {state.value}") if hr == 0 else f"hr {hr:#x}"


def toast(action: str) -> str:
    out = subprocess.run(["powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass",
                          "-File", str(HELPER), f"-{action}"],
                         capture_output=True, text=True, timeout=60)
    return (out.stdout or out.stderr).strip()


def visible_windows() -> dict[int, tuple[str, str, tuple]]:
    acc = {}

    def cb(h, _):
        if win32gui.IsWindowVisible(h):
            acc[h] = (win32gui.GetClassName(h), win32gui.GetWindowText(h), win32gui.GetWindowRect(h))
        return True
    win32gui.EnumWindows(cb, None)
    return acc


def corner(mon: dict):
    """Where toasts appear: the bottom right of the monitor's work area."""
    w = mon["work"]
    return (w.right - 900, w.bottom - 600, w.right, w.bottom)


def changed(a, b) -> int:
    diff = ImageChops.difference(a.convert("RGB"), b.convert("RGB")).convert("L")
    return diff.point(lambda v: 255 if v > 24 else 0).histogram()[255]


def toast_over_paint(mon: dict, paint: int) -> dict:
    core.focus(paint)
    time.sleep(0.5)
    box = corner(mon)
    before_img = ImageGrab.grab(bbox=box, all_screens=True)
    before_wins = visible_windows()
    shown = toast("Show")
    time.sleep(2.0)
    after_img = ImageGrab.grab(bbox=box, all_screens=True)
    new = {h: v for h, v in visible_windows().items() if h not in before_wins}
    res = {"toast": shown, "state": notification_state(),
           "changed pixels in the toast corner": changed(before_img, after_img),
           "new windows": [{"class": c, "title": t, "rect": r} for c, t, r in new.values()],
           "Paint still in front": win32gui.GetForegroundWindow() == paint}
    time.sleep(6)  # let it time out on screen
    res["removed from history"] = toast("Remove")
    return res


def presentation_mode(mon: dict, paint: int) -> dict:
    out = {"state before": notification_state()}
    subprocess.Popen(["PresentationSettings.exe", "/start"])
    time.sleep(2.0)
    out["state with presentation settings on"] = notification_state()
    out["toast"] = toast_over_paint(mon, paint)
    subprocess.Popen(["PresentationSettings.exe", "/stop"])
    time.sleep(2.0)
    out["state after /stop"] = notification_state()
    return out


def paint_popups(mon: dict, n: int) -> list:
    runs = []
    for _ in range(n):
        paint = e11.open_maximised(mon, 1600, 900)
        try:
            time.sleep(2.0)
            c = core.info(paint)["canvas_on_screen"]
            cr = (c["left"], c["top"], c["left"] + c["width"], c["top"] + c["height"])
            pops = []
            for h, (cls, title, r) in visible_windows().items():
                if "PopupWindowSiteBridge" not in cls:
                    continue
                if win32gui.GetWindow(h, 4) != paint and not _owned_by(h, paint):  # GW_OWNER
                    continue
                overlap = not (r[2] <= cr[0] or r[0] >= cr[2] or r[3] <= cr[1] or r[1] >= cr[3])
                pops.append({"rect": r, "overlaps canvas": overlap, "text": _uia_text(h)})
            runs.append(pops)
        finally:
            core.close(paint, discard=True)
    return runs


def _owned_by(h: int, paint: int) -> bool:
    import win32process
    return win32process.GetWindowThreadProcessId(h)[1] == \
        win32process.GetWindowThreadProcessId(paint)[1]


def _uia_text(h: int) -> list[str]:
    try:
        from pywinauto import Desktop
        w = Desktop(backend="uia").window(handle=h)
        return sorted({d.element_info.name for d in w.descendants() if d.element_info.name})[:6]
    except Exception as exc:  # noqa: BLE001
        return [f"({type(exc).__name__})"]


def keep_awake() -> dict:
    es = 0x80000000 | 0x00000002 | 0x00000001  # CONTINUOUS | DISPLAY | SYSTEM
    k32 = ctypes.WinDLL("kernel32")
    k32.SetThreadExecutionState.restype = wintypes.DWORD
    prev = k32.SetThreadExecutionState(es)
    back = k32.SetThreadExecutionState(0x80000000)  # clear again
    return {"accepted": prev != 0, "previous state": hex(prev),
            "state while set": hex(back)}


def main() -> int:
    p = argparse.ArgumentParser(description=__doc__,
                                formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--monitor", default=r"\\.\DISPLAY1")
    p.add_argument("--launches", type=int, default=3)
    a = p.parse_args()
    mon = next(m for m in e11.monitors() if m["device"] == a.monitor)
    out: dict = {"notification state": notification_state()}

    def log(name, fn):
        try:
            res = fn()
        except Exception as exc:  # noqa: BLE001 - record and go on
            res = {"error": f"{type(exc).__name__}: {exc}"}
        out[name] = res
        print(f"{name}: {json.dumps(res)}", file=sys.stderr, flush=True)

    paint = e11.open_maximised(mon, 1600, 900)
    try:
        log("toast over Paint", lambda: toast_over_paint(mon, paint))
        log("presentation mode", lambda: presentation_mode(mon, paint))
    finally:
        subprocess.run(["PresentationSettings.exe", "/stop"], timeout=30)
        core.close(paint, discard=True)
    log("Paint popups at launch", lambda: paint_popups(mon, a.launches))
    log("keep awake", keep_awake)
    json.dump(out, sys.stdout, indent=2, ensure_ascii=False)
    print()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
