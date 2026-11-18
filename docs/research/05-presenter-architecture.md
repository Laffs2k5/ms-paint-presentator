# Research: the presenter's architecture

How to build the real presenter, now that the POC ([plan](../poc-plan.md),
[results](../poc-results.md)) has answered how each part works. It updates
the sketch in [research 03 §1](03-authoring-rendering-and-pptx.md#1-architecture-sketch),
which assumed slides built ahead into a bundle. The POC moved rendering to
start-up, for the screen actually used.

## TL;DR

- **C# on .NET 9, one process**, native on ARM64 and x64, shipped as a
  self-contained single-file exe. The keyboard hook, the OLE clipboard, UI
  Automation, the synthetic pen and the windows are all first-class there.
  Python's GIL put 16 ms between a key and the hook callback (E10), and a
  long C call could approach the 300 ms Windows allows before it passes the
  key on and stalls every keyboard in the session.
- **Yes: rendering and painting stay apart, and more parts than those two get
  their own thread.** Rendering slides (source to pixels) runs in child
  processes (Node with Edge, or PowerPoint) that the presenter only directs.
  Everything that touches Paint runs on one thread, the **stage**, one command
  at a time, because Paint is a single UI thread that two writers at once
  would confuse. The keyboard hook, the clipboard and the windows each get a
  thread of their own. A **controller** owns the state and is the only part
  that decides.
- **Messages, not shared state:** components talk through queues (`Channel<T>`).
  The hook only enqueues; the controller turns keys into commands; the stage
  runs them; the slide pipeline reports progress.
- **Smarter than the POC:**
  - start the show before every slide is rendered;
  - detect a finished paste by an event instead of reading pixels;
  - a cache keyed by deck, renderer and size;
  - watchdogs for Paint, the hook and the clipboard;
  - monitor identity that survives a cable being pulled;
  - a `doctor` command that reruns the POC's checks.
- The Python POC stays as the reference and test harness. Four short C#
  experiments (E20–E23, below) de-risk the port before the bulk of it.

## 1. What the POC fixed, and what follows for the architecture

| Finding | Consequence |
|---|---|
| Every slide change is a paste plus `S`; Paint never notices the clipboard by itself (E5, E14) | One component owns the paste-and-commit cycle and runs one at a time |
| Paint is one UI thread; UIA actions activate it; drawing needs it in front (E2, E4, E15) | All Paint interaction (pastes, keys, pen strokes, UIA) goes through one queue: the stage |
| Windows waits about 300 ms for a low-level hook, then passes the key on and holds up the keyboard (E10) | The hook callback only enqueues, on a thread that does nothing else, with no allocations |
| Paint reads our clipboard object within about 7 ms of the paste; that call arrives on our STA thread (E1, E6) | The clipboard object lives on its own STA thread that always pumps messages, never blocked by waiting for Paint |
| A clipboard object per slide, or Paint pastes a cached PNG (E7) | Each slide gets a new data object; the PNG bytes are encoded ahead and kept in memory |
| Each screen read waits for a display refresh; 36 reads per slide made the show take seconds (E6, E13) | Finished pastes are detected by an event, or by at most three points chosen in memory |
| Slides render at start for the measured size, from a fixed design canvas at a scale factor, about 2.5–11 s for 40 slides (E12) | Rendering is a background pipeline with progress, a cache and room to start early |
| A display change brings back the picker and waits (decided) | Display events go to the controller, which pauses the show and opens the picker |
| Do not disturb cannot be read; toasts show over Paint (E19) | The pre-flight asks the speaker; no API is relied on |
| Edit colors blocks UIA; a whole-tree walk can hang (E16) | UIA calls have time-outs and target single elements; tree walks only in diagnostics |
| Edge must never touch the user's profiles (user's rule) | Renderers run in a Job Object with a throwaway profile; only our own processes are ever stopped |

## 2. Components

```
                ┌──────────────────────── presenter.exe (C#) ────────────────────────┐
  keyboard ───▶ │ Input thread ─ WH_KEYBOARD_LL + message loop ─▶ key queue          │
                │                                                   │                 │
  displays ───▶ │ Display watcher ─ hidden window ─▶ event queue ──┤                 │
                │                                                   ▼                 │
                │                           Controller (one thread: the state machine)│
                │                            │            │               │           │
                │       commands ◀───────────┘            │               └──▶ UI     │
                │          ▼                              ▼              (WPF thread: │
                │  Stage thread ─ the only writer    Slide pipeline        picker,    │
                │   to Paint: paste+S, pen, keys,     (tasks): source →     presenter │
                │   UIA, focus, self-tests            renderer → cache      view,     │
                │     │                                │                    pre-flight)│
                │     └─ Clipboard STA thread          └─ child processes in a        │
                │        (live OLE data object,           Job Object: Node + Marp or  │
                │         always pumping)                 Slidev + Edge (throwaway    │
                │                                         profile); PowerPoint via COM│
                └─────────────────────────────────────────────────────────────────────┘
                          │ Ctrl+V, S, pen, UIA                     ▲ GetData (OLE)
                          ▼                                         │
                                         Microsoft Paint ───────────┘
```

| Thread or process | Owns | Must never | Why |
|---|---|---|---|
| **Input** (dedicated thread, message loop) | the `WH_KEYBOARD_LL` hook, the heartbeat, the `RegisterHotKey` fallback | allocate, lock, log or wait in the callback | Windows' ~300 ms limit (E10) |
| **Display watcher** (hidden window, may share the UI thread) | `WM_DISPLAYCHANGE`, `WM_DPICHANGED`, `WM_POWERBROADCAST`, the monitor list | act on its own | the user's rule: the picker waits for the speaker |
| **Controller** (one thread, an actor) | state: slide index, slide size, chosen screen, cache handle; key combining; the state machine (§4) | touch Paint or the clipboard directly | one place decides; everything else executes |
| **Stage** (one thread) | the Paint process and window, focus, `SendInput`, synthetic pen, UIA (FlaUI), paste-done detection, the loading painting's strokes | run two Paint operations at once | Paint is one UI thread (E2, E15) |
| **Clipboard** (STA thread, message pump) | `OleSetClipboard`, the live data object per slide (PNG plus the history opt-out, E8) | block | Paint's `GetData` arrives here (E1, E6) |
| **UI** (WPF thread) | screen picker, presenter view, pre-flight checklist, error notices | steal focus from Paint during the show (`WS_EX_NOACTIVATE`) | the hook handles keys; Paint keeps its lit title bar |
| **Slide pipeline** (tasks on the thread pool) | starting renderers, collecting PNGs, crop or pad (E12), encoding, the cache | block the controller | renders take seconds |
| **Renderers** (child processes, Job Object with kill-on-close) | Node + marp-cli or Slidev + headless Edge (throwaway profile); PowerPoint (out-of-process COM, quit if we started it) | outlive the presenter; use the user's Edge profile | E12; the user's rule |

**Why the stage is one thread and not just a lock:** most Paint work is a
sequence that must not interleave: choose a tool, set a colour, drag a stroke,
or offer a slide, `Ctrl+V`, wait, `S`. A queue of whole commands keeps each
sequence intact and makes it easy to cancel what is queued. When the speaker
presses Next during the loading painting, the painting's remaining strokes are
dropped.

## 3. The path of a key press

```
key down ─▶ hook callback: tag check, foreground check, enqueue    ~0.1 ms (E10)
         ─▶ controller: combine queued keys into one target         <1 ms
         ─▶ stage: new data object with that slide's PNG (pre-encoded) on the clipboard thread
         ─▶ SendInput Ctrl+V (tagged as ours) ─▶ Paint GetData       ~7 ms (E6)
         ─▶ Paint decodes and draws                                  ~60 ms + 50–70 ms per MP (E6, E17)
         ─▶ paste-done signal (event; or 3 points, ≤1 refresh)       ≤17 ms
         ─▶ SendInput S (tagged): commit                             (E14)
                                                    total: ~110 ms at 1.6 MP, ~220 ms at 3.5 MP (E10, E13)
```

- **Combining keys:** while a paste runs, more keys only move the target.
  When the stage is free, the controller sends one paste for the final slide
  (E13: four quick Nexts gave two pastes).
- **The tag** in `dwExtraInfo` lets the hook pass the presenter's own keys and
  treat everything else as the speaker (E10).
- **Foreground and background routes** (E2): with Paint in front, use
  `Ctrl+V` and `S`, Paint's public shortcuts. With the presenter view in
  front, post `WM_COMMAND` 0xE125 and post `S` to the input-site window. A
  self-test at start picks the route that works.

## 4. The controller's states

```
 Start ─▶ Picking screen ─▶ Preparing ─▶ Loading ─▶ Ready ─▶ Showing ⇄ Blanked
             ▲   (picker)    (Paint up,   (painting │          │  ▲
             │                measure,     ‖ render)│          │  │ display change
             │                self-tests)           │          ▼  │
             └──────────────── Interrupted (picker waits) ◀────────┘
                                   │ speaker chooses
                                   ▼
                              Resuming: same size → re-paste the current slide
                                        new size → Preparing + Loading (cache first)
 any state ─▶ Ended (Esc, confirmed) ─▶ Paint closed without saving, hook removed
 Paint gone ─▶ relaunch, Preparing, resume at the current slide
```

- **Rendering and painting at once:** during **Loading** the pipeline renders
  while the stage draws the loading painting. Neither waits for the other; the
  progress bar's strokes are queued as progress arrives.
- **Ready** means the painting is done and enough slides are rendered: slide
  1, or the slide to resume at. The rest can still be rendering (§6).

## 5. Display changes, as decided

- **Detection:** the display watcher's messages, then a fresh monitor list.
  Give monitors a stable identity from `QueryDisplayConfig` and
  `DisplayConfigGetDeviceInfo` (the monitor's device path), not the GDI name
  (`\\.\DISPLAY2`), which can change when a cable is replugged **[verify in
  E18]**.
- **The picker:** a non-modal WPF window that stays up and refreshes its list
  on every change. Each display shows its resolution, scaling and predicted
  slide size, and has Identify. The speaker's last choice is remembered by
  monitor identity.
- **After the choice,** compare with what the show was using:

| Situation | Work |
|---|---|
| Same monitor, same resolution, scaling and work area | Move Paint back, maximise, zoom 100%, check the viewport, re-paste the current slide |
| Different slide size, already in the cache | Paint as above, re-measure, then the cached slides |
| New slide size | Re-measure, then a new loading painting while rendering; resume at the current slide |

- **Things E18 must check:**
  - where Paint goes when its monitor disappears;
  - whether it comes back maximised at 100%;
  - whether FancyZones moves it;
  - whether its canvas survives.

## 6. Smarter than the POC

In rough order of value:

1. **Start the show early.** Render per slide, in priority order: the first
   (or resume) slide, then the next few, then the rest. The show starts when
   the painting is done and slide 1 is ready. When the speaker jumps ahead,
   that slide moves to the front of the queue. This needs a renderer that can
   do one slide at a time:
   - **PowerPoint** already can (`Slide.Export`, E12).
   - **Marp and Slidev:** render their HTML once, then screenshot slide by
     slide with Playwright for .NET, driving Edge headless with a throwaway
     profile. Experiment E22.
2. **Detect a finished paste by an event,** not by reading the screen.
   - **UIA:** Paint's Crop button becomes enabled when a floating selection
     exists. DoomPaint watches it; a UIA property-changed event would do the
     same without polling.
   - **Windows.Graphics.Capture:** a frame of Paint's window arrives when its
     content changes.
   - Measure both against the three-point poll in experiment E21.
3. **A cache keyed by what decides the pixels:** the deck's content hash, the
   renderer and its version, the theme and the slide size. Clean it up
   least-recently-used. Optionally pre-build for known sizes, such as a venue
   projector's 1854×875 (the bundle idea from research 03). The start-up
   render stays as the fallback.
4. **Watchdogs:**

   | Failure | Detected by | Response |
   |---|---|---|
   | Hook removed or stuck | heartbeat key unanswered (E10) | reinstall; else `RegisterHotKey` |
   | Paint closed or crashed | process exit | relaunch, re-measure, resume at the current slide |
   | "Can't complete operation" | its window appears | close it (E1), retry the paste once |
   | Paint un-maximised, zoom changed, scrolled | WinEvent location change, UIA zoom value | restore before the next paste |
   | Paint loses the foreground | `EVENT_SYSTEM_FOREGROUND` | the hook stops swallowing (by design); the presenter view shows "click Paint to continue" |
   | A renderer fails | exit code, time-out | show an error slide for that slide; offer another renderer |
   | Paint updated, UIA changed | start-up self-test against E4's table | fall back to keys and pen taps; warn |

5. **Garbage collection off the critical path:** workstation GC with
   background collection, `GCLatencyMode.SustainedLowLatency` during the show,
   and nothing allocated in the hook callback or the paste path. Pre-encode
   every PNG at load.
6. **The loading painting as data:** a short script of commands generated from
   a seed: tool, colour, size, strokes. The stage only executes it. Painting
   becomes testable (replay a seed), can be cut short when the speaker starts,
   and could later come in themes (seasons, the deck's colours). Use the E16
   palette and tools; never Edit colors, and restore the Text font if text is
   used.
7. **`presenter doctor`:** reruns the POC's checks on the current machine. The
   hook swallows (E10), both paste routes work (E2), the E4 checklist holds,
   E11's calibration paste is pixel-exact, the renderers start, and the
   pre-flight passes. It prints one report: the first thing to run on a new
   laptop.
8. **Timing trace:** an `EventSource` that logs every slide change, key to
   on-screen time, and each render. "It felt slow" then has numbers behind it.
   Never log content or names.
9. **Presenter view:** a WPF window on the other screen with
   `WS_EX_NOACTIVATE`: current and next slide from the cache, notes, timer,
   and "n of N". Research 03's phone web page is an alternative for a single
   screen.
10. **Builds:** Slidev's `--with-clicks` already gives one image per step
    (E12); PowerPoint needs PPspliT-style splitting (research 03 §4).

## 7. Language: C# (gate 4)

| | C# / .NET 9 | Python (as in the POC) | Python + a small C# hook process | Rust or C++ |
|---|---|---|---|---|
| Keyboard hook | own thread; GC is the only pause, tunable (§6.5) | GIL: 16 ms with a busy thread (E10); a long C call risks the 300 ms limit | safe | safest |
| OLE clipboard | WinForms `DataObject` with `SetDataObject(obj, false)` keeps a live OLE object; or our own `IDataObject` via source-generated COM | DoomPaint's clipserve, proven | proven | hand-written COM |
| UIA, pen, `SendInput` | FlaUI (UIA3), CsWin32 for P/Invoke | pywinauto, ctypes, proven | mixed | `windows` crate; verbose |
| UI (picker, presenter view) | WPF, native on ARM64 | Tk does not load in the ARM64 Python (E13); would need Win32 by hand | two stacks | most work |
| Shipping | single-file exe, no runtime to install | Python + venv on a corporate laptop | two stacks | single small exe |

**Recommendation:** C#. Keep Python as the POC and harness: its experiments
stay the reference for behaviour and timing, and the `doctor` checks are
ports of them.

Libraries, all compatible with the repo's MIT licence:

- **CsWin32** (MIT): generated P/Invoke for the hook, `SendInput`,
  `InjectSyntheticPointerInput`, GDI paths, display config and the OLE calls.
- **FlaUI** (MIT): UIA3.
- **WPF / WinForms** (MIT, part of .NET).
- **Playwright for .NET** (MIT): only if per-slide HTML rendering is chosen
  (E22).
- **Marp CLI and Slidev** (MIT) run as external tools, not linked.
- **PowerPoint** through late-bound COM, so no interop assembly ships.

## 8. From the POC to C#

| POC (Python) | C# component |
|---|---|
| `paintctl/core.py` (launch, info, zoom, close, dismiss) | Stage: `PaintSession` |
| `e1_formats.py`, `doompaint_clipserve.py` (live data object) | Clipboard thread: `SlideDataObject` |
| `e2_background_paste.py` (posted paste, posted `S`) | Stage: background route |
| `e10_keys.py` (hook, tags, heartbeat, hotkey) | Input thread: `KeyHook` |
| `e11_display_area.py` (monitors, display area rule) | Display watcher + Stage: `Measure` |
| `e12_render.py` (Marp, Slidev, PowerPoint at a size) | Slide pipeline: renderers |
| `e15_draw.py`, `e16_tools.py` (pen, tools, palette) | Stage: `Brush`, `Palette`, `Tools` |
| `e13_presenter.py` (the whole show) | Controller + loading-painting script |

A possible layout:

- `src/Presenter.Core`: controller, state machine, messages, cache. No
  Windows calls, so it can be unit-tested.
- `src/Presenter.Windows`: the input, stage, clipboard and display-watcher
  threads.
- `src/Presenter.Slides`: the renderer adapters and the pipeline.
- `src/Presenter.App`: WPF, the single exe.
- `src/Presenter.Cli`: `doctor`, `render`, and maybe `present`.

## 9. Experiments before the port

| # | Question | Pass |
|---|---|---|
| **E20** | Does a C# live clipboard object work like DoomPaint's? WinForms `DataObject` with `SetDataObject(obj, false)` against our own `IDataObject` (source-generated COM). PNG plus the opt-out, a new object per slide, 200 pastes | Paint pastes every slide, `GetData` within ~10 ms, no error dialog, Win+V untouched |
| **E21** | A finished paste by event: UIA (Crop button enabled) or Windows.Graphics.Capture, against the three-point poll | The event arrives within a frame of the slide showing, with no polling |
| **E22** | One slide at a time: Marp/Slidev HTML screenshotted with Playwright .NET through headless Edge (throwaway profile) at the scale factor | Exact size, same pixels as E12's export, first slide in under a second |
| **E23** | The C# hook under load: allocate heavily, force collections, while timing key to callback | Callback well under 300 ms at all times; heartbeat never misses |

E18 (display changes) still needs a second display; it also checks the
monitor identity from §5.

## 10. Open questions for the user

- **Node on the target laptops:** Marp and Slidev need Node (and Edge, which
  is always there). Is that acceptable, or should PowerPoint be the primary
  renderer, which needs no install where Office is present?
- **Presenter view:** a window on the second screen, or a page on a phone?
- **Where the code goes:** `src/` in this repo next to `poc/`?
