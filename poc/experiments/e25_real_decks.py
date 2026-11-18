"""E25, part 2: real past PowerPoint decks through Slide.Export.

Finds .ppt and .pptx files under the given folders (default: the work
OneDrive and Documents), opens each read-only and without a window, and
reports what matters for presenting it in Paint:

- the slide shape, slide count, hidden slides (skipped, as PowerPoint's show
  does);
- what a static export loses or may get wrong: animations, transitions,
  video or audio, embedded or linked objects, linked pictures; and charts,
  SmartArt and tables, to look out for;
- fonts the deck uses, and which are embedded;
- every visible slide exported at the largest size of the deck's exact shape
  for the target viewport (E11's rule): the time, errors, and slides that
  come out as a single colour.

**The output is anonymous:** decks are numbered, and no file name, title or
text leaves the script. The exported images go to a temporary folder that is
deleted at the end. Each deck runs in its own subprocess with a time limit,
with PowerPoint's alerts off, so a password or a dialog cannot hang the run;
PowerPoint is quit afterwards (and stopped after a time-out) only if it was not
running before.

Run from poc/paintctl:

    uv run python ../experiments/e25_real_decks.py [--viewport 2784x1499] [folders...]

Prints JSON; progress on stderr.
"""
from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys
import tempfile
import time
from pathlib import Path

from PIL import Image


def fit(vp_w: int, vp_h: int, ratio: float, margin: int = 66) -> tuple[int, int, float]:
    """The largest size of the shape `ratio` inside viewport - margin, with
    viewport - slide even on both axes (E11), as close to the shape as
    whole pixels allow. Returns (w, h, aspect error in px)."""
    aw, ah = vp_w - margin, vp_h - margin
    best = None
    for h in range(ah, ah // 2, -1):
        w = round(h * ratio)
        if w > aw or (vp_w - w) % 2 or (vp_h - h) % 2:
            continue
        err = abs(w - h * ratio)
        if err < 0.5:
            return w, h, round(err, 3)
        if best is None or err < best[2]:
            best = (w, h, round(err, 3))
    return best


def shape_name(w: float, h: float) -> str:
    r = w / h
    for name, v in (("16:9", 16 / 9), ("16:10", 1.6), ("4:3", 4 / 3), ("A4", 297 / 210)):
        if abs(r - v) < 0.01:
            return name
    return f"{r:.3f}:1"


def one(path: str, viewport: tuple[int, int], out: Path) -> dict:
    import pythoncom
    import win32com.client
    pythoncom.CoInitialize()
    app = win32com.client.Dispatch("PowerPoint.Application")
    app.DisplayAlerts = 1  # ppAlertsNone
    t0 = time.perf_counter()
    pres = app.Presentations.Open(path, -1, 0, 0)  # ReadOnly, keep title, no window
    res = {"open s": round(time.perf_counter() - t0, 2)}
    try:
        ps = pres.PageSetup
        w_pt, h_pt = ps.SlideWidth, ps.SlideHeight
        res["shape"] = shape_name(w_pt, h_pt)
        size = fit(*viewport, w_pt / h_pt)
        res["export size"] = list(size[:2])
        res["aspect error px"] = size[2]
        n = pres.Slides.Count
        res["slides"] = n
        feats = dict.fromkeys(["hidden", "with animations", "animation effects", "with transitions",
                               "media", "charts", "SmartArt", "tables", "embedded OLE",
                               "linked OLE", "linked pictures"], 0)
        times, errors, single_colour = [], 0, 0
        for i in range(1, n + 1):
            s = pres.Slides(i)
            hidden = bool(s.SlideShowTransition.Hidden)
            feats["hidden"] += hidden
            effects = s.TimeLine.MainSequence.Count
            feats["animation effects"] += effects
            feats["with animations"] += effects > 0
            feats["with transitions"] += s.SlideShowTransition.EntryEffect not in (0, 2000)
            for k in range(1, s.Shapes.Count + 1):
                sh = s.Shapes(k)
                t = sh.Type
                feats["media"] += t == 16
                feats["embedded OLE"] += t == 7
                feats["linked OLE"] += t == 10
                feats["linked pictures"] += t == 11
                try:
                    feats["charts"] += bool(sh.HasChart)
                    feats["SmartArt"] += bool(sh.HasSmartArt)
                    feats["tables"] += bool(sh.HasTable)
                except Exception:  # noqa: BLE001 - some shape types lack these
                    pass
            if hidden:
                continue
            f = out / f"s{i}.png"
            t1 = time.perf_counter()
            try:
                s.Export(str(f), "PNG", size[0], size[1])
                times.append(time.perf_counter() - t1)
                img = Image.open(f)
                if img.size != tuple(size[:2]):
                    errors += 1
                elif len(img.convert("RGB").getcolors(4) or []) == 1:
                    single_colour += 1
            except Exception:  # noqa: BLE001
                errors += 1
            finally:
                f.unlink(missing_ok=True)
        res.update(feats)
        # Font.Embedded raises E_INVALIDARG for some fonts in some decks: count those
        embedded = unreadable = 0
        for k in range(1, pres.Fonts.Count + 1):
            try:
                embedded += bool(pres.Fonts(k).Embedded)
            except Exception:  # noqa: BLE001
                unreadable += 1
        res["fonts"] = pres.Fonts.Count
        res["fonts embedded"] = embedded
        res["fonts not readable"] = unreadable
        res["export errors"] = errors
        res["single-colour slides"] = single_colour
        res["export s per slide"] = round(sum(times) / len(times), 3) if times else None
        res["export s total"] = round(sum(times), 2)
    finally:
        pres.Close()
    return res


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("folders", nargs="*")
    ap.add_argument("--viewport", default="2784x1499")
    ap.add_argument("--timeout", type=int, default=180, help="seconds per deck")
    ap.add_argument("--one", help=argparse.SUPPRESS)  # internal: a single deck
    a = ap.parse_args()
    vp = tuple(map(int, a.viewport.split("x")))
    if a.one:
        with tempfile.TemporaryDirectory() as td:
            print(json.dumps(one(a.one, vp, Path(td))))
        return 0
    folders = a.folders or [p for p in (os.environ.get("OneDriveCommercial"),
                                        str(Path.home() / "Documents")) if p]
    decks = sorted({p.resolve() for f in folders if Path(f).is_dir()
                    for ext in ("*.ppt", "*.pptx") for p in Path(f).rglob(ext)
                    if not p.name.startswith("~$")})
    def powerpoint_pids() -> set[int]:
        rows = subprocess.run(["tasklist", "/FI", "IMAGENAME eq POWERPNT.EXE", "/FO", "CSV", "/NH"],
                              capture_output=True, text=True).stdout.splitlines()
        return {int(r.split('","')[1]) for r in rows if r.startswith('"POWERPNT')}

    before = powerpoint_pids()  # the user's own PowerPoint, if any: never stopped
    ours: set[int] = set()
    out: dict = {"viewport": list(vp), "decks found": len(decks), "decks": []}
    try:
        for i, d in enumerate(decks, 1):
            row = {"deck": i, "ext": d.suffix, "MB": round(d.stat().st_size / 1e6, 1),
                   "year": time.localtime(d.stat().st_mtime).tm_year}
            try:
                p = subprocess.run([sys.executable, __file__, "--one", str(d), "--viewport", a.viewport],
                                   capture_output=True, text=True, timeout=a.timeout)
                lines = p.stdout.strip().splitlines()
                row.update(json.loads(lines[-1]) if p.returncode == 0 and lines
                           else {"error": "failed to open or export"})
            except subprocess.TimeoutExpired:
                row["error"] = f"timed out after {a.timeout} s"
                for pid in (powerpoint_pids() - before):  # only the one we started
                    subprocess.run(["taskkill", "/PID", str(pid), "/F"], capture_output=True)
            if not before:
                ours |= powerpoint_pids()
            out["decks"].append(row)
            print(json.dumps(row), file=sys.stderr, flush=True)
    finally:
        for pid in ours - before:  # quit the PowerPoint we started, gently
            subprocess.run(["taskkill", "/PID", str(pid)], capture_output=True)
    json.dump(out, sys.stdout, indent=2)
    print()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
