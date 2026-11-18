"""E25: past PowerPoint decks through Slide.Export.

The presenter must show past PowerPoint decks as they are (research 05 §6).
This builds a synthetic "legacy" deck with the things old decks have:

- 4:3 slides (720 x 540 pt);
- entrance animations on click (Fly In on each bullet, Appear on a shape);
- text in a font this laptop does not have;
- a hidden slide (PowerPoint's own show skips it);
- a picture;

saves it as .pptx and as an old .ppt (PowerPoint 97-2003), reopens each and
exports every slide at the size the laptop's display area allows for 4:3
(E11's rule, height-limited). Recorded per deck: exported sizes and time,
whether animated content shows in the export (the final state, or hidden as
before the first click), what the adapter can read up front (hidden slides,
animation counts, fonts not installed, media), and Slide.Export of a hidden
slide.

The deck's text and picture are made up for the test. Files go to a temp
folder. PowerPoint is started hidden through COM and quit afterwards unless it
was already running. Run from poc/paintctl:

    uv run python ../experiments/e25_legacy_ppt.py [--viewport 2784x1499]

Prints JSON. Does not touch Paint.
"""
from __future__ import annotations

import argparse
import json
import subprocess
import sys
import tempfile
import time
import winreg
from pathlib import Path

from PIL import Image, ImageDraw

MISSING_FONT = "Presentator Missing Font"


def installed_fonts() -> set[str]:
    names = set()
    for root, path in ((winreg.HKEY_LOCAL_MACHINE, r"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts"),
                       (winreg.HKEY_CURRENT_USER, r"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts")):
        try:
            with winreg.OpenKey(root, path) as k:
                i = 0
                while True:
                    try:
                        name = winreg.EnumValue(k, i)[0]
                    except OSError:
                        break
                    names.add(name.split(" (")[0].lower())
                    i += 1
        except OSError:
            pass
    return names


def fit_43(vp_w: int, vp_h: int, margin: int = 66) -> tuple[int, int]:
    """Largest 4:3 inside viewport - margin, viewport - slide even (E11)."""
    aw, ah = vp_w - margin, vp_h - margin
    w = min(aw, ah * 4 // 3)
    h = min(ah, round(w * 3 / 4))
    w -= (vp_w - w) % 2
    h -= (vp_h - h) % 2
    return w, h


def build(app, folder: Path) -> tuple[Path, Path]:
    pres = app.Presentations.Add(0)
    pres.PageSetup.SlideWidth, pres.PageSetup.SlideHeight = 720, 540  # 4:3
    title, content = pres.SlideMaster.CustomLayouts(1), pres.SlideMaster.CustomLayouts(2)
    s = pres.Slides.AddSlide(1, title)
    s.Shapes.Title.TextFrame.TextRange.Text = "An old deck"
    s.Shapes.Placeholders(2).TextFrame.TextRange.Text = "Made up for the test"
    s = pres.Slides.AddSlide(2, content)
    s.Shapes.Title.TextFrame.TextRange.Text = "Builds"
    body = s.Shapes.Placeholders(2)
    body.TextFrame.TextRange.Text = "First point\rSecond point\rThird point"
    seq = s.TimeLine.MainSequence
    seq.AddEffect(body, 2, 2, 1)  # msoAnimEffectFly, by paragraph, on click
    box = s.Shapes.AddShape(1, 520, 380, 150, 100)
    box.Fill.ForeColor.RGB = 0x2020D0  # red-ish (BGR)
    seq.AddEffect(box, 1, 0, 1)  # msoAnimEffectAppear, on click
    s = pres.Slides.AddSlide(3, content)
    s.Shapes.Title.TextFrame.TextRange.Text = "Hidden slide"
    s.SlideShowTransition.Hidden = -1  # msoTrue
    s = pres.Slides.AddSlide(4, content)
    s.Shapes.Title.TextFrame.TextRange.Text = "A font this laptop lacks"
    tr = s.Shapes.Placeholders(2).TextFrame.TextRange
    tr.Text = "This text asks for a missing font"
    tr.Font.Name = MISSING_FONT
    img = Image.new("RGB", (400, 300), (250, 230, 120))
    ImageDraw.Draw(img).ellipse((100, 50, 300, 250), fill=(240, 120, 20))
    pic = folder / "picture.png"
    img.save(pic)
    s.Shapes.AddPicture(str(pic), 0, -1, 480, 300, 200, 150)
    pptx, ppt = folder / "legacy.pptx", folder / "legacy.ppt"
    pres.SaveAs(str(pptx), 24)  # ppSaveAsOpenXMLPresentation
    pres.SaveAs(str(ppt), 1)    # ppSaveAsPresentation (97-2003)
    pres.Close()
    return pptx, ppt


def inspect_and_export(app, path: Path, size: tuple[int, int], out: Path, fonts: set[str]) -> dict:
    t0 = time.perf_counter()
    pres = app.Presentations.Open(str(path), -1, 0, 0)  # ReadOnly, Untitled no, WithWindow no
    opened = time.perf_counter() - t0
    try:
        ps = pres.PageSetup
        slides = []
        t1 = time.perf_counter()
        for i in range(1, pres.Slides.Count + 1):
            s = pres.Slides(i)
            f = out / f"{path.suffix[1:]}-slide{i}.png"
            s.Export(str(f), "PNG", *size)
            media = sum(1 for k in range(1, s.Shapes.Count + 1) if s.Shapes(k).Type == 16)
            slides.append({"slide": i, "hidden": bool(s.SlideShowTransition.Hidden),
                           "animations": s.TimeLine.MainSequence.Count, "media": media,
                           "exported": list(Image.open(f).size)})
        exported = time.perf_counter() - t1
        used_fonts = [pres.Fonts(k).Name for k in range(1, pres.Fonts.Count + 1)]
        return {"open s": round(opened, 2), "export s": round(exported, 2),
                "slide size pt": [ps.SlideWidth, ps.SlideHeight], "slides": slides,
                "fonts used": used_fonts,
                "fonts not installed": [n for n in used_fonts if n.lower() not in fonts]}
    finally:
        pres.Close()


def animated_content_shows(png: Path) -> dict:
    """Slide 2: are the animated bullets and the red box in the export?"""
    img = Image.open(png).convert("RGB")
    w, h = img.size
    box = img.crop((int(w * 520 / 720), int(h * 380 / 540), int(w * 670 / 720), int(h * 480 / 540)))
    red = sum(1 for r, g, b in box.getdata() if r > 150 and g < 80 and b < 80) / (box.width * box.height)
    body = img.crop((int(w * 0.08), int(h * 0.3), int(w * 0.6), int(h * 0.7))).convert("L")
    ink = sum(1 for v in body.getdata() if v < 128) / (body.width * body.height)
    return {"red box share": round(red, 3), "bullet text ink share": round(ink, 4)}


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--viewport", default="2784x1499", help="Paint's viewport on the target screen")
    a = ap.parse_args()
    import pythoncom
    import win32com.client
    vp = tuple(map(int, a.viewport.split("x")))
    size = fit_43(*vp)
    fonts = installed_fonts()
    out: dict = {"viewport": list(vp), "4:3 slide size": list(size)}
    running = "POWERPNT" in subprocess.run(["tasklist", "/FI", "IMAGENAME eq POWERPNT.EXE"],
                                           capture_output=True, text=True).stdout
    pythoncom.CoInitialize()
    app = win32com.client.Dispatch("PowerPoint.Application")
    try:
        with tempfile.TemporaryDirectory() as td:
            folder = Path(td)
            pptx, ppt = build(app, folder)
            for p in (pptx, ppt):
                res = inspect_and_export(app, p, size, folder, fonts)
                res["slide 2 export"] = animated_content_shows(folder / f"{p.suffix[1:]}-slide2.png")
                out[p.suffix] = res
                print(f"{p.suffix}: {json.dumps(res)}", file=sys.stderr, flush=True)
    finally:
        if not running:
            app.Quit()
    out["PowerPoint was already running"] = running
    json.dump(out, sys.stdout, indent=2)
    print()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
