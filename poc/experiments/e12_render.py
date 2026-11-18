"""E12: render a deck at an exact pixel size measured at start, from source.

The presenter renders every slide at start, at exactly the slide size it
measured (E11), from the source, never by resizing ready-made images (plan:
presenter design). This renders one small sample deck (its text is written for
this test) with three tools, at two sizes that are not standard ones:

- Marp (marp-cli): a theme that sets the slide to the size, `--images png`.
- Slidev: `canvasWidth` and `aspectRatio` set to the size, `export --format
  png --with-clicks` (each click step its own image).
- PowerPoint, through COM: slide size set to the shape, `Slide.Export` with
  the width and height in pixels.

For each: the PNG sizes (must be exact), time per slide, a 1 px box border
measured across the middle of one slide (crisp means 1 px), and the speaker
notes read back.

Marp and Slidev render through the installed Edge, headless. Both start it with
a throwaway profile of their own (Marp: a marp-cli-* folder; Slidev/Playwright:
a non-persistent context) in a temp folder this script points them to, so the
user's Edge profiles and tabs are never touched. Edge processes are never
killed by name: only leftovers whose command line names that folder are
stopped, by process id.

Needs Node with marp-cli, @slidev/cli, @slidev/theme-default and
playwright-chromium installed in --work (see docs/poc-results.md, E12):

    npm install --prefix <work> @marp-team/marp-cli @slidev/cli @slidev/theme-default playwright-chromium

(with PLAYWRIGHT_SKIP_BROWSER_DOWNLOAD=1: Edge is used instead). Run from
poc/paintctl, so paintctl's venv is used:

    uv run python ../experiments/e12_render.py --work <work> [--sizes 1854x875,2650x1250]

PowerPoint is started hidden through COM and quit afterwards unless it was
already running. Prints JSON; progress on stderr.
"""
from __future__ import annotations

import argparse
import json
import os
import re
import shutil
import subprocess
import sys
import time
from pathlib import Path

from PIL import Image

EDGE = Path(os.environ.get("ProgramFiles(x86)", r"C:\Program Files (x86)")) / \
    "Microsoft/Edge/Application/msedge.exe"

# The sample deck: (title, body lines, note). Slide 3 has three click steps,
# slide 4 a 400 x 200 box with a 1 px border in the middle.
SLIDES = [
    ("Paint Presentator", ["A smoke-test deck"], "Opening line: this deck only tests rendering."),
    ("How it works", ["Slides are rendered at start", "Each slide is pasted into Paint",
                      "A keyboard hook takes the keys"], "Three points, one breath each."),
    ("One step at a time", ["First", "Second", "Third"], "Each point appears on a click."),
    ("Fine lines", [], "The box border must stay one pixel wide."),
]
BOX = '<div style="position:absolute;left:calc(50% - 200px);top:calc(50% - 100px);' \
      'width:400px;height:200px;border:1px solid #000;box-sizing:border-box"></div>'


# Two ways to reach a pixel size (--mode):
# - scale: the deck keeps one design canvas in the reference shape (the display
#   area of a 1920 x 1200 screen, E11) and is rendered at a device scale factor.
#   Layout is the same on every screen; only the pixel density changes.
# - canvas: the design canvas is set to the pixel size itself. Text keeps its
#   CSS pixel size, so it gets relatively smaller on bigger screens: a reflow.
MODE = "scale"
REF = (1854, 875)
MARP_DESIGN = (1280, round(1280 * REF[1] / REF[0]))  # 1280 x 604
SLIDEV_CANVAS = 980  # Slidev's default canvas width


def design_size(w: int, h: int, tool: str) -> tuple[int, int]:
    if MODE == "canvas":
        return w, h
    return MARP_DESIGN if tool == "marp" else (SLIDEV_CANVAS, round(SLIDEV_CANVAS * REF[1] / REF[0]))


# --- the deck in each tool's format ----------------------------------------------------------
def marp_source(w: int, h: int) -> tuple[str, str]:
    dw, dh = design_size(w, h, "marp")
    theme = f"/* @theme presentator */\n@import 'default';\nsection {{ width: {dw}px; height: {dh}px; }}\n"
    slides = []
    for i, (title, body, note) in enumerate(SLIDES):
        md = f"# {title}\n\n" + "".join(f"- {line}\n" for line in body)
        if i == 3:
            md += "\n" + BOX + "\n"
        md += f"\n<!-- {note} -->\n"
        slides.append(md)
    front = "---\nmarp: true\ntheme: presentator\n---\n\n"
    return front + "\n---\n\n".join(slides), theme


def slidev_source(w: int, h: int) -> str:
    cw = w if MODE == "canvas" else SLIDEV_CANVAS
    aw, ah = (w, h) if MODE == "canvas" else REF
    head = f"---\ntheme: default\ncanvasWidth: {cw}\naspectRatio: {aw}/{ah}\n" \
           f"title: Paint Presentator\n---\n\n"
    out = []
    for i, (title, body, note) in enumerate(SLIDES):
        md = f"# {title}\n\n"
        if i == 2:
            md += "<v-clicks>\n\n" + "".join(f"- {line}\n" for line in body) + "\n</v-clicks>\n"
        else:
            md += "".join(f"- {line}\n" for line in body)
        if i == 3:
            md += "\n" + BOX + "\n"
        md += f"\n<!--\n{note}\n-->\n"
        out.append(md)
    return head + "\n---\n\n".join(out)


def slidev_notes(src: str) -> list[str]:
    """Slidev's rule: the last HTML comment of a slide is its note."""
    body = src.split("---\n", 2)[2]  # drop the headmatter
    return [(re.findall(r"<!--(.*?)-->", s, re.S) or [""])[-1].strip()
            for s in body.split("\n---\n")]


# --- helpers -----------------------------------------------------------------------------------
def png_report(files: list[Path], want: tuple[int, int], box_slide: Path | None,
               box_scale: float = 1.0, layout_slide: Path | None = None) -> dict:
    sizes = sorted({Image.open(f).size for f in files})
    out = {"images": len(files), "sizes": [list(s) for s in sizes],
           "all exact": sizes == [want]}
    if box_slide:
        out["box border widths (px)"] = border_runs(box_slide, box_scale)
    if layout_slide:
        out["slide 2 ink box, fraction of the slide"] = ink_fraction(layout_slide)
    return out


def ink_fraction(path: Path) -> list[float]:
    """Where slide 2's text sits, as fractions of the slide: equal at every
    size means the layout did not change (no reflow)."""
    img = Image.open(path).convert("L")
    b = img.point(lambda v: 255 if v < 200 else 0).getbbox()
    w, h = img.size
    return [round(b[0] / w, 3), round(b[1] / h, 3), round(b[2] / w, 3), round(b[3] / h, 3)]


def border_runs(path: Path, scale: float = 1.0) -> list[int | None]:
    """Widths of the box's left and right borders, measured along a row just
    above its bottom edge (clear of any centred title text): the non-white run
    nearest each expected edge. At scale 1, crisp = 1 px."""
    img = Image.open(path).convert("L")
    w, h = img.size
    y = h // 2 + round(90 * scale)
    row = [img.getpixel((x, y)) for x in range(w)]
    out = []
    for edge in (w // 2 - round(200 * scale), w // 2 + round(199 * scale)):
        hits = [x for x in range(edge - 6, edge + 7) if 0 <= x < w and row[x] < 230]
        out.append(max(hits) - min(hits) + 1 if hits else None)
    return out


def run(cmd: list[str], cwd: Path, env: dict, timeout: int = 600) -> float:
    t0 = time.perf_counter()
    p = subprocess.run(cmd, cwd=cwd, env=env, capture_output=True, text=True, timeout=timeout,
                       encoding="utf-8", errors="replace")
    took = time.perf_counter() - t0
    if p.returncode:
        raise RuntimeError(f"{Path(cmd[0]).name} failed ({p.returncode}): "
                           f"{(p.stderr or p.stdout)[-800:]}")
    return took


def leftover_edge(profiles: Path) -> list[int]:
    """Edge processes whose command line names our throwaway profile folder."""
    ps = ("Get-CimInstance Win32_Process -Filter \"Name='msedge.exe'\" | "
          f"Where-Object {{ $_.CommandLine -like '*{profiles}*' }} | "
          "ForEach-Object { $_.ProcessId }")
    out = subprocess.run(["powershell.exe", "-NoProfile", "-Command", ps],
                         capture_output=True, text=True, timeout=60).stdout
    return [int(x) for x in out.split()]


# --- the tools ----------------------------------------------------------------------------------
def render_marp(work: Path, env: dict, w: int, h: int) -> dict:
    d = work / f"marp-{w}x{h}"
    shutil.rmtree(d, ignore_errors=True)
    (d / "out").mkdir(parents=True)
    deck, theme = marp_source(w, h)
    (d / "deck.md").write_text(deck, encoding="utf-8")
    (d / "theme.css").write_text(theme, encoding="utf-8")
    marp = str(work / "node_modules/.bin/marp.cmd")
    base = [marp, "deck.md", "--html", "--theme-set", "theme.css", "--browser", "edge",
            "--browser-path", str(EDGE)]
    scale = w / design_size(w, h, "marp")[0]
    took = run(base + ["--images", "png", "--image-scale", f"{scale:.6f}",
                       "-o", "out/deck.png"], d, env)
    files = sorted((d / "out").glob("deck.*.png"))
    run(base + ["--notes", "-o", "out/notes.txt"], d, env)
    notes = (d / "out/notes.txt").read_text(encoding="utf-8").strip()
    return {"seconds": round(took, 1), "per slide s": round(took / max(len(files), 1), 2),
            "scale": round(scale, 4),
            **png_report(files, (w, h), files[3] if len(files) > 3 else None, scale,
                         files[1] if len(files) > 1 else None),
            "notes": [n.strip() for n in notes.split("\n\n") if n.strip() not in ("", "---")]}


def render_slidev(work: Path, env: dict, w: int, h: int) -> dict:
    d = work / f"slidev-{w}x{h}"
    shutil.rmtree(d, ignore_errors=True)
    d.mkdir(parents=True)
    src = slidev_source(w, h)
    (d / "slides.md").write_text(src, encoding="utf-8")
    slidev = str(work / "node_modules/.bin/slidev.cmd")
    scale = w / design_size(w, h, "slidev")[0]  # PNG export would default to 2
    took = run([slidev, "export", f"slidev-{w}x{h}/slides.md", "--format", "png",
                "--with-clicks", "--scale", f"{scale:.6f}",
                "--output", f"slidev-{w}x{h}/out",
                "--executable-path", str(EDGE), "--timeout", "60000"], work, env)
    files = sorted((d / "out").glob("*.png"), key=lambda p: [int(t) if t.isdigit() else t
                                                              for t in re.split(r"(\d+)", p.name)])
    return {"seconds": round(took, 1), "per image s": round(took / max(len(files), 1), 2),
            "scale": round(scale, 4), "files": [f.name for f in files],
            **png_report(files, (w, h), files[-1] if files else None, scale,
                         files[1] if len(files) > 1 else None),
            "notes": slidev_notes(src)}


def render_pptx(work: Path, w: int, h: int, pdf: Path | None = None) -> dict:
    """With `pdf`, the deck is also saved there as PDF (for E24)."""
    import pythoncom
    import win32com.client

    d = work / f"pptx-{w}x{h}"
    shutil.rmtree(d, ignore_errors=True)
    d.mkdir(parents=True)
    pythoncom.CoInitialize()
    running = subprocess.run(["tasklist", "/FI", "IMAGENAME eq POWERPNT.EXE"],
                             capture_output=True, text=True).stdout.count("POWERPNT") > 0
    t_start = time.perf_counter()
    app = win32com.client.Dispatch("PowerPoint.Application")
    pres = app.Presentations.Add(0)  # WithWindow = msoFalse
    started = time.perf_counter() - t_start
    try:
        aw, ah = (w, h) if MODE == "canvas" else REF  # a slide is vector: it scales
        pres.PageSetup.SlideWidth = 960
        pres.PageSetup.SlideHeight = round(960 * ah / aw, 2)
        title_layout = pres.SlideMaster.CustomLayouts(1)
        content_layout = pres.SlideMaster.CustomLayouts(2)
        for i, (title, body, note) in enumerate(SLIDES):
            if i == 0:
                s = pres.Slides.AddSlide(i + 1, title_layout)
                s.Shapes.Placeholders(2).TextFrame.TextRange.Text = body[0]
            else:
                s = pres.Slides.AddSlide(i + 1, content_layout)
                if body:
                    s.Shapes.Placeholders(2).TextFrame.TextRange.Text = "\r".join(body)
                else:
                    s.Shapes.Placeholders(2).Delete()
            s.Shapes.Title.TextFrame.TextRange.Text = title
            if i == 3:  # 400 x 200 px box with a 1 px border, centred
                pw, ph = pres.PageSetup.SlideWidth, pres.PageSetup.SlideHeight
                bw, bh = 400 * pw / w, 200 * pw / w
                box = s.Shapes.AddShape(1, (pw - bw) / 2, (ph - bh) / 2, bw, bh)
                box.Fill.Visible = 0
                box.Line.ForeColor.RGB = 0
                box.Line.Weight = pw / w  # 1 px at this export size, in points
            s.NotesPage.Shapes.Placeholders(2).TextFrame.TextRange.Text = note
        t0 = time.perf_counter()
        files = []
        for i in range(1, pres.Slides.Count + 1):
            f = d / f"slide{i}.png"
            pres.Slides(i).Export(str(f), "PNG", w, h)
            files.append(f)
        took = time.perf_counter() - t0
        notes = [pres.Slides(i).NotesPage.Shapes.Placeholders(2).TextFrame.TextRange.Text
                 for i in range(1, pres.Slides.Count + 1)]
        if pdf:
            pres.SaveAs(str(pdf), 32)  # ppSaveAsPDF
    finally:
        pres.Close()
        if not running:
            app.Quit()
    return {"seconds": round(took, 1), "per slide s": round(took / len(files), 2),
            "PowerPoint start s": round(started, 1),
            **png_report(files, (w, h), files[3], layout_slide=files[1]), "notes": notes,
            "PowerPoint was already running": running}


def main() -> int:
    global MODE
    p = argparse.ArgumentParser(description=__doc__,
                                formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--work", required=True, type=Path)
    p.add_argument("--mode", choices=("scale", "canvas"), default="scale")
    p.add_argument("--sizes", help="default: 1854x875 and the laptop's slide size; in scale "
                                   "mode the height must follow the reference shape")
    p.add_argument("--tools", default="marp,slidev,pptx")
    p.add_argument("--repeat", type=int, default=1,
                   help="repeat the sample slides, for render time on a longer deck")
    a = p.parse_args()
    MODE = a.mode
    if a.repeat > 1:
        SLIDES[:] = SLIDES * a.repeat
    a.sizes = a.sizes or ("1854x875,2650x1251" if MODE == "scale" else "1854x875,2650x1250")
    work = a.work.resolve()
    profiles = work / "browser-profiles"  # Edge's throwaway profiles land here
    profiles.mkdir(exist_ok=True)
    env = {**os.environ, "TEMP": str(profiles), "TMP": str(profiles),
           "PLAYWRIGHT_SKIP_BROWSER_DOWNLOAD": "1"}
    out: dict = {"edge": str(EDGE), "mode": MODE}
    for size in a.sizes.split(","):
        w, h = map(int, size.split("x"))
        for tool in a.tools.split(","):
            try:
                if tool == "marp":
                    res = render_marp(work, env, w, h)
                elif tool == "slidev":
                    res = render_slidev(work, env, w, h)
                else:
                    res = render_pptx(work, w, h)
            except Exception as exc:  # noqa: BLE001 - record and go on
                res = {"error": f"{type(exc).__name__}: {exc}"}
            out[f"{tool} {size}"] = res
            print(f"{tool} {size}: {json.dumps(res)}", file=sys.stderr, flush=True)
    left = leftover_edge(profiles)
    out["leftover Edge processes with our profile"] = left
    for pid in left:  # ours only: their command line names our folder
        subprocess.run(["taskkill", "/PID", str(pid), "/T", "/F"], capture_output=True)
    out["profiles left in the temp folder"] = sorted(p.name for p in profiles.iterdir())
    json.dump(out, sys.stdout, indent=2, ensure_ascii=False)
    print()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
