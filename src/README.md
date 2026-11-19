# The presenter

`presenter.exe` shows a slide deck live in Microsoft Paint. It is the real
product, written in C# on .NET 9 as [research 05](../docs/research/05-presenter-architecture.md)
proposes, after the Python POC (`poc/`) answered how each part works. The
POC stays as the reference and test harness.

> Status: a whole show runs in Paint on Windows 11 ARM64 (Paint
> 11.2609.441), from PowerPoint, PDF, Marp and Slidev decks or a folder of
> slide images: screen picker, the loading painting while the deck renders,
> slides by key, display changes, watchdogs, `doctor`.

Installing a release (no admin rights needed): [docs/install.md](../docs/install.md).

## Build

Needs the .NET 9 SDK. Native on ARM64 and x64.

```powershell
dotnet build Presenter.sln
dotnet test Presenter.sln
src\Presenter.Cli\bin\Debug\net9.0-windows10.0.19041.0\presenter.exe --help
```

One self-contained file to copy to another laptop (no .NET to install;
about 210 MB; use `-r win-x64` for an Intel or AMD laptop):

```powershell
dotnet publish src/Presenter.Cli -c Release -r win-arm64 --self-contained `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

## Use

```powershell
presenter                        # demo mode: the built-in help deck
presenter doctor                 # check this machine first: about half a minute, opens Paint
presenter setup                  # once, for Marp decks: installs marp-cli (needs Node and npm)
presenter screens                # the displays and the slide size each would get
presenter pick                   # the screen picker on its own
presenter render <deck>          # render without Paint (warms the cache); --out folder, --size WxH, --cache folder
presenter present <deck>         # the show
```

A deck is whatever its own tool leaves (research 05 §6): a PowerPoint file
(`.pptx`, `.ppt`; PowerPoint renders it), a PDF (Windows renders it, from
any tool), a Marp Markdown file (`marp: true`), a Slidev project (its own
Slidev), or a folder of slide images. Each slide keeps the deck's shape,
fitted to the screen; animations are not supported (each slide in its final
state); hidden slides are skipped. Rendered slides are cached per deck,
renderer and size in `%LOCALAPPDATA%\PaintPresentator\cache`, so the second
show of a deck starts at once. Details: [Presenter.Slides](Presenter.Slides/README.md).

`present` options:

| Option | What it does |
|---|---|
| `--monitor N` | Present on display N (or a GDI name like `\\.\DISPLAY2`) without the picker |
| (none) | The screen picker comes first, with the pre-flight reminders; Enter takes the highlighted display |
| `--no-painting` | Skip the loading painting |
| `--title "…"` | The loading painting's title (default: the deck's own title; Paint Presentator in demo mode) |

Keys during the show: Right, Down, Space, Page Down, Enter or N for the next
slide; Left, Up, Backspace, Page Up or P back; Home for the loading painting
(slide 0); End for the last slide; digits then Enter to jump; B or `.` for
a black screen, W or `,` for white (any key comes back); Esc ends the show
and closes Paint without saving. Other keys that would change Paint under the slides
(Ctrl combinations such as Ctrl+Z, Delete, the F keys) are swallowed while
Paint is in front; Alt+Tab still works.

For testing and demos: `--auto` plays a scripted set of keys, `--verify`
reads every frame back from the screen and compares it with the slide,
`--snapshot file.png` saves the finished painting, `--seed N` repeats a
painting, `--simulate-prep S` paces the painting as if the slides took S
seconds, `--interrupt-after S` presses a key S seconds into the painting,
and `--simulate-display-change S` brings the picker up S seconds into the
show; `--soak N` presses N random speaker keys (a stability run). A test run leaves the user's Paint settings as they were.

Each show's timings (every slide change, key to slide on screen, resumes
after a display change) are printed as JSON at the end and kept in
`%LOCALAPPDATA%\PaintPresentator\logs` (the newest 50, no deck names).

## Testing display changes (E18)

Needs a second display. Results so far: [poc-results E18](../docs/poc-results.md#e18-display-changes-mid-show-2026-11-19-pass-for-a-display-leaving-and-coming-back-the-rest-still-open).

**Without a cable**, `poc/dotnet/E18Displays` switches a display off and on
(`list`, `detach <N> --for <S>`, `internal --for <S>`, `restore`; Win+P,
Extend brings everything back if it goes wrong). The harness runs a whole
change and answers the picker itself; from `poc/paintctl`, with the
presenter and E18Displays built:

```powershell
uv run python ..\experiments\e18_display_changes.py --deck <deck> --display \\.\DISPLAY3 --scenario back --seconds 12
```

`--scenario other --other \\.\DISPLAY4` picks another display meanwhile,
`--scenario internal` leaves only the laptop panel. Present on a non-primary
display (the primary cannot be switched off), and keep `--soak` long enough
to outlast the change. These drive the real screen.

**By hand**, from a terminal on the laptop's screen:

1. `presenter screens`, then `presenter pick`: both displays listed, the
   external one highlighted, Identify puts a number on each.
2. `presenter present <deck> --verify`: pick the external display, page to
   slide 3.
3. **Pull the cable.** The picker should pop up on the laptop and wait.
   Note where Paint went (`paintctl list`, `paintctl info`).
4. **Plug it back in** while the picker waits: the display should reappear
   in the list. Pick it. Expected: "resuming … Repaste", slide 3 back,
   pixel-exact (the log's `verify` line).
5. Pull it again and this time pick the laptop: "NewSlideSize" if the
   laptop's slide size differs: Paint reopens at the new size and the
   current slide shows, re-fitted.
6. Change the external display's resolution or scaling in Settings during
   a show; then switch duplicate/extend with Win+P.
7. Run E26's `poc/dotnet/E26MonitorId` before and after a replug: the
   EDID identity should stay the same.

What to look for: does the picker always come up, does the show resume at
the right slide, how long does it take (the summary's `resumed` entries),
and does FancyZones move Paint (exclude `mspaint.exe` if it does).

## Layout

| Project | What it holds |
|---|---|
| `Presenter.Core` | Slide geometry (E11, E12), show keys, navigation, the controller, screen choice. No Windows calls; unit-tested in `tests/Presenter.Core.Tests` |
| `Presenter.Windows` | The keyboard hook, tagged keys, the clipboard thread and slide data object, monitors and their identity, the Paint session, the stage, the synthetic pen, Paint's toolbox, the loading painting |
| `Presenter.Slides` | Decks from other tools: recognising them and rendering their slides at a size, with a cache |
| `Presenter.Ui` | The screen picker (WPF) |
| `Presenter.Cli` | `presenter.exe`: `present`, `doctor`, `screens`, `pick` |

Threads, as research 05 §2 lays them out: the hook alone on its thread
(its callback only writes into a ring); the clipboard on an STA thread that
always pumps; the stage, the only thread that touches Paint; the picker on
a WPF thread of its own; the controller on the thread pool.

## Measured on the laptop

| | Result |
|---|---|
| Key to slide on screen, 2718x1283 | 175–190 ms median (the Python POC: 230 ms) |
| Frames read back with `--verify` | pixel-exact, every change |
| `doctor`'s calibration paste | pixel-exact, except the canvas's last row (below) |
| The loading painting | about 26 s, the same picture as the POC's |
| A Marp deck (7 slides), rendered during the painting | 4.0 s; then 190 ms median, every frame pixel-exact |
| A PowerPoint deck (4 slides + 1 hidden), rendered during the painting | 3.8 s, PowerPoint gone after; 200 ms median, pixel-exact |
| A PDF (4 pages) / a cached deck | 0.7 s / at once |
| A Slidev project (4 slides), rendered during the painting | 7.2 s; then 189 ms median, pixel-exact |
| A deck that cannot render (Marp with no marp-cli) | the painting stops, the reason and the install command are shown, Paint closes; exit 1 |
| The user's 23 real PowerPoint decks (E25's), `render` into a throwaway cache | all 23 rendered, 370 slides; 5.0 s median a deck with PowerPoint's start and quit |
| A key during the painting | the painting stops within a stroke; slide 1 0.6 s after the key |
| A display change (simulated), same screen chosen | resumed at the same slide in 2.6 s |
| A soak: 400 random keys (331 changes, bursts combined), every frame verified | all confirmed, worst 695 ms; pixel-exact but for Paint's bottom edge; 96 MB, flat |
| Paint un-maximised / closed during a show | put back in 2.2 s / reopened, slide back in 4.6 s |
| Paint zoomed to 50% during a show; Ctrl+Z and Delete pressed | back at 100% with the slide in about 2 s; the keys changed nothing |
| The x64 build, run under emulation on the ARM64 laptop | `doctor` passes; 189 ms median, every frame pixel-exact |

## Found while building

- **Paint draws a light line over the canvas's last row** on screen
  (227, 227, 227): every other row of a pasted slide is pixel-exact. Too
  thin to matter in a show; `doctor` checks the rows above it.
- **Choosing a tool does not reliably commit a text box** (E27): the text
  was dropped in some runs. The painter commits with a pen tap outside the
  box first.
- **Paint keeps the Text font and its size** across sessions. The painter
  records both before changing them and puts them back after the show; a
  show that dies half way is put right by the next one (or by `doctor`).
- **Notifications land on the slides.** In the first soak run every frame
  was about 87% right, a black screen 91%: something sat over a tenth of
  the canvas the whole time, most likely notification toasts (Do not
  disturb was off; E19). The same soak again was clean. Switch Do not
  disturb on before a show; the picker says so.
- **Paint's bottom edge**: on a dark slide the canvas's last two rows show
  Paint's edge, not the slide.
- **A lost focus loses a paste**: Ctrl+V reaches another window. The stage
  refocuses Paint and tries once more before it reports a change as
  unconfirmed.
