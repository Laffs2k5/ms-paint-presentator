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
- **Decks come from other tools** (the user's rule). A deck is made in, and
  normally presented from, its own tool. The Paint presenter is a second way
  to show the same deck, and must also show past PowerPoint decks. So it reads
  decks as their tools leave them, renders them with the deck's own
  toolchain, and keeps each deck's shape (§6). Node is fine as a dependency.
  There is no presenter view, so no speaker notes are needed.
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
- The Python POC stays as the reference and test harness. Short experiments
  (E20–E25, below) de-risk the port before the bulk of it.

## 1. What the POC fixed, and what follows for the architecture

| Finding | Consequence |
|---|---|
| Every slide change is a paste plus `S`; Paint never notices the clipboard by itself (E5, E14) | One component owns the paste-and-commit cycle and runs one at a time |
| Paint is one UI thread; UIA actions activate it; drawing needs it in front (E2, E4, E15) | All Paint interaction (pastes, keys, pen strokes, UIA) goes through one queue: the stage |
| Windows waits about 300 ms for a low-level hook, then passes the key on and holds up the keyboard (E10) | The hook callback only enqueues, on a thread that does nothing else, with no allocations |
| Paint reads our clipboard object within about 7 ms of the paste; that call arrives on our STA thread (E1, E6) | The clipboard object lives on its own STA thread that always pumps messages, never blocked by waiting for Paint |
| A clipboard object per slide, or Paint pastes a cached PNG (E7) | Each slide gets a new data object; the PNG bytes are encoded ahead and kept in memory |
| Each screen read waits for a display refresh; 36 reads per slide made the show take seconds (E6, E13) | Finished pastes are detected by an event, or by at most three points chosen in memory |
| Slides render at start for the measured size, from the deck's own design canvas at a scale factor, about 2.5–11 s for 40 slides (E12) | Rendering is a background pipeline with progress, a cache and room to start early |
| Decks are made and shown in their own tools; past PowerPoint decks must work (the user) | Adapters per source, the deck's own toolchain, the deck's own shape (§6) |
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
                │   to Paint: paste+S, pen, keys,     (tasks): source →     pre-flight│
                │   UIA, focus, self-tests            adapter → cache       notices)  │
                │     │                                │                              │
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
| **UI** (WPF thread) | screen picker, pre-flight checklist, error notices (no presenter view: the user) | steal focus from Paint during the show | the hook handles keys; Paint keeps its lit title bar |
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
- **Foreground and background routes** (E2): with no presenter view, Paint is
  in front during the show, so `Ctrl+V` and `S`, Paint's public shortcuts, are
  the route. Posting `WM_COMMAND` 0xE125 and `S` to the input-site window is
  the fallback, for example while the picker or a notice has the focus. A
  self-test at start checks both.

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
  1, or the slide to resume at. The rest can still be rendering (§7).

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

## 6. Decks come from other tools

The user's rule: a deck is made in, and normally presented from, its own tool
(a browser in full screen, PowerPoint, whatever that tool does). The Paint
presenter is a second way to show the same deck, for when the nerdy laugh is
wanted, and it must show past PowerPoint decks too. So:

- **No Paint-specific markup.** The presenter reads a deck as its tool leaves
  it. Any hint for the presenter must be something the tool itself ignores.
- **The deck's own toolchain renders it.** A Slidev project is exported by its
  own Slidev, version and theme (its `node_modules`), so Paint shows what the
  browser shows. Node is required, which the user accepts.
- **A slide keeps its deck's shape:** 16:9, 4:3, whatever the deck has. It is
  fitted into Paint's display area and centred with Paint's background around
  it, never stretched or reflowed. The 16:10 reference shape of the
  presentation constraints applies only to decks made for Paint.
- **No presenter view and no speaker notes** (the user).

| Source | Recognised by | Rendered by | Build steps | One slide at a time (early start) |
|---|---|---|---|---|
| **PowerPoint** `.pptx`, `.ppt` (past decks) | the extension | PowerPoint through COM, `Slide.Export` at the size (E12) | not at first: each slide in its final state; PPspliT-style splitting later (research 03 §4) | yes |
| **Slidev** project | Slidev headmatter in `slides.md`, or `@slidev/cli` in `package.json` | the project's own Slidev: `slidev export --format png --with-clicks --scale` (E12) | yes, one image per click | through Playwright (E22) |
| **Marp** Markdown | `marp: true` in the front matter | marp-cli (the deck's own, if it has one): `--images png --image-scale` (E12) | no: Marp's fragments only animate in its HTML | through Playwright (E22) |
| **PDF**, from anything else (Keynote, Google Slides, reveal.js through decktape, a tool not chosen yet) | the extension | Windows' own PDF renderer, `Windows.Data.Pdf`, at the size: nothing to install (E24) | only if the tool exports one page per step | yes |

**The "something else":** Slidev fits the user's description best. It
presents itself full screen in a browser, has clicks for build steps, and
exports one PNG per click at any size (E12). Marp is the simpler choice. PDF
keeps every other tool open.

## 7. Smarter than the POC

In rough order of value:

1. **Start the show early.** Render per slide, in priority order: the first
   (or resume) slide, then the next few, then the rest. The show starts when
   the painting is done and slide 1 is ready. When the speaker jumps ahead,
   that slide moves to the front of the queue. This needs a renderer that can
   do one slide at a time:
   - **PowerPoint** already can (`Slide.Export`, E12).
   - **Marp and Slidev:** a Node slide server on the deck's own toolchain
     and its Playwright (Playwright for .NET has no ARM64 driver for
     Windows), Edge headless with a throwaway profile. E22: the first slide
     at 1.9 s (Marp) or 5 s (Slidev), then 0.15 s or 0.35 s a slide.
   - **PDF:** any page on its own, about 50 ms (E24).
2. **Detect a finished paste without reading the screen.** E21 answered it:
   Paint raises no event, but polling the Crop button's `IsEnabled` over UIA
   (3.4 ms a read) tells the paste as fast as the pixel does, and the commit
   too. The options that were considered:
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
9. **Builds:** Slidev's `--with-clicks` already gives one image per step
   (E12); PowerPoint needs PPspliT-style splitting (research 03 §4); see §6.

## 8. Language: C# (gate 4)

| | C# / .NET 9 | Python (as in the POC) | Python + a small C# hook process | Rust or C++ |
|---|---|---|---|---|
| Keyboard hook | own thread; GC is the only pause, tunable (§7.5) | GIL: 16 ms with a busy thread (E10); a long C call risks the 300 ms limit | safe | safest |
| OLE clipboard | WinForms `DataObject` with `SetDataObject(obj, false)` keeps a live OLE object; or our own `IDataObject` via source-generated COM | DoomPaint's clipserve, proven | proven | hand-written COM |
| UIA, pen, `SendInput` | FlaUI (UIA3), CsWin32 for P/Invoke | pywinauto, ctypes, proven | mixed | `windows` crate; verbose |
| UI (picker, pre-flight) | WPF, native on ARM64 | Tk does not load in the ARM64 Python (E13); would need Win32 by hand | two stacks | most work |
| Shipping | single-file exe, no runtime to install | Python + venv on a corporate laptop | two stacks | single small exe |

**Recommendation:** C#. Keep Python as the POC and harness: its experiments
stay the reference for behaviour and timing, and the `doctor` checks are
ports of them.

Libraries, all compatible with the repo's MIT licence:

- **CsWin32** (MIT): generated P/Invoke for the hook, `SendInput`,
  `InjectSyntheticPointerInput`, GDI paths, display config and the OLE calls.
- **FlaUI** (MIT): UIA3.
- **WPF / WinForms** (MIT, part of .NET).
- **Playwright** (Apache-2.0, the copy in each deck's toolchain): used from the
  Node slide server (E22), not linked. Playwright for .NET (MIT) ships no
  ARM64 driver for Windows.
- **Windows.Data.Pdf** through .NET's Windows target (the WinRT projection):
  part of Windows, for the PDF adapter (E24).
- **Marp CLI and Slidev** (MIT) run as external tools, not linked.
- **PowerPoint** through late-bound COM, so no interop assembly ships.

## 9. From the POC to C#

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

Where the code goes (the user left it to us): the product in `src/`, the C#
experiments in `poc/dotnet/` next to the Python ones.

- `src/Presenter.Core`: controller, state machine, messages, cache. No
  Windows calls, so it can be unit-tested.
- `src/Presenter.Windows`: the input, stage, clipboard and display-watcher
  threads.
- `src/Presenter.Slides`: the source adapters and the pipeline.
- `src/Presenter.App`: WPF (picker, pre-flight, notices), the single exe.
- `src/Presenter.Cli`: `doctor`, `render`, and maybe `present`.

## 10. Experiments before the port

| # | Question | Pass |
|---|---|---|
| **E20** | Does a C# live clipboard object work like DoomPaint's? WinForms `DataObject` with `SetDataObject(obj, false)` against our own `IDataObject` (source-generated COM). PNG plus the opt-out, a new object per slide, 200 pastes | Paint pastes every slide, `GetData` within ~10 ms, no error dialog, Win+V untouched |
| **E21** | A finished paste by event: UIA (Crop button enabled) or Windows.Graphics.Capture, against the three-point poll | The event arrives within a frame of the slide showing, with no polling |
| **E22** | One slide at a time: a Slidev or Marp deck's HTML, built by its own toolchain, screenshotted through headless Edge (throwaway profile) at the scale factor | Exact size, same pixels as E12's export, first slide in under a second. **Done in Node, not .NET (no ARM64 driver): same pixels; first slide 1.9 s / 5 s** |
| **E23** | The C# hook under load: allocate heavily, force collections, while timing key to callback | Callback well under 300 ms at all times; heartbeat never misses |
| **E24** | The PDF adapter: decks exported to PDF by Marp, Slidev and PowerPoint, rendered by `Windows.Data.Pdf` at the measured size | Exact size, close to the tool's own PNG, fast enough for start-up |
| **E25** | Past PowerPoint decks: 4:3 and 16:9, animations, embedded video, fonts the laptop lacks, old `.ppt` files. What comes out of `Slide.Export`? | Every slide renders at the size; what is lost (builds, video) is known and reported per deck |

E18 (display changes) still needs a second display; it also checks the
monitor identity from §5.

## 11. Decided by the user

- **No presenter view** (and so no speaker notes).
- **The code lives in this repo:** the product in `src/`, the C# experiments
  in `poc/dotnet/`.
- **Node is fine** as a dependency.
- **Decks come from other tools,** presented natively when no nerdy laugh is
  wanted; past PowerPoint decks must work (§6).
- **Land as much as possible through experiments before building.**
