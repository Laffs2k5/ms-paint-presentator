"""E10: the presenter's keys through a low-level keyboard hook.

The design (docs/poc-plan.md): Paint stays in front with its title bar active;
a low-level keyboard hook (WH_KEYBOARD_LL) takes the show keys before Paint
sees them, but only while Paint (or the presenter view) is in front, and lets
the presenter's own synthetic keys through. The callback only queues the key;
another thread does the paste.

Telling keys apart: the presenter tags its own SendInput events with an
`dwExtraInfo` value (OWN_TAG) and the hook passes those. Everything else is
treated as the user, physical or not. The tests stand in for the user with
SendInput events tagged USER_TAG, which the hook treats like any other key.

Tests (Paint maximised and in front, canvas 2000 x 1000, laptop panel):

- swallow: a floating paste on the canvas; the user presses Right x3, B and
  Esc. With the hook swallowing, the paste must not move (arrows move it,
  E17), the tool must stay Select (B is Fill) and the paste must survive (Esc
  discards it). Control: the same keys with the hook only watching.
- other app: another window in front; the user's keys must reach it and the
  presenter must ignore them.
- latency: user presses Right; the presenter pastes the next slide (already
  on the clipboard) with tagged Ctrl+V and `S`. Key press to slide on screen,
  against a direct Ctrl+V without the hook. Also how long the hook callback
  takes to be called, idle and with the main thread busy.
- timeout: a callback that sleeps too long once. Does Windows remove the hook
  (it says so for Windows 7 and later), from what delay? A heartbeat (a tagged
  key the hook swallows) detects it, and reinstalling fixes it.
- hotkey: the fallback, RegisterHotKey on Right with no modifiers: does the
  presenter get WM_HOTKEY and Paint nothing?

Run from poc/paintctl, so paintctl's venv is used:

    uv run python ../experiments/e10_keys.py [--monitor \\\\.\\DISPLAY1] [--tests swallow,...]
    uv run python ../experiments/e10_keys.py --listen 30   # print keys for 30 s (a remote)

Hands off the keyboard and mouse while it runs (about a minute): it sends key
presses and, in the timeout test, stalls keyboard input for up to about a
second at a time. --listen swallows nothing and prints each key's codes.
"""
from __future__ import annotations

import argparse
import ctypes
import json
import queue
import statistics
import sys
import threading
import time
from ctypes import wintypes

import e6_paste_speed as e6  # _INPUT structures, ScreenProbe, colour()
import e11_display_area as e11  # also sets DPI awareness
import e15_draw as e15  # active_tool
import e17_place_paste as e17  # Stage: Paint, clipboard, finding a stamp
import win32con
import win32gui
from paintctl import core

_u32 = ctypes.WinDLL("user32", use_last_error=True)
_k32 = ctypes.WinDLL("kernel32", use_last_error=True)
LRESULT = ctypes.c_ssize_t
HOOKPROC = ctypes.WINFUNCTYPE(LRESULT, ctypes.c_int, wintypes.WPARAM, wintypes.LPARAM)
_u32.SetWindowsHookExW.restype = wintypes.HHOOK
_u32.SetWindowsHookExW.argtypes = (ctypes.c_int, HOOKPROC, wintypes.HINSTANCE, wintypes.DWORD)
_u32.CallNextHookEx.restype = LRESULT
_u32.CallNextHookEx.argtypes = (wintypes.HHOOK, ctypes.c_int, wintypes.WPARAM, wintypes.LPARAM)
_u32.UnhookWindowsHookEx.argtypes = (wintypes.HHOOK,)
_u32.GetForegroundWindow.restype = wintypes.HWND
_u32.GetAncestor.restype = wintypes.HWND
_u32.GetAncestor.argtypes = (wintypes.HWND, wintypes.UINT)
_u32.GetMessageW.argtypes = (ctypes.POINTER(wintypes.MSG), wintypes.HWND, wintypes.UINT, wintypes.UINT)
_u32.PostThreadMessageW.argtypes = (wintypes.DWORD, wintypes.UINT, wintypes.WPARAM, wintypes.LPARAM)
_u32.RegisterHotKey.argtypes = (wintypes.HWND, ctypes.c_int, wintypes.UINT, wintypes.UINT)
_u32.UnregisterHotKey.argtypes = (wintypes.HWND, ctypes.c_int)
_k32.GetModuleHandleW.restype = wintypes.HMODULE


class KBDLLHOOKSTRUCT(ctypes.Structure):
    _fields_ = (("vkCode", wintypes.DWORD), ("scanCode", wintypes.DWORD),
                ("flags", wintypes.DWORD), ("time", wintypes.DWORD),
                ("dwExtraInfo", ctypes.c_size_t))


WH_KEYBOARD_LL = 13
LLKHF_INJECTED, LLKHF_UP = 0x10, 0x80
OWN_TAG = 0x50414E54  # "PANT": the presenter's own keys
USER_TAG = 0x55534552  # "USER": the tests' stand-in for the user
VK_HEARTBEAT = 0x97  # unassigned virtual-key code
VK_SLOW = 0x87  # F24: makes the callback sleep (timeout test only)
WM_APP_HOOK, WM_APP_UNHOOK, WM_APP_HOTKEY, WM_APP_UNHOTKEY = 0x8001, 0x8002, 0x8003, 0x8004

# PowerPoint's slide-show keys (plan: presenter design), by virtual-key code
SHOW_KEYS = {0x27: "next", 0x28: "next", 0x20: "next", 0x0D: "next", 0x22: "next", 0x4E: "next",
             0x25: "prev", 0x26: "prev", 0x08: "prev", 0x21: "prev", 0x50: "prev",
             0x24: "first", 0x23: "last", 0x42: "black", 0xBE: "black", 0x57: "white",
             0xBC: "white", 0x1B: "end", **{0x30 + d: f"digit {d}" for d in range(10)}}
VK_NAMES = {0x27: "Right", 0x28: "Down", 0x25: "Left", 0x26: "Up", 0x20: "Space", 0x0D: "Enter",
            0x22: "PageDown", 0x21: "PageUp", 0x08: "Backspace", 0x1B: "Esc", 0x74: "F5",
            0xBE: ".", 0xBC: ","}


def send(events: list[tuple[int, bool]], tag: int) -> float:
    """(vk, key_up) events as one SendInput batch, tagged. Returns the send time."""
    arr = (e6._INPUT * len(events))()
    for i, (vk, up) in enumerate(events):
        arr[i].type = 1
        arr[i].u.ki = e6._KEYBDINPUT(vk, 0, 0x0002 if up else 0, 0, tag)
    t = time.perf_counter()
    if e6._user32.SendInput(len(events), arr, ctypes.sizeof(e6._INPUT)) != len(events):
        raise ctypes.WinError(ctypes.get_last_error())
    return t


def tap(vk: int, tag: int = USER_TAG) -> float:
    return send([(vk, False), (vk, True)], tag)


class KeyHook:
    """A WH_KEYBOARD_LL hook on its own thread with a message loop."""

    def __init__(self, targets: set[int], swallow: bool = True, listen: bool = False):
        self.targets, self.swallow, self.listen = targets, swallow, listen
        self.events: queue.Queue = queue.Queue()  # (action, vk, t_called)
        self.passed: list = []  # user keys let through, (vk, foreground)
        self.called: list[float] = []  # perf_counter at each callback
        self.heartbeat = threading.Event()
        self.slow_ms = 0
        self.hotkeys: list = []
        self.hook = None
        self._proc = HOOKPROC(self._callback)  # keep a reference
        self._ready = threading.Event()
        self._thread = threading.Thread(target=self._run, daemon=True, name="keyhook")
        self._thread.start()
        self._ready.wait(5)

    # -- on the hook thread ----------------------------------------------------------------
    def _install(self):
        self.hook = _u32.SetWindowsHookExW(WH_KEYBOARD_LL, self._proc,
                                           _k32.GetModuleHandleW(None), 0)

    def _run(self):
        self.tid = _k32.GetCurrentThreadId()
        self._install()
        self._ready.set()
        msg = wintypes.MSG()
        while _u32.GetMessageW(ctypes.byref(msg), None, 0, 0) > 0:
            if msg.message == WM_APP_HOOK:
                self._install()
            elif msg.message == WM_APP_UNHOOK:
                if self.hook:
                    _u32.UnhookWindowsHookEx(self.hook)
                self.hook = None
            elif msg.message == WM_APP_HOTKEY:
                ok = _u32.RegisterHotKey(None, 1, 0x4000, msg.wParam)  # MOD_NOREPEAT
                self.hotkeys.append(("registered", bool(ok), ctypes.get_last_error()))
            elif msg.message == WM_APP_UNHOTKEY:
                _u32.UnregisterHotKey(None, 1)
            elif msg.message == win32con.WM_HOTKEY:
                self.hotkeys.append(("WM_HOTKEY", msg.lParam >> 16, time.perf_counter()))
            self._ack.set() if hasattr(self, "_ack") else None
        if self.hook:
            _u32.UnhookWindowsHookEx(self.hook)

    def _callback(self, code, wparam, lparam):
        t = time.perf_counter()
        if code < 0:
            return _u32.CallNextHookEx(None, code, wparam, lparam)
        kb = ctypes.cast(lparam, ctypes.POINTER(KBDLLHOOKSTRUCT)).contents
        self.called.append(t)
        down = wparam in (win32con.WM_KEYDOWN, win32con.WM_SYSKEYDOWN)
        if self.listen:
            if down:
                print(json.dumps({"vk": kb.vkCode, "name": VK_NAMES.get(kb.vkCode, chr(kb.vkCode)
                                  if 0x30 <= kb.vkCode <= 0x5A else None),
                                  "scan": kb.scanCode, "flags": kb.flags,
                                  "injected": bool(kb.flags & LLKHF_INJECTED)}), flush=True)
            return _u32.CallNextHookEx(None, code, wparam, lparam)
        if kb.dwExtraInfo == OWN_TAG:
            if kb.vkCode == VK_HEARTBEAT:
                self.heartbeat.set()
                return 1
            return _u32.CallNextHookEx(None, code, wparam, lparam)
        if kb.vkCode == VK_SLOW and self.slow_ms:
            if down:
                time.sleep(self.slow_ms / 1000)
            return 1
        fg = _u32.GetAncestor(_u32.GetForegroundWindow(), 3)  # GA_ROOTOWNER
        action = SHOW_KEYS.get(kb.vkCode)
        if action and fg in self.targets:
            if down:
                self.events.put((action, kb.vkCode, t))
            if self.swallow:
                return 1
        elif down:
            self.passed.append((kb.vkCode, fg))
        return _u32.CallNextHookEx(None, code, wparam, lparam)

    # -- from other threads --------------------------------------------------------------------
    def post(self, msg: int, wparam: int = 0) -> None:
        self._ack = threading.Event()
        _u32.PostThreadMessageW(self.tid, msg, wparam, 0)
        self._ack.wait(2)

    def alive(self, timeout: float = 0.3) -> bool:
        """Heartbeat: a tagged key only the hook sees and swallows."""
        self.heartbeat.clear()
        tap(VK_HEARTBEAT, OWN_TAG)
        return self.heartbeat.wait(timeout)

    def drain(self) -> list:
        out = []
        while not self.events.empty():
            out.append(self.events.get_nowait())
        return out

    def stop(self) -> None:
        _u32.PostThreadMessageW(self.tid, win32con.WM_QUIT, 0, 0)
        self._thread.join(2)


class OtherApp:
    """A window of our own standing in for another app; counts key presses."""

    def __init__(self, x, y, w, h):
        self.keys: list[int] = []
        ready = threading.Event()

        def run():
            wc = win32gui.WNDCLASS()
            # one class per instance: the window procedure is stored with the class
            wc.lpszClassName = f"E10OtherApp{id(self)}"
            wc.hbrBackground = win32con.COLOR_WINDOW + 1
            wc.lpfnWndProc = {
                win32con.WM_KEYDOWN:
                    lambda h, m, wp, lp: self.keys.append((wp, time.perf_counter())) or 0,
                win32con.WM_DESTROY: lambda *a: win32gui.PostQuitMessage(0) or 0}
            try:
                win32gui.RegisterClass(wc)
            except win32gui.error as exc:
                if exc.winerror != 1410:
                    raise
            self.hwnd = win32gui.CreateWindowEx(0, wc.lpszClassName, "E10 other app",
                                                win32con.WS_OVERLAPPEDWINDOW | win32con.WS_VISIBLE,
                                                x, y, w, h, 0, 0, 0, None)
            ready.set()
            win32gui.PumpMessages()

        threading.Thread(target=run, daemon=True).start()
        ready.wait(5)

    def close(self):
        win32gui.PostMessage(self.hwnd, win32con.WM_CLOSE, 0, 0)


# --- tests ------------------------------------------------------------------------------------
def floating_stamp(st) -> tuple:
    img, rgb = e17.solid(200, 150, 40)
    st.paste(img)
    return rgb, st.wait_for(rgb)


def user_keys_on_paste(st, hook) -> dict:
    rgb, start = floating_stamp(st)
    for vk in (0x27, 0x27, 0x27):
        tap(vk)
        time.sleep(0.05)
    time.sleep(0.4)
    after_arrows = st.find(rgb)
    tap(0x42)  # B: Fill in Paint, "black" for the presenter
    time.sleep(0.4)
    tool = e15.active_tool(st.paint)
    tap(0x1B)  # Esc: discards a floating paste in Paint
    time.sleep(0.4)
    return {"paste at start": start, "after Right x3": after_arrows, "tool after B": tool,
            "paste after Esc": st.find(rgb),
            "presenter got": [(a, VK_NAMES.get(vk, chr(vk))) for a, vk, _ in hook.drain()]}


def test_swallow(mon) -> dict:
    out = {}
    for label, swallow in (("hook swallowing", True), ("control: hook only watching", False)):
        st = e17.Stage(mon)
        hook = KeyHook({st.paint}, swallow=swallow)
        try:
            out[label] = user_keys_on_paste(st, hook)
        finally:
            hook.stop()
            st.close()
    return out


def test_other_app(mon) -> dict:
    st = e17.Stage(mon)
    w = mon["work"]
    other = OtherApp(w.left + 300, w.top + 300, 800, 400)
    hook = KeyHook({st.paint})
    try:
        core.focus(other.hwnd)
        time.sleep(0.4)
        for vk in (0x27, 0x20, 0x42):
            tap(vk)
            time.sleep(0.05)
        time.sleep(0.4)
        return {"other app got": [VK_NAMES.get(v, chr(v)) for v, _ in other.keys],
                "presenter got": hook.drain(),
                "foreground was other app": _u32.GetForegroundWindow() == other.hwnd}
    finally:
        hook.stop()
        other.close()
        st.close()


def _wait_colour(st, xy, rgb, timeout=3.0):
    t0 = time.perf_counter()
    while time.perf_counter() - t0 < timeout:
        if st.screen.pixel(*xy) == rgb:
            return time.perf_counter()
    return None


def test_latency(mon, n: int) -> dict:
    st = e17.Stage(mon)
    hook = KeyHook({st.paint})
    probe = st.at(1000, 500)
    out = {"direct Ctrl+V": [], "key through the hook": [], "hook called after SendInput": []}
    try:
        k = 50
        for i in range(2 * n):
            k += 1
            img, rgb = e17.solid(*e17.CANVAS, k)
            st.offer(img)  # the next slide is on the clipboard before the key
            time.sleep(0.4)
            if i % 2 == 0:  # baseline: the presenter pastes straight away
                t0 = send(list(e6.CTRL_V), OWN_TAG)
                seen = _wait_colour(st, probe, rgb)
                if seen:
                    out["direct Ctrl+V"].append(round((seen - t0) * 1000, 1))
            else:  # the user presses Right; the hook queues it; we paste
                hook.called.clear()
                t0 = tap(0x27)
                action, vk, t_hook = hook.events.get(timeout=2)
                send(list(e6.CTRL_V), OWN_TAG)
                seen = _wait_colour(st, probe, rgb)
                out["hook called after SendInput"].append(round((t_hook - t0) * 1000, 2))
                if seen:
                    out["key through the hook"].append(round((seen - t0) * 1000, 1))
            time.sleep(0.3)
            send(list(e6.KEY_S), OWN_TAG)
            time.sleep(0.4)
        # hook delay with the main thread busy in pure Python
        busy = []
        stop = time.perf_counter() + 3

        def spin():
            x = 0
            while time.perf_counter() < stop:
                x += 1

        spinner = threading.Thread(target=spin)
        spinner.start()
        for _ in range(10):
            hook.called.clear()
            t0 = tap(VK_HEARTBEAT, USER_TAG)  # not a show key: passes, but is timed
            time.sleep(0.2)
            if hook.called:
                busy.append(round((hook.called[0] - t0) * 1000, 2))
        spinner.join()
        out["hook called after SendInput, a thread busy"] = busy
    finally:
        hook.stop()
        st.close()
    for key in list(out):
        v = out[key]
        if v:
            out[key] = {"median": statistics.median(v), "max": max(v), "n": len(v)}
    return out


def test_timeout(mon) -> dict:
    """A callback that sleeps once. With another app in front: is the slow key
    passed on anyway (Windows gave up waiting), how late does the next key
    reach that app (the stall everyone's keyboard sees), and is the hook still
    alive afterwards?"""
    out = {}
    w = mon["work"]
    other = OtherApp(w.left + 300, w.top + 300, 800, 400)
    try:
        core.focus(other.hwnd)
        time.sleep(0.4)
        for ms in (200, 400, 700, 1100, 2100):
            hook = KeyHook(set())
            try:
                ok_before = hook.alive()
                other.keys.clear()
                hook.slow_ms = ms
                t_slow = tap(VK_SLOW)
                t_a = tap(0x41)  # "A", straight after
                time.sleep(ms / 1000 + 0.6)
                hook.slow_ms = 0
                got = {vk: t for vk, t in other.keys}
                res = {"alive before": ok_before,
                       "SendInput of the slow key returned after ms": round((t_a - t_slow) * 1000),
                       "slow key passed on to the app anyway": VK_SLOW in got,
                       "next key reached the app after ms":
                           round((got[0x41] - t_a) * 1000) if 0x41 in got else None,
                       "alive after": hook.alive()}
                if not res["alive after"]:
                    hook.post(WM_APP_HOOK)
                    res["alive after reinstall"] = hook.alive()
                out[f"callback sleeps {ms} ms"] = res
            finally:
                hook.stop()
            print(f"  timeout {ms} ms: {res}", file=sys.stderr, flush=True)
        # the watchdog: a removed hook misses the heartbeat; reinstalling fixes it
        hook = KeyHook(set())
        try:
            hook.post(WM_APP_UNHOOK)
            gone = hook.alive()
            hook.post(WM_APP_HOOK)
            out["watchdog"] = {"heartbeat with the hook removed": gone,
                               "heartbeat after reinstalling": hook.alive()}
        finally:
            hook.stop()
    finally:
        other.close()
    return out


def test_hotkey(mon) -> dict:
    st = e17.Stage(mon)
    hook = KeyHook(set())  # only the thread and its message loop are used
    try:
        hook.post(WM_APP_UNHOOK)
        rgb, start = floating_stamp(st)
        hook.post(WM_APP_HOTKEY, 0x27)
        tap(0x27)
        time.sleep(0.4)
        moved = st.find(rgb)
        hook.post(WM_APP_UNHOTKEY)
        tap(0x27)
        time.sleep(0.4)
        return {"register": hook.hotkeys[0] if hook.hotkeys else None,
                "WM_HOTKEY received": sum(1 for h in hook.hotkeys if h[0] == "WM_HOTKEY"),
                "paste at start": start, "after Right with the hotkey": moved,
                "after Right, hotkey removed": st.find(rgb)}
    finally:
        hook.stop()
        st.close()


def listen(seconds: float) -> None:
    hook = KeyHook(set(), listen=True)
    print(f"Press the remote's buttons; listening {seconds:.0f} s (nothing is swallowed).",
          file=sys.stderr, flush=True)
    time.sleep(seconds)
    hook.stop()


TESTS = {"swallow": test_swallow, "other-app": test_other_app, "latency": None,
         "timeout": test_timeout, "hotkey": test_hotkey}


def main() -> int:
    p = argparse.ArgumentParser(description=__doc__,
                                formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--monitor", default=r"\\.\DISPLAY1")
    p.add_argument("--tests", default=",".join(TESTS))
    p.add_argument("--n", type=int, default=8, help="slides per route in the latency test")
    p.add_argument("--listen", type=float, metavar="SECONDS")
    a = p.parse_args()
    if a.listen:
        listen(a.listen)
        return 0
    mon = next(m for m in e11.monitors() if m["device"] == a.monitor)
    out: dict = {}
    for name in a.tests.split(","):
        fn = (lambda m: test_latency(m, a.n)) if name == "latency" else TESTS[name]
        try:
            res = fn(mon)
        except Exception as exc:  # noqa: BLE001 - record and go on
            res = {"error": f"{type(exc).__name__}: {exc}"}
        out[name] = res
        print(f"{name}: {json.dumps(res)}", file=sys.stderr, flush=True)
    json.dump(out, sys.stdout, indent=2)
    print()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
