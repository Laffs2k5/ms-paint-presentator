"""paintctl core: launch, size, measure and capture Windows 11 Paint.

All coordinates are **physical screen pixels**: the process opts into
per-monitor-v2 DPI awareness before any window or UI Automation call, so
nothing is DPI-virtualised.

UI Automation anchors (AutomationIds) are the ones DoomPaint uses against
Windows 11 Paint (https://github.com/markrussinovich/DoomPaint, MIT):
``scrollViewer`` (canvas viewport), ``image`` (the canvas itself),
``CanvasSizeTextBlock`` (status bar "W x H px"), ``ZoomValuesComboBox`` and
``ZoomSliderControl``. AutomationIds are locale-independent; element *names*
(e.g. "Fit to window") are localised, so name-based lookups are fallbacks only.
"""
from __future__ import annotations

import ctypes
import os
import re
import subprocess
import tempfile
import time
from ctypes import wintypes
from dataclasses import asdict, dataclass
from pathlib import Path

# --- DPI awareness: must run before any user32/UIA geometry call -------------
_DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = ctypes.c_void_p(-4)


def _enable_dpi_awareness() -> str:
    user32 = ctypes.windll.user32
    try:
        if user32.SetProcessDpiAwarenessContext(
                _DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2):
            return "per-monitor-v2"
    except AttributeError:
        pass
    try:
        ctypes.windll.shcore.SetProcessDpiAwareness(2)  # PER_MONITOR
        return "per-monitor"
    except (AttributeError, OSError):
        return "unaware"


DPI_MODE = _enable_dpi_awareness()

import win32api  # noqa: E402  (after DPI awareness on purpose)
import win32con  # noqa: E402
import win32gui  # noqa: E402
import win32process  # noqa: E402

_user32 = ctypes.windll.user32
_dwmapi = ctypes.windll.dwmapi
_DWMWA_EXTENDED_FRAME_BOUNDS = 9
_PW_RENDERFULLCONTENT = 0x00000002
_ENUM_CURRENT_SETTINGS = -1


# --- small value types --------------------------------------------------------
@dataclass
class Rect:
    left: int
    top: int
    right: int
    bottom: int

    @property
    def width(self) -> int:
        return self.right - self.left

    @property
    def height(self) -> int:
        return self.bottom - self.top

    def intersect(self, other: "Rect") -> "Rect | None":
        r = Rect(max(self.left, other.left), max(self.top, other.top),
                 min(self.right, other.right), min(self.bottom, other.bottom))
        return r if r.width > 0 and r.height > 0 else None

    def contains(self, other: "Rect") -> bool:
        return (self.left <= other.left and self.top <= other.top
                and self.right >= other.right and self.bottom >= other.bottom)

    def to_dict(self) -> dict:
        d = asdict(self)
        d.update(width=self.width, height=self.height)
        return d

    @classmethod
    def of(cls, r) -> "Rect":
        """From a (l, t, r, b) tuple or a pywinauto RECT."""
        if hasattr(r, "left"):
            return cls(int(r.left), int(r.top), int(r.right), int(r.bottom))
        return cls(*(int(v) for v in r))


class PaintError(RuntimeError):
    pass


# --- finding / launching Paint ---------------------------------------------------
def _window_exe(hwnd: int) -> str:
    try:
        _, pid = win32process.GetWindowThreadProcessId(hwnd)
        h = win32api.OpenProcess(
            win32con.PROCESS_QUERY_LIMITED_INFORMATION, False, pid)
        try:
            return os.path.basename(
                win32process.GetModuleFileNameEx(h, 0)).lower()
        finally:
            win32api.CloseHandle(h)
    except win32api.error:
        return ""


def list_paint_windows() -> list[dict]:
    """Top-level visible Paint document windows ("<doc> - Paint")."""
    found: list[dict] = []

    def cb(hwnd, _):
        if not win32gui.IsWindowVisible(hwnd):
            return True
        title = win32gui.GetWindowText(hwnd)
        # menu flyouts etc. are other top-level mspaint.exe windows: skip them
        if title.endswith("Paint") and _window_exe(hwnd) == "mspaint.exe":
            found.append({"hwnd": hwnd, "title": title})
        return True

    win32gui.EnumWindows(cb, None)
    return found


def find_paint(title_prefix: str | None = None) -> int | None:
    for w in list_paint_windows():
        if title_prefix is None or w["title"].startswith(title_prefix):
            return w["hwnd"]
    return None


def blank_canvas_file(width: int, height: int,
                      color: str = "white") -> Path:
    """A blank PNG of exactly width x height. Opening it in Paint gives a canvas
    of exactly that size, with no resize dialog and no drag tricks."""
    from PIL import Image, ImageColor
    folder = Path(tempfile.gettempdir()) / "paintctl"
    folder.mkdir(exist_ok=True)
    path = folder / f"canvas-{width}x{height}.png"
    if path.exists() and not _is_blank(path, (width, height), ImageColor.getrgb(color)):
        # Someone saved over it in Paint (File > Save writes back to this file):
        # keep their picture under another name and start from a blank one.
        path.replace(path.with_name(f"{path.stem}-saved-{time.strftime('%Y%m%d-%H%M%S')}.png"))
    if not path.exists():
        Image.new("RGB", (width, height), color).save(path)
    return path


def _is_blank(path: Path, size: tuple[int, int], rgb: tuple[int, int, int]) -> bool:
    from PIL import Image
    try:
        with Image.open(path) as im:
            if im.size != size:
                return False
            colours = im.convert("RGB").getcolors(2)
            return colours is not None and len(colours) == 1 and colours[0][1] == rgb
    except OSError:
        return False


def launch(image: str | None = None, canvas: tuple[int, int] | None = None,
           timeout: float = 20.0, reuse: bool = True) -> int:
    """Start Paint (optionally opening an image, or a blank canvas of an exact
    size) and return its main window handle."""
    if canvas is not None:
        image = str(blank_canvas_file(*canvas))
    prefix = Path(image).stem if image else None
    if reuse and (hwnd := find_paint(prefix)):
        return hwnd
    before = {w["hwnd"] for w in list_paint_windows()}
    args = ["mspaint"] + ([str(Path(image).resolve())] if image else [])
    subprocess.Popen(args, shell=False)  # resolves the App Execution Alias
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        for w in list_paint_windows():
            if w["hwnd"] in before:  # only accept the window we just started
                continue
            if prefix is None or w["title"].startswith(prefix):
                time.sleep(0.8)  # let the canvas finish initialising
                return w["hwnd"]
        time.sleep(0.2)
    raise PaintError("Paint window did not appear within timeout")


def require_paint(hwnd: int | None = None) -> int:
    hwnd = hwnd or find_paint()
    if not hwnd or not win32gui.IsWindow(hwnd):
        raise PaintError("no Paint window found (run `paintctl launch`)")
    return hwnd


# --- window state ---------------------------------------------------------------
def focus(hwnd: int, timeout: float = 3.0) -> bool:
    if win32gui.IsIconic(hwnd):
        win32gui.ShowWindow(hwnd, win32con.SW_RESTORE)
    deadline = time.monotonic() + timeout
    alt_tapped = False
    while time.monotonic() < deadline:
        try:
            win32gui.SetForegroundWindow(hwnd)
        except win32gui.error:
            pass
        if win32gui.GetForegroundWindow() == hwnd:
            return True
        if not alt_tapped:  # foreground lock workaround (once only)
            alt_tapped = True
            win32api.keybd_event(win32con.VK_MENU, 0, 0, 0)
            win32api.keybd_event(win32con.VK_MENU, 0,
                                 win32con.KEYEVENTF_KEYUP, 0)
            continue
        time.sleep(0.1)
    return win32gui.GetForegroundWindow() == hwnd


def close(hwnd: int, discard: bool = False, timeout: float = 6.0) -> bool:
    """Close a Paint window. If Paint asks to save unsaved changes, discard=True
    answers "Don't save" (the dialog's ``SecondaryButton``, an AutomationId
    that does not depend on the UI language); otherwise the prompt stays open.
    Returns True once the window is hidden: Paint hides it within ~50 ms but
    can take anywhere from 0.3 to 6 s more to destroy it."""
    def gone() -> bool:
        return not (win32gui.IsWindow(hwnd) and win32gui.IsWindowVisible(hwnd))

    win32gui.PostMessage(hwnd, win32con.WM_CLOSE, 0, 0)
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        time.sleep(0.3)
        if gone():
            return True
        if discard:
            try:
                _uia_window(hwnd).child_window(
                    auto_id="SecondaryButton",
                    control_type="Button").wrapper_object().invoke()
            except Exception:
                pass  # no prompt (yet)
    return gone()


def dismiss_clipboard_error(hwnd: int, timeout: float = 1.0) -> bool:
    """Close Paint's modal "Can't complete operation" dialog ("The information
    on the Clipboard can't be inserted into Paint"), if it appears within
    timeout seconds. While it is open every paste fails. Clicks its
    ``CloseButton`` (OK) through UIA, so no keys are sent (DoomPaint's
    approach). The title match is English. Returns True if one was closed."""
    try:
        dlg = _uia_window(hwnd).child_window(title_re=".*complete operation.*",
                                            control_type="Window")
        if not dlg.exists(timeout=timeout):
            return False
        dlg.child_window(auto_id="CloseButton", control_type="Button") \
            .wrapper_object().invoke()
        return True
    except Exception:
        return False


def maximize(hwnd: int) -> None:
    win32gui.ShowWindow(hwnd, win32con.SW_MAXIMIZE)
    time.sleep(0.3)


def restore(hwnd: int) -> None:
    win32gui.ShowWindow(hwnd, win32con.SW_RESTORE)
    time.sleep(0.3)


def set_window_rect(hwnd: int, width: int, height: int,
                    x: int | None = None, y: int | None = None) -> None:
    """Outer window size in physical pixels (GetWindowRect semantics,
    i.e. including Windows 11's invisible resize borders)."""
    if _user32.IsZoomed(hwnd):
        restore(hwnd)
    l, t, _, _ = win32gui.GetWindowRect(hwnd)
    win32gui.SetWindowPos(hwnd, 0, l if x is None else x, t if y is None else y,
                          width, height,
                          win32con.SWP_NOZORDER | win32con.SWP_NOACTIVATE)
    time.sleep(0.3)  # let Paint relayout


def fit_viewport(hwnd: int, width: int, height: int,
                 attempts: int = 5) -> Rect | None:
    """Resize the window until the canvas viewport is width x height pixels.
    Iterates because scrollbars appear/disappear as the viewport changes."""
    vp = None
    for _ in range(attempts):
        vp = viewport_rect(hwnd)
        if vp is None:
            return None
        dw, dh = width - vp.width, height - vp.height
        if dw == 0 and dh == 0:
            return vp
        wl, wt, wr, wb = win32gui.GetWindowRect(hwnd)
        set_window_rect(hwnd, (wr - wl) + dw, (wb - wt) + dh)
    return viewport_rect(hwnd)


# --- measurement -------------------------------------------------------------------
def _uia_window(hwnd: int):
    from pywinauto import Application
    from pywinauto.timings import Timings
    Timings.window_find_timeout = 1.0
    return Application(backend="uia").connect(handle=hwnd).window(handle=hwnd)


def _uia_rect(win, **criteria) -> Rect | None:
    try:
        return Rect.of(win.child_window(**criteria).wrapper_object()
                       .rectangle())
    except Exception:
        return None


def viewport_rect(hwnd: int) -> Rect | None:
    return _uia_rect(_uia_window(hwnd), auto_id="scrollViewer",
                     control_type="Pane")


def canvas_pixel_size(win) -> tuple[int, int] | None:
    try:
        text = win.child_window(auto_id="CanvasSizeTextBlock",
                                control_type="Text").wrapper_object() \
            .window_text()
    except Exception:
        return None
    nums = re.findall(r"\d+", text.replace(" ", "").replace(" ", ""))
    return (int(nums[0]), int(nums[1])) if len(nums) >= 2 else None


def zoom_percent(win) -> int | None:
    try:
        text = win.child_window(auto_id="ZoomValuesComboBox",
                                control_type="ComboBox").wrapper_object() \
            .selected_text()
        nums = re.findall(r"\d+", text)
        return int(nums[0]) if nums else None
    except Exception:
        return None


def scroll_state(win) -> dict | None:
    """Whether Paint lets the viewport scroll, from the UIA ScrollPattern. Not
    scrollable on both axes means the whole canvas fits with no scrollbars."""
    try:
        sp = win.child_window(auto_id="scrollViewer", control_type="Pane") \
            .wrapper_object().iface_scroll
        return {"horizontal": bool(sp.CurrentHorizontallyScrollable),
                "vertical": bool(sp.CurrentVerticallyScrollable)}
    except Exception:
        return None


def _find_child_by_class(hwnd: int, cls: str) -> int | None:
    hits: list[int] = []

    def cb(child, _):
        if win32gui.GetClassName(child) == cls:
            hits.append(child)
            return False
        return True

    try:
        win32gui.EnumChildWindows(hwnd, cb, None)
    except win32gui.error:
        pass  # EnumChildWindows "fails" when the callback stops early
    return hits[0] if hits else None


def _frame_bounds(hwnd: int) -> Rect:
    r = wintypes.RECT()
    hr = _dwmapi.DwmGetWindowAttribute(
        wintypes.HWND(hwnd), _DWMWA_EXTENDED_FRAME_BOUNDS,
        ctypes.byref(r), ctypes.sizeof(r))
    if hr != 0:
        return Rect.of(win32gui.GetWindowRect(hwnd))
    return Rect(r.left, r.top, r.right, r.bottom)


def _monitor(hwnd: int) -> dict:
    mon = win32api.MonitorFromWindow(hwnd, win32con.MONITOR_DEFAULTTONEAREST)
    info = win32api.GetMonitorInfo(mon)
    out = {"device": info.get("Device"),
           "bounds": Rect.of(info["Monitor"]).to_dict(),
           "work_area": Rect.of(info["Work"]).to_dict(),
           "primary": bool(info.get("Flags", 0) & 1)}
    try:
        mode = win32api.EnumDisplaySettings(info["Device"],
                                            _ENUM_CURRENT_SETTINGS)
        out["mode"] = {"width": mode.PelsWidth, "height": mode.PelsHeight,
                       "refresh_hz": mode.DisplayFrequency}
    except Exception:
        pass
    return out


def info(hwnd: int) -> dict:
    """Everything about Paint's geometry, in physical screen pixels."""
    errors: list[str] = []
    dpi = _user32.GetDpiForWindow(hwnd)
    client_w, client_h = win32gui.GetClientRect(hwnd)[2:]
    cx, cy = win32gui.ClientToScreen(hwnd, (0, 0))
    out: dict = {
        "hwnd": hwnd,
        "title": win32gui.GetWindowText(hwnd),
        "dpi_mode": DPI_MODE,
        "dpi": dpi,
        "scale_percent": round(dpi * 100 / 96),
        "maximized": bool(_user32.IsZoomed(hwnd)),
        "minimized": bool(win32gui.IsIconic(hwnd)),
        "foreground": win32gui.GetForegroundWindow() == hwnd,
        "monitor": _monitor(hwnd),
        "window_rect": Rect.of(win32gui.GetWindowRect(hwnd)).to_dict(),
        "visible_frame": _frame_bounds(hwnd).to_dict(),
        "client_rect": Rect(cx, cy, cx + client_w, cy + client_h).to_dict(),
    }
    view = _find_child_by_class(hwnd, "MSPaintView")
    out["mspaintview_rect"] = (Rect.of(win32gui.GetWindowRect(view)).to_dict()
                               if view else None)
    win = _uia_window(hwnd)
    vp = _uia_rect(win, auto_id="scrollViewer", control_type="Pane")
    img = _uia_rect(win, auto_id="image", control_type="Group")
    size = canvas_pixel_size(win)
    zoom = zoom_percent(win)
    scroll = scroll_state(win)
    hints = {
        # Paint's status bar is responsive: in a narrow window (seen at 960
        # logical px) it drops the canvas size text block altogether.
        "canvas_px": " (narrow window? the status bar then hides the canvas "
                     "size: widen Paint)",
    }
    for name, val in (("viewport", vp), ("canvas_on_screen", img),
                      ("canvas_px", size), ("zoom_percent", zoom),
                      ("scrollable", scroll)):
        if val is None:
            errors.append(f"{name}: UIA lookup failed{hints.get(name, '')}")
    out["viewport"] = vp.to_dict() if vp else None          # where the canvas can show
    out["canvas_on_screen"] = img.to_dict() if img else None  # canvas incl. off-screen parts
    out["canvas_px"] = ({"width": size[0], "height": size[1]}
                        if size else None)                   # document pixels
    out["zoom_percent"] = zoom
    out["scrollable"] = scroll  # {"horizontal", "vertical"}: scrollbars shown
    # UIA clips the canvas rect to the viewport, so a canvas that is scrolled
    # or larger than the viewport reports a *smaller* rect, not one that sticks
    # out. Detect that by comparing with the size the zoom implies. The zoom box
    # shows a truncated integer (Fit to window: 121.6% reads "121%"), hence 1%.
    clipped = None
    if size and zoom and img:
        exp_w, exp_h = size[0] * zoom / 100, size[1] * zoom / 100
        clipped = (img.width < exp_w * 0.99 - 1
                   or img.height < exp_h * 0.99 - 1)
        out["zoom_check"] = {"expected_on_screen": [exp_w, exp_h],
                             "actual_on_screen": [img.width, img.height]}
        out["canvas_clipped"] = clipped
        # The real zoom, from the canvas's size on screen. The zoom box can
        # say 100 while this says 99.8 (after Fit to window).
        out["zoom_measured"] = (None if clipped else
                                round(100 * img.width / size[0], 2))
    if vp and img:
        vis = vp.intersect(img)
        out["canvas_visible_on_screen"] = vis.to_dict() if vis else None
        # None = unknown (no canvas size or zoom to check clipping against)
        out["canvas_fully_visible"] = (None if clipped is None
                                       else vp.contains(img) and not clipped)
    if vp and zoom:
        # how many document pixels fit the viewport at the current zoom
        out["viewport_in_canvas_px"] = {
            "width": vp.width * 100 // zoom, "height": vp.height * 100 // zoom}
    out["errors"] = errors
    return out


# --- zoom ------------------------------------------------------------------------
def set_zoom(hwnd: int, zoom: str | int) -> int | None:
    """zoom: an integer percent, or "fit" (Paint's Fit to window)."""
    win = _uia_window(hwnd)
    if str(zoom).lower() == "fit":
        try:
            win.child_window(title="Fit to window",
                             control_type="Button").invoke()
        except Exception as exc:  # localised UI: name differs → use uia-dump
            raise PaintError("Fit to window button not found (localised UI? "
                             "find its AutomationId with `paintctl uia-dump`)"
                             ) from exc
    else:
        # After Fit to window the slider can read exactly 100.0 while the real
        # zoom is 99.8%, and setting the value it already has is a no-op. Step
        # to a neighbour first so the target value always takes.
        slider = win.child_window(auto_id="ZoomSliderControl",
                                  control_type="Slider").wrapper_object()
        target = int(zoom)
        slider.set_value(target + 1 if target < slider.max_value() else target - 1)
        time.sleep(0.15)
        slider.set_value(target)
    time.sleep(0.3)
    return zoom_percent(win)


# --- capture ---------------------------------------------------------------------
def screenshot(hwnd: int, path: str, region: str = "window") -> dict:
    """PrintWindow capture (works while occluded). region: window | client |
    viewport | canvas (visible part of the canvas)."""
    import win32ui
    from PIL import Image
    wl, wt, wr, wb = win32gui.GetWindowRect(hwnd)
    w, h = wr - wl, wb - wt
    hwnd_dc = win32gui.GetWindowDC(hwnd)
    mfc_dc = win32ui.CreateDCFromHandle(hwnd_dc)
    save_dc = mfc_dc.CreateCompatibleDC()
    bmp = win32ui.CreateBitmap()
    try:
        bmp.CreateCompatibleBitmap(mfc_dc, w, h)
        save_dc.SelectObject(bmp)
        if not _user32.PrintWindow(hwnd, save_dc.GetSafeHdc(),
                                   _PW_RENDERFULLCONTENT):
            raise PaintError("PrintWindow failed")
        bits = bmp.GetBitmapBits(True)
        img = Image.frombuffer("RGB", (w, h), bits, "raw", "BGRX", 0, 1)
    finally:
        win32gui.DeleteObject(bmp.GetHandle())
        save_dc.DeleteDC()
        mfc_dc.DeleteDC()
        win32gui.ReleaseDC(hwnd, hwnd_dc)

    data = info(hwnd)
    crop_src = {"window": data["window_rect"], "client": data["client_rect"],
                "viewport": data.get("viewport"),
                "canvas": data.get("canvas_visible_on_screen")}.get(region)
    if crop_src is None:
        raise PaintError(f"region {region!r} unavailable")
    box = (crop_src["left"] - wl, crop_src["top"] - wt,
           crop_src["right"] - wl, crop_src["bottom"] - wt)
    img = img.crop(box)
    img.save(path)
    return {"path": str(Path(path).resolve()), "region": region,
            "width": img.width, "height": img.height}


# --- UIA exploration -----------------------------------------------------------------
# Paint's account button is named "Signed in as <full name>". Dumps get
# committed, so never write the account holder's name. English UI only: check
# dumps from a localised Paint by hand.
_REDACT = [(re.compile(r"^(Signed in as ).+$"), r"\1[redacted]")]


def _redact(name: str) -> str:
    for pattern, repl in _REDACT:
        name = pattern.sub(repl, name)
    return name


def uia_dump(hwnd: int, max_depth: int = 6) -> list[dict]:
    """Flat list of the UIA tree: control type, name, AutomationId, class,
    rectangle. Use it to discover anchors (layers panel, full screen, ...)."""
    from pywinauto.uia_element_info import UIAElementInfo
    rows: list[dict] = []

    def walk(el, depth):
        try:
            r = el.rectangle
            rows.append({"depth": depth, "control_type": el.control_type,
                         "name": _redact(el.name),
                         "automation_id": el.automation_id,
                         "class_name": el.class_name,
                         "rect": Rect.of(r).to_dict()})
        except Exception as exc:
            rows.append({"depth": depth, "error": str(exc)})
            return
        if depth < max_depth:
            for child in el.children():
                walk(child, depth + 1)

    walk(UIAElementInfo(hwnd), 0)
    return rows
