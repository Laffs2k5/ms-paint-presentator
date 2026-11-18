"""E22: render one slide at a time, so the show can start early.

A full export renders every slide before the first is usable (E12). This runs
e22_slide_server.mjs (Node, the deck's own toolchain and Playwright, Edge
headless with a throwaway profile) for E12's sample deck in Marp and in
Slidev, and measures: time until the server is ready, time to the first
slide, time per further slide, a jump out of order (the last slide before the
second), and how each image compares with the tool's own export from E12 at
the same size (pixel size, mean difference, pixels off by more than 40).

Needs E12's work folder with its Node tools and E12's scale-mode exports at
2650 px (run e12_render.py or e24_pdf.py first). Run from poc/paintctl:

    uv run python ../experiments/e22_per_slide.py --work <work>

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
import e24_pdf as e24  # compare()

SERVER = Path(__file__).with_name("e22_slide_server.mjs")


def run_mode(mode: str, work: Path, env: dict, width: int, out: Path) -> dict:
    deck = work / (f"marp-{width}x1251" if mode == "marp" else f"slidev-{width}x1251")
    out.mkdir(parents=True, exist_ok=True)
    t0 = time.perf_counter()
    p = subprocess.Popen(["node", str(SERVER), mode, str(deck), str(width), str(e12.EDGE),
                          str(work / "node_modules")], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                         stderr=subprocess.PIPE, text=True, env=env, bufsize=1)

    def reply() -> str:
        line = p.stdout.readline().strip()
        if not line or line.startswith("error"):
            raise RuntimeError(line or p.stderr.read()[-500:])
        return line

    res: dict = {}
    try:
        reply()
        res["ready s"] = round(time.perf_counter() - t0, 2)
        if mode == "marp":
            order = [(1, 0), (4, 0), (2, 0), (3, 0)]
        else:  # slide 3 has three click steps
            order = [(1, 0), (4, 0), (2, 0), (3, 0), (3, 1), (3, 2), (3, 3)]
        shots = []
        for n, k in order:
            f = out / f"{n:03d}-{k}.png"
            p.stdin.write(f"render {n} {k} {f}\n")
            p.stdin.flush()
            ms = int(reply().split()[-1])
            shots.append({"slide": n, "clicks": k, "ms": ms, "file": f})
            if len(shots) == 1:
                res["first slide s"] = round(time.perf_counter() - t0, 2)
        res["per slide ms"] = [s["ms"] for s in shots]
        res["order rendered"] = [f"{s['slide']}.{s['clicks']}" for s in shots]
        comps = []
        for s in shots:
            if mode == "marp":
                ref = deck / "out" / f"deck.{s['slide']:03d}.png"
            else:
                ref = deck / "out" / f"{s['slide']:03d}-{s['clicks'] + 1:02d}.png"
            if ref.exists():
                comps.append({"slide": f"{s['slide']}.{s['clicks']}", **e24.compare(s["file"], ref)})
        res["against the tool's export"] = comps
    finally:
        p.stdin.close()
        p.wait(30)
    res["total s"] = round(time.perf_counter() - t0, 2)
    return res


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--work", required=True, type=Path)
    ap.add_argument("--width", type=int, default=2650)
    a = ap.parse_args()
    work = a.work.resolve()
    profiles = work / "browser-profiles"
    profiles.mkdir(exist_ok=True)
    env = {**os.environ, "TEMP": str(profiles), "TMP": str(profiles)}
    out: dict = {"width": a.width}
    try:
        for mode in ("marp", "slidev"):
            try:
                out[mode] = run_mode(mode, work, env, a.width, work / f"e22-{mode}")
            except Exception as exc:  # noqa: BLE001 - record and go on
                out[mode] = {"error": f"{type(exc).__name__}: {exc}"}
            print(f"{mode}: {json.dumps(out[mode])}", file=sys.stderr, flush=True)
    finally:
        left = e12.leftover_edge(profiles)
        for pid in left:  # ours only: their command line names our folder
            subprocess.run(["taskkill", "/PID", str(pid), "/T", "/F"], capture_output=True)
        out["leftover Edge processes with our profile"] = left
        shutil.rmtree(profiles, ignore_errors=True)
    json.dump(out, sys.stdout, indent=2, default=str)
    print()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
