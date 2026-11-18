"""E24: PDF as the adapter for decks from any tool.

Decks from tools without an adapter of their own (Keynote, Google Slides,
reveal.js through decktape, a tool not chosen yet) can be presented if the
tool exports PDF. Windows renders PDF itself (Windows.Data.Pdf), so this needs
nothing installed: poc/dotnet/E24Pdf renders each page at an exact width.

This exports E12's sample deck to PDF from Marp, Slidev and PowerPoint, renders
each PDF with E24Pdf at the laptop's slide width, and compares every page with
that tool's own PNG export of the same deck at the same size (E12, scale
mode): image size, mean difference per channel, and the share of pixels that
differ by more than 40 in some channel. Slidev's PDF holds each slide's final
state, so it is compared with the last click step of each slide.

Marp and Slidev run through Edge headless with throwaway profiles in the work
folder (as E12); leftover processes that name that folder are stopped, and the
folder is deleted.

Build the C# side first (dotnet build -c Release poc/dotnet/E24Pdf). Needs
E12's Node tools in --work. Run from poc/paintctl:

    uv run python ../experiments/e24_pdf.py --work <work> [--width 2650]

Prints JSON; progress on stderr. Does not touch Paint.
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

import e12_render as e12
from PIL import Image, ImageChops

EXE = Path(__file__).resolve().parents[1] / "dotnet/E24Pdf/bin/Release/net9.0-windows10.0.19041.0/E24Pdf.exe"


def compare(a: Path, b: Path) -> dict:
    ia, ib = Image.open(a).convert("RGB"), Image.open(b).convert("RGB")
    w, h = min(ia.width, ib.width), min(ia.height, ib.height)
    diff = ImageChops.difference(ia.crop((0, 0, w, h)), ib.crop((0, 0, w, h)))
    channels = diff.split()
    mean = sum(sum(i * c for i, c in enumerate(ch.histogram())) for ch in channels) / (3 * w * h)
    big = ImageChops.lighter(ImageChops.lighter(channels[0], channels[1]), channels[2]) \
        .point(lambda v: 255 if v > 40 else 0).histogram()[255]
    return {"pdf size": list(ia.size), "tool png size": list(ib.size),
            "mean diff": round(mean, 2), "pixels off by > 40": f"{100 * big / (w * h):.2f}%"}


def render_pdf(pdf: Path, width: int, out: Path) -> dict:
    t0 = time.perf_counter()
    p = subprocess.run([str(EXE), str(pdf), str(width), str(out)], capture_output=True, text=True,
                       timeout=300)
    if p.returncode:
        raise RuntimeError(p.stderr[-500:])
    res = json.loads(p.stdout)
    res["wall s"] = round(time.perf_counter() - t0, 2)
    return res


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--work", required=True, type=Path)
    ap.add_argument("--width", type=int, default=2650)
    a = ap.parse_args()
    if not EXE.exists():
        raise SystemExit(f"build the C# side first: dotnet build -c Release {EXE.parents[3]}")
    e12.MODE = "scale"
    work = a.work.resolve()
    profiles = work / "browser-profiles"
    profiles.mkdir(exist_ok=True)
    env = {**os.environ, "TEMP": str(profiles), "TMP": str(profiles),
           "PLAYWRIGHT_SKIP_BROWSER_DOWNLOAD": "1"}
    w = a.width
    h = round(w * e12.REF[1] / e12.REF[0])
    out: dict = {"width": w}
    try:
        # Marp: PNGs (E12), then the same deck as PDF
        e12.render_marp(work, env, w, h)
        d = work / f"marp-{w}x{h}"
        e12.run([str(work / "node_modules/.bin/marp.cmd"), "deck.md", "--html", "--theme-set",
                 "theme.css", "--browser", "edge", "--browser-path", str(e12.EDGE),
                 "--pdf", "-o", "out/deck.pdf"], d, env)
        refs = sorted((d / "out").glob("deck.*.png"))
        out["marp"] = {"pdf": render_pdf(d / "out/deck.pdf", w, d / "pdf-pages")}
        out["marp"]["pages"] = [compare(p, r) for p, r in
                                zip(sorted((d / "pdf-pages").glob("*.png")), refs)]
        print(f"marp: {json.dumps(out['marp'])}", file=sys.stderr, flush=True)

        # Slidev: PNGs per click (E12), then PDF (each slide's final state)
        e12.render_slidev(work, env, w, h)
        d = work / f"slidev-{w}x{h}"
        e12.run([str(work / "node_modules/.bin/slidev.cmd"), "export", f"slidev-{w}x{h}/slides.md",
                 "--format", "pdf", "--output", f"slidev-{w}x{h}/out/deck.pdf",
                 "--executable-path", str(e12.EDGE), "--timeout", "60000"], work, env)
        last = {}
        for f in sorted((d / "out").glob("*.png")):
            m = re.match(r"(\d+)-(\d+)\.png", f.name)
            if m:
                last[int(m.group(1))] = f  # the highest click step wins
        refs = [last[k] for k in sorted(last)]
        out["slidev"] = {"pdf": render_pdf(d / "out/deck.pdf", w, d / "pdf-pages")}
        out["slidev"]["pages"] = [compare(p, r) for p, r in
                                  zip(sorted((d / "pdf-pages").glob("*.png")), refs)]
        print(f"slidev: {json.dumps(out['slidev'])}", file=sys.stderr, flush=True)

        # PowerPoint: PNGs through Slide.Export (E12) and SaveAs PDF
        d = work / f"pptx-{w}x{h}"
        pdf = work / "pptx-deck.pdf"
        e12.render_pptx(work, w, h, pdf=pdf)
        refs = [d / f"slide{i}.png" for i in range(1, len(list(d.glob("slide*.png"))) + 1)]
        out["powerpoint"] = {"pdf": render_pdf(pdf, w, d / "pdf-pages")}
        out["powerpoint"]["pages"] = [compare(p, r) for p, r in
                                      zip(sorted((d / "pdf-pages").glob("*.png")), refs)]
        print(f"powerpoint: {json.dumps(out['powerpoint'])}", file=sys.stderr, flush=True)
    finally:
        left = e12.leftover_edge(profiles)
        for pid in left:  # ours only: their command line names our folder
            subprocess.run(["taskkill", "/PID", str(pid), "/T", "/F"], capture_output=True)
        out["leftover Edge processes with our profile"] = left
        shutil.rmtree(profiles, ignore_errors=True)
    json.dump(out, sys.stdout, indent=2)
    print()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
