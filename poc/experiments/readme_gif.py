"""Record the README's demo GIF: the whole Paint window, from the moment the
presenter has it maximised on the chosen display, through the loading
painting, to the welcome slide.

Runs `presenter` in demo mode with --monitor (no picker), records the Paint
window only, presses Space a moment after the painting is done, holds on the
welcome slide, then presses Esc. Keys are sent only while Paint is in front.
Drives the real screen: run it only when the screen is free.

    uv run python ..\\experiments\\readme_gif.py --monitor \\\\.\\DISPLAY3 --out ..\\..\\docs\\media\\demo.gif

The GIF is public, so two things never reach it:
- **Other windows.** Paint maximised is not yet Paint on screen: the first
  frames of a run showed the window that was there before (a code editor).
  Recording starts only once Paint is the foreground window, and a moment
  after.
- **The account picture** Paint shows at the right of its menu bar (the
  signed-in Microsoft account). It is painted over in every frame before the
  frame is written.

  Even so, the presenter puts Paint in place by restoring it to a small
  window and maximising it again, which shows what is behind it for a
  moment. So before the GIF is made, every frame's title bar is compared
  with Paint's: the setup at the start is cut up to its last frame that is
  not Paint (those frames are deleted), and a frame that is not Paint later
  on stops the run without a GIF.

`--rebuild <frames folder>` makes the GIF again from kept frames (`--frames`),
with the same masking and checks; `--skip N` drops the first N as well.

Needs the presenter built (Debug) and ffmpeg on PATH or from winget
(Gyan.FFmpeg).
"""
from __future__ import annotations

import argparse
import ctypes
import queue
import shutil
import subprocess
import threading
import time
from pathlib import Path

from paintctl import core  # also makes this process per-monitor DPI aware
from PIL import ImageGrab

ROOT = Path(__file__).resolve().parents[2]
PRESENTER = ROOT / "src/Presenter.Cli/bin/Debug/net9.0-windows10.0.19041.0/presenter.exe"
WINGET_FFMPEG = Path.home() / "AppData/Local/Microsoft/WinGet/Packages"
user32 = ctypes.windll.user32


def ffmpeg() -> str:
    found = shutil.which("ffmpeg") or next((str(p) for p in WINGET_FFMPEG.glob("Gyan.FFmpeg*/*/bin/ffmpeg.exe")), None)
    if not found:
        raise SystemExit("ffmpeg not found (winget install Gyan.FFmpeg)")
    return found


def mask_account(img, k: float = 1.0):
    """Paint over the account picture at the right of Paint's menu bar, in
    the bar's own colour. At 100% scaling it sits 38-66 px from the window's
    right edge and 28-58 px down; k is the display's scale (1.0 = 100%, the
    display these GIFs are recorded on)."""
    from PIL import ImageDraw
    x0, x1, y0, y1 = img.width - round(66 * k), img.width - round(38 * k), round(26 * k), round(60 * k)
    colour = img.getpixel((img.width - round(110 * k), round(42 * k)))
    ImageDraw.Draw(img).rectangle((x0, y0, x1, y1), fill=colour)
    return img


def paint_only(files: list[Path], setup: int = 60) -> int:
    """How many frames at the start to cut: up to the last one, among the
    first `setup`, that is not Paint's window (checked by the title bar,
    "canvas-....png - Paint", against the last frame's). The presenter puts
    Paint in place by restoring it to a small window and maximising it again,
    so the first seconds can show whatever was behind it; the GIF starts at
    Paint in place. A frame that is not Paint after that is an error: no GIF."""
    from PIL import Image, ImageChops

    def title(p: Path):
        return Image.open(p).convert("RGB").crop((0, 0, 700, 32))
    ref = title(files[-1])

    def is_paint(p: Path) -> bool:
        d = ImageChops.difference(title(p), ref).convert("L").point(lambda v: 255 if v > 16 else 0)
        return d.histogram()[255] / (700 * 32) <= 0.02
    ok = [is_paint(p) for p in files]
    start = max((k + 1 for k in range(min(setup, len(files))) if not ok[k]), default=0)
    if not all(ok[start:]):
        bad = [files[k].name for k in range(start, len(files)) if not ok[k]]
        raise SystemExit(f"frames that are not Paint after Paint was in place, no GIF made: {bad[:10]}")
    return start


def build_gif(frames: Path, times: list[float], out: Path, fps: float, width: int) -> int:
    """Paint's frames only, each for as long as it was on screen, the last one held 3 s."""
    files = sorted(frames.glob("*.png"))
    skip = paint_only(files)
    for p in files[:skip]:
        p.unlink()  # not Paint: not kept, not in the GIF
    files, times = files[skip:], times[skip:]
    concat = frames / "frames.txt"
    with concat.open("w", encoding="utf-8") as f:
        for k, p in enumerate(files):
            d = (times[k + 1] - times[k]) if k + 1 < len(files) else 3.0
            f.write(f"file '{p.as_posix()}'\nduration {d:.3f}\n")
        f.write(f"file '{files[-1].as_posix()}'\n")
    vf = (f"fps={fps},scale={width}:-1:flags=lanczos,split[a][b];"
          "[a]palettegen=max_colors=128:stats_mode=diff[p];[b][p]paletteuse=dither=bayer:bayer_scale=4:diff_mode=rectangle")
    subprocess.run([ffmpeg(), "-y", "-loglevel", "error", "-f", "concat", "-safe", "0", "-i", str(concat),
                    "-vf", vf, "-loop", "0", str(out)], check=True)
    return len(files)


def rebuild(frames: Path, out: Path, skip: int, frame_seconds: float, fps: float, width: int) -> None:
    files = sorted(frames.glob("*.png"))
    times_file = frames / "times.txt"
    times = [float(t) for t in times_file.read_text().split()] if times_file.exists() \
        else [k * frame_seconds for k in range(len(files))]
    work = frames.with_name(frames.name + ".rebuild")
    if work.exists():
        shutil.rmtree(work)
    work.mkdir()
    from PIL import Image
    for k, p in enumerate(files[skip:]):
        mask_account(Image.open(p).convert("RGB")).save(work / f"{k:05d}.png", compress_level=1)
    used = build_gif(work, times[skip:], out, fps, width)
    shutil.rmtree(work)
    print(f"{out.name}: {used} of {len(files)} frames, {out.stat().st_size / 1e6:.1f} MB")


def tap(vk: int, paint: int, log: list[str]) -> bool:
    """A speaker's key, only while Paint is in front."""
    if user32.GetForegroundWindow() != paint:
        log.append(f"key {vk:#x} skipped: Paint is not in front")
        return False
    user32.keybd_event(vk, 0, 0, 0)
    user32.keybd_event(vk, 0, 2, 0)
    return True


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--monitor", help=r"the display to present on, e.g. \\.\DISPLAY3")
    ap.add_argument("--out", required=True, help="the GIF to write")
    ap.add_argument("--frames", help="keep the frames in this folder (default: next to the GIF, removed after)")
    ap.add_argument("--fps", type=float, default=10)
    ap.add_argument("--width", type=int, default=960, help="the GIF's width")
    ap.add_argument("--seed", type=int, help="the painting's random seed")
    ap.add_argument("--rebuild", help="only make the GIF again, from this frames folder")
    ap.add_argument("--skip", type=int, default=0, help="with --rebuild: drop this many frames at the start")
    ap.add_argument("--frame-seconds", type=float, default=0.1,
                    help="with --rebuild, when the folder has no times.txt: each frame's time on screen")
    a = ap.parse_args()

    out = Path(a.out).resolve()
    if a.rebuild:
        rebuild(Path(a.rebuild).resolve(), out, a.skip, a.frame_seconds, a.fps, a.width)
        return 0
    if not a.monitor:
        raise SystemExit("--monitor is needed to record")
    frames = Path(a.frames).resolve() if a.frames else out.with_suffix(".frames")
    if frames.exists():
        shutil.rmtree(frames)
    frames.mkdir(parents=True)
    log: list[str] = []
    args = [str(PRESENTER), "--monitor", a.monitor] + (["--seed", str(a.seed)] if a.seed is not None else [])
    presenter = subprocess.Popen(args, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, encoding="utf-8")
    lines: list[str] = []
    threading.Thread(target=lambda: [lines.append(l.rstrip()) for l in presenter.stderr], daemon=True).start()

    # Paint maximised on the chosen display: only then is it the presenter's
    deadline = time.time() + 60
    paint = None
    while time.time() < deadline:
        if presenter.poll() is not None:
            raise SystemExit("the presenter ended early:\n" + "\n".join(lines))
        h = core.find_paint()
        if h and user32.IsZoomed(h) and (core._monitor(h)["device"] or "").lower() == a.monitor.lower() \
                and user32.GetForegroundWindow() == h:
            paint = h
            break
        time.sleep(0.05)
    if paint is None:
        raise SystemExit("Paint never came up maximised and in front on " + a.monitor)
    time.sleep(0.6)  # in front is not yet drawn: let Paint paint its window over what was there
    r = core._frame_bounds(paint)
    box = (r.left, r.top, r.right, r.bottom)
    log.append(f"recording Paint at {box}")

    # grab on this thread, write PNGs on another so grabbing keeps its pace
    todo: queue.Queue = queue.Queue()

    def writer():
        while (item := todo.get()) is not None:
            i, img = item
            mask_account(img).save(frames / f"{i:05d}.png", compress_level=1)
    w = threading.Thread(target=writer)
    w.start()
    times: list[float] = []
    t0 = time.perf_counter()
    space_at = stop_at = None
    i = 0
    while True:
        t = time.perf_counter() - t0
        todo.put((i, ImageGrab.grab(bbox=box, all_screens=True)))
        times.append(t)
        i += 1
        if space_at is None and any("Ready:" in l for l in lines):
            space_at = t + 2.5  # a moment to read the tip
        if space_at is not None and stop_at is None and t >= space_at:
            if not tap(0x20, paint, log):
                break
            stop_at = t + 4.5  # the welcome slide
        if stop_at is not None and t >= stop_at:
            break
        if t > 240 or presenter.poll() is not None:
            log.append("stopped early: no Ready, or the presenter ended")
            break
        time.sleep(max(0.0, t0 + i / a.fps - time.perf_counter()))
    todo.put(None)
    w.join()
    tap(0x1B, paint, log)  # Esc: the end of the show, Paint closes
    try:
        presenter.wait(timeout=30)
    except subprocess.TimeoutExpired:
        log.append("the presenter did not end after Esc")
    log.append(f"{i} frames over {times[-1]:.1f} s")

    (frames / "times.txt").write_text("\n".join(f"{t:.3f}" for t in times))
    used = build_gif(frames, times, out, a.fps, a.width)
    log.append(f"{out.name}: {used} of {i} frames (the setup cut), {out.stat().st_size / 1e6:.1f} MB")
    if not a.frames:
        shutil.rmtree(frames)
    print("\n".join(log))
    print("\n".join(l for l in lines if any(k in l for k in ("painting", "Ready", "slide 1", "error", "failed"))))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
