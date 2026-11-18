"""E1: which clipboard image formats does Paint use, and does alpha work?

An OLE IDataObject of our own offers any mix of `PNG` (registered format),
`CF_DIBV5` (32-bit BGRA, alpha mask, sRGB) and `CF_DIB` (24- or 32-bit), and
logs every EnumFormatEtc, QueryGetData and GetData call Paint makes. Paint is
maximised at 100% zoom and in the foreground; pastes are Ctrl+V and commits
are `S` (E14), sent with SendInput.

Tests:
- preference: all formats at once, each carrying a different probe colour, in
  two offering orders. The log and the pasted pixels both say which one Paint
  used.
- alpha: over a committed opaque slide, paste an overlay that is transparent
  except for an opaque red block and a 50%-alpha blue block, one format at a
  time. Shows whether transparent pixels let the slide show through.
- speed: a few opaque slide-sized pastes per format, Ctrl+V -> on screen.

Run from poc/paintctl, so paintctl's venv is used:

    uv run python ../experiments/e1_formats.py [--monitor \\\\.\\DISPLAY1] [--size 2716x1434]

Hands off while it runs. Prints JSON; progress on stderr.
"""
from __future__ import annotations

import argparse
import io
import json
import struct
import sys
import threading
import time

import e6_paste_speed as e6  # same folder
import e11_display_area as e11
import pythoncom
import win32api
import win32clipboard
import win32con
from doompaint_clipserve import _new_enum  # IEnumFORMATETC helper
from paintctl import core
from PIL import Image, ImageGrab
from win32com.server.exception import COMException
from win32com.server.util import wrap

CF_DIB, CF_DIBV5 = 8, 17
CF_PNG = win32clipboard.RegisterClipboardFormat("PNG")
_TYMED_HGLOBAL = pythoncom.TYMED_HGLOBAL
_DV_E_FORMATETC = -2147221404
_E_NOTIMPL = -2147467263
_OLE_E_ADVISENOTSUPPORTED = -2147221501
_STANDARD = {1: "CF_TEXT", 2: "CF_BITMAP", 3: "CF_METAFILEPICT", 8: "CF_DIB",
             13: "CF_UNICODETEXT", 14: "CF_ENHMETAFILE", 15: "CF_HDROP",
             17: "CF_DIBV5"}


def format_name(cf: int) -> str:
    if cf in _STANDARD:
        return _STANDARD[cf]
    try:
        return win32clipboard.GetClipboardFormatName(cf)
    except Exception:
        return f"cf{cf}"


# --- encoders ---------------------------------------------------------------------------
def enc_png(img: Image.Image) -> bytes:
    with io.BytesIO() as buf:
        img.save(buf, "PNG", compress_level=1)  # fast to encode; still lossless
        return buf.getvalue()


def enc_dib24(img: Image.Image) -> bytes:
    with io.BytesIO() as buf:
        img.convert("RGB").save(buf, "BMP")
        return buf.getvalue()[14:]


def enc_dib32(img: Image.Image) -> bytes:
    """BITMAPINFOHEADER, 32 bpp BI_RGB, bottom-up BGRA (alpha in the 4th byte,
    which the format calls reserved)."""
    w, h = img.size
    px = img.convert("RGBA").transpose(Image.Transpose.FLIP_TOP_BOTTOM) \
        .tobytes("raw", "BGRA")
    hdr = struct.pack("<IiiHHIIiiII", 40, w, h, 1, 32, 0, len(px), 3780, 3780, 0, 0)
    return hdr + px


def enc_dibv5(img: Image.Image) -> bytes:
    """BITMAPV5HEADER, 32 bpp BI_BITFIELDS with an alpha mask, sRGB, bottom-up."""
    w, h = img.size
    px = img.convert("RGBA").transpose(Image.Transpose.FLIP_TOP_BOTTOM) \
        .tobytes("raw", "BGRA")
    hdr = struct.pack("<IiiHHIIiiII", 124, w, h, 1, 32, 3, len(px), 3780, 3780, 0, 0)
    hdr += struct.pack("<IIII", 0x00FF0000, 0x0000FF00, 0x000000FF, 0xFF000000)
    hdr += struct.pack("<I", 0x73524742)  # LCS_sRGB
    hdr += b"\0" * 36                     # CIEXYZTRIPLE endpoints
    hdr += struct.pack("<IIIIIII", 0, 0, 0, 4, 0, 0, 0)  # gamma, LCS_GM_IMAGES
    assert len(hdr) == 124
    return hdr + px


ENCODERS = {"PNG": (CF_PNG, enc_png), "CF_DIBV5": (CF_DIBV5, enc_dibv5),
            "CF_DIB": (CF_DIB, enc_dib24), "CF_DIB 32-bit": (CF_DIB, enc_dib32)}


# --- the data object ----------------------------------------------------------------------
class _MultiDataObject:
    _com_interfaces_ = [pythoncom.IID_IDataObject]
    _public_methods_ = ["GetData", "GetDataHere", "QueryGetData",
                        "GetCanonicalFormatEtc", "SetData", "EnumFormatEtc",
                        "DAdvise", "DUnadvise", "EnumDAdvise"]

    def __init__(self, formats, log):
        self._formats = formats  # [(cf, bytes)] in offering order
        self._log = log

    def _note(self, what, cf=None, tymed=None):
        self._log.append({"t": time.perf_counter(), "call": what,
                          "format": format_name(cf) if cf is not None else None,
                          "tymed": tymed})

    def QueryGetData(self, fe):
        self._note("QueryGetData", fe[0], fe[4])
        if any(cf == fe[0] for cf, _ in self._formats) and fe[4] & _TYMED_HGLOBAL:
            return 0
        raise COMException(scode=_DV_E_FORMATETC)

    def GetData(self, fe):
        self._note("GetData", fe[0], fe[4])
        for cf, data in self._formats:
            if cf == fe[0] and fe[4] & _TYMED_HGLOBAL:
                medium = pythoncom.STGMEDIUM()
                medium.set(_TYMED_HGLOBAL, data)
                return medium
        raise COMException(scode=_DV_E_FORMATETC)

    def EnumFormatEtc(self, direction):
        self._note("EnumFormatEtc")
        if direction == 1:  # DATADIR_GET
            return _new_enum([(cf, None, 1, -1, _TYMED_HGLOBAL)
                              for cf, _ in self._formats])
        raise COMException(scode=_E_NOTIMPL)

    def GetDataHere(self, *a):
        self._note("GetDataHere")
        raise COMException(scode=_E_NOTIMPL)

    def GetCanonicalFormatEtc(self, *a):
        raise COMException(scode=_E_NOTIMPL)

    def SetData(self, *a):
        raise COMException(scode=_E_NOTIMPL)

    def DAdvise(self, *a):
        self._note("DAdvise")  # E5 asks whether anything calls this
        raise COMException(scode=_OLE_E_ADVISENOTSUPPORTED)

    def DUnadvise(self, *a):
        raise COMException(scode=_OLE_E_ADVISENOTSUPPORTED)

    def EnumDAdvise(self, *a):
        raise COMException(scode=_OLE_E_ADVISENOTSUPPORTED)


class MultiFormatClipboard:
    """Owns the clipboard with a _MultiDataObject on its own STA thread."""

    def __init__(self):
        self.log: list[dict] = []
        self._tid = None
        self._thread = None
        self._obj = None

    def update(self, formats: list[tuple[int, bytes]]) -> None:
        """Swap the data inside the live object, without touching the
        clipboard (DoomPaint's way: no churn, so no read can be cut off).
        Falls back to offer() when nothing is on the clipboard yet."""
        if self._thread is None or self._obj is None:
            self.offer(formats)
            return
        self.log = []
        self._obj._log = self.log
        self._obj._formats = formats  # one reference swap: atomic under the GIL

    def offer(self, formats: list[tuple[int, bytes]]) -> None:
        """Put a new data object on the clipboard (replaces the previous)."""
        self.stop()
        self.log = []
        ready = threading.Event()

        def run():
            pythoncom.OleInitialize()
            self._obj = _MultiDataObject(formats, self.log)
            do = wrap(self._obj, pythoncom.IID_IDataObject)
            for _ in range(20):
                try:
                    pythoncom.OleSetClipboard(do)
                    break
                except pythoncom.com_error:
                    time.sleep(0.05)
            self._tid = win32api.GetCurrentThreadId()
            ready.set()
            pythoncom.PumpMessages()  # serve Paint's calls until WM_QUIT
            try:
                if pythoncom.OleIsCurrentClipboard(do):
                    pythoncom.OleSetClipboard(None)
            except pythoncom.com_error:
                pass

        self._thread = threading.Thread(target=run, daemon=True, name="e1-clip")
        self._thread.start()
        if not ready.wait(5):
            raise RuntimeError("clipboard thread did not start")

    def stop(self) -> None:
        if self._thread:
            win32api.PostThreadMessage(self._tid, win32con.WM_QUIT, 0, 0)
            self._thread.join(3)
            self._thread = None
            self._obj = None


# --- helpers ----------------------------------------------------------------------------
def solid_probe(base: Image.Image, colour) -> Image.Image:
    img = base.copy().convert("RGBA")
    for x, y in e6.probe_points(*base.size):
        img.paste(colour + (255,), (x, y, x + e6._PROBE, y + e6._PROBE))
    return img


def paste_and_commit(paint, screen, pts, want, timeout=3.0):
    """Ctrl+V, wait for the probe colour, commit with `S`. Returns (visible ms
    or None, whether Paint's clipboard-error dialog appeared and was closed)."""
    t0 = time.perf_counter()
    e6.send_keys(*e6.CTRL_V)
    seen = None
    while time.perf_counter() - t0 < timeout:
        if screen.pixel(*pts[0]) == want:
            seen = time.perf_counter()
            break
    if seen is None:  # a failed read pops a modal dialog: close it
        return None, core.dismiss_clipboard_error(paint, 1.0)
    time.sleep(0.4)
    e6.send_keys(*e6.KEY_S)
    time.sleep(0.6)
    return round((seen - t0) * 1000, 1), False


def summarise_log(log, t0):
    return [{"ms": round((e["t"] - t0) * 1000, 1), "call": e["call"],
             "format": e["format"]} for e in log]


def mean_rgb(img: Image.Image, box) -> list[float]:
    region = img.crop(box).convert("RGB")
    n = region.width * region.height
    sums = [0, 0, 0]
    for count, c in region.getcolors(n) or []:
        for i in range(3):
            sums[i] += c[i] * count
    return [round(s / n, 1) for s in sums]


# --- tests ------------------------------------------------------------------------------
def main() -> int:
    p = argparse.ArgumentParser(description=__doc__,
                                formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--monitor", default=r"\\.\DISPLAY1")
    p.add_argument("--size", default="2716x1434")
    p.add_argument("--speed-n", type=int, default=5)
    p.add_argument("--tests", default="preference,alpha,speed",
                   help="comma-separated subset of: preference, alpha, speed")
    a = p.parse_args()
    tests = set(a.tests.split(","))
    w, h = (int(v) for v in a.size.lower().split("x"))
    mon = next(m for m in e11.monitors() if m["device"] == a.monitor)
    base = e11.test_pattern(w, h)
    clip = MultiFormatClipboard()
    screen = e6.ScreenProbe()
    out: dict = {"size": [w, h], "png_format_id": CF_PNG}
    marks = {"PNG": (0, 200, 0), "CF_DIBV5": (0, 0, 220), "CF_DIB": (220, 0, 0)}
    paint = None
    try:
        paint = e11.open_maximised(mon, w, h)
        info = core.info(paint)
        c = info["canvas_on_screen"]
        P = e11._PAD
        box = (c["left"] - P, c["top"] - P, c["left"] + w + P, c["top"] + h + P)
        pts = [(c["left"] + x + e6._PROBE // 2, c["top"] + y + e6._PROBE // 2)
               for x, y in e6.probe_points(w, h)]
        core.focus(paint)

        # 1. preference: all formats at once, two orders
        out["preference"] = {}
        for order in ((["PNG", "CF_DIBV5", "CF_DIB"], ["CF_DIB", "CF_DIBV5", "PNG"])
                      if "preference" in tests else ()):
            formats = [(ENCODERS[n][0], ENCODERS[n][1](solid_probe(base, marks[n])))
                       for n in order]
            clip.offer(formats)
            t0 = time.perf_counter()
            visible = None
            e6.send_keys(*e6.CTRL_V)
            got = None
            while time.perf_counter() - t0 < 5:
                px = screen.pixel(*pts[0])
                got = next((n for n, m in marks.items() if m == px), None)
                if got:
                    visible = round((time.perf_counter() - t0) * 1000, 1)
                    break
            dialog = False
            if got is None:
                dialog = core.dismiss_clipboard_error(paint, 1.0)
            else:
                time.sleep(0.4)
                e6.send_keys(*e6.KEY_S)
                time.sleep(0.6)
            key = " > ".join(order)
            out["preference"][key] = {"pasted_from": got, "visible_ms": visible,
                                      "error_dialog": dialog,
                                      "calls": summarise_log(clip.log, t0)}
            print(f"preference [{key}]: pasted from {got}; calls (ms after Ctrl+V) "
                  f"{[(e['ms'], e['call'], e['format']) for e in out['preference'][key]['calls']]}",
                  file=sys.stderr, flush=True)

        # 2. alpha: overlay over a committed opaque slide, one format at a time
        overlay = Image.new("RGBA", (w, h), (0, 0, 0, 0))
        red = (w // 8, h // 8, w // 8 + 300, h // 8 + 200)
        blue = (w // 2, h // 8, w // 2 + 300, h // 8 + 200)
        clear = (w // 8, h // 2, w // 8 + 300, h // 2 + 200)
        overlay.paste((255, 0, 0, 255), red)
        overlay.paste((0, 0, 255, 128), blue)
        out["alpha"] = {}
        for name in (("PNG", "CF_DIBV5", "CF_DIB 32-bit") if "alpha" in tests else ()):
            # a fresh opaque slide underneath; its probe colour differs per test
            mark = (1, 2, 3 + len(out["alpha"]))
            clip.offer([(CF_DIB, enc_dib24(solid_probe(base, mark)))])
            base_ms, base_dialog = paste_and_commit(paint, screen, pts, mark)
            cf, enc = ENCODERS[name]
            clip.offer([(cf, enc(overlay))])
            t0 = time.perf_counter()
            e6.send_keys(*e6.CTRL_V)
            time.sleep(1.5)
            dialog = core.dismiss_clipboard_error(paint, 0.5)
            if not dialog:
                e6.send_keys(*e6.KEY_S)
            time.sleep(0.8)
            grab = ImageGrab.grab(bbox=box, all_screens=True).convert("RGB")
            dx, dy = e11.compare(grab, base.convert("RGB"))["offset_from_uia_rect"]
            shot = grab.crop((P + dx, P + dy, P + dx + w, P + dy + h))
            under_blue = mean_rgb(base, blue)
            res = {
                "base_slide_landed": base_ms is not None,
                "error_dialog": dialog or base_dialog,
                "red_block": mean_rgb(shot, red),
                "blue_50pct_block": mean_rgb(shot, blue),
                "blue_block_if_blended": [round(v / 2, 1) for v in under_blue[:2]]
                + [round((under_blue[2] + 255) / 2, 1)],
                "transparent_area": mean_rgb(shot, clear),
                "transparent_area_if_see_through": mean_rgb(base, clear),
                "calls": [(e["call"], e["format"]) for e in clip.log],
            }
            res["see_through"] = all(abs(x - y) <= 2 for x, y in zip(
                res["transparent_area"], res["transparent_area_if_see_through"]))
            out["alpha"][name] = res
            print(f"alpha [{name}]: {json.dumps({k: v for k, v in res.items() if k != 'calls'})}",
                  file=sys.stderr, flush=True)

        # 3. speed per format, opaque slide-sized pastes
        out["speed"] = {}
        for fi, name in enumerate(("CF_DIB", "PNG", "CF_DIBV5") if "speed" in tests else ()):
            cf, enc = ENCODERS[name]
            # probe colours unique per format, so a slide left on screen by the
            # previous format can never count as this one arriving
            col = [e6.colour(k + 100 * (fi + 1)) for k in range(a.speed_n)]
            times, dialogs = [], 0
            t_enc = time.perf_counter()
            datas = [enc(solid_probe(base, col[k])) for k in range(a.speed_n)]
            enc_ms = round((time.perf_counter() - t_enc) * 1000 / a.speed_n)
            for k, data in enumerate(datas):
                clip.offer([(cf, data)])
                ms, dialog = paste_and_commit(paint, screen, pts, col[k])
                times.append(ms)
                dialogs += dialog
            ok = [t for t in times if t is not None]
            out["speed"][name] = {"bytes": len(datas[0]), "encode_ms_each": enc_ms,
                                  "visible_ms": times, "error_dialogs": dialogs,
                                  "median": sorted(ok)[len(ok) // 2] if ok else None}
            print(f"speed [{name}]: {len(datas[0]) / 1e6:.1f} MB, encode {enc_ms} ms, "
                  f"visible {times}, error dialogs {dialogs}", file=sys.stderr, flush=True)
    finally:
        clip.stop()
        screen.close()
        if paint:
            core.dismiss_clipboard_error(paint, 0.5)  # a late one blocks closing
            core.close(paint, discard=True)
    json.dump(out, sys.stdout, indent=2)
    print()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
