# The presenter

`presenter.exe` shows a slide deck live in Microsoft Paint. It is the real
product, written in C# on .NET 9 as [research 05](../docs/research/05-presenter-architecture.md)
proposes, after the Python POC (`poc/`) answered how each part works. The
POC stays as the reference and test harness.

> Status: a whole show runs in Paint on Windows 11 ARM64 (Paint
> 11.2609.441). Screen picker, the loading painting, slides by key, display
> changes, `doctor`. Decks: a folder of slide images for now; the renderers
> for PowerPoint, PDF, Marp and Slidev are being added (`Presenter.Slides`).

## Build

Needs the .NET 9 SDK. Native on ARM64 and x64.

```powershell
dotnet build Presenter.sln
dotnet test Presenter.sln
src\Presenter.Cli\bin\Debug\net9.0-windows10.0.19041.0\presenter.exe --help
```

## Use

```powershell
presenter doctor                 # check this machine first: about half a minute, opens Paint
presenter screens                # the displays and the slide size each would get
presenter pick                   # the screen picker on its own
presenter present <deck>         # the show
```

`present` options:

| Option | What it does |
|---|---|
| `--monitor N` | Present on display N (or a GDI name like `\\.\DISPLAY2`) without the picker |
| `--pick` | Show the screen picker even with one display (it shows by itself with two or more) |
| `--no-painting` | Skip the loading painting |
| `--title "…"` | The loading painting's title (default: Paint Presentator) |

Keys during the show: Right, Down, Space, Page Down, Enter or N for the next
slide; Left, Up, Backspace, Page Up or P back; Home for the loading painting
(slide 0); End for the last slide; digits then Enter to jump; B or `.` for
a black screen, W or `,` for white (any key comes back); Esc ends the show
and closes Paint without saving.

For testing and demos: `--auto` plays a scripted set of keys, `--verify`
reads every frame back from the screen and compares it with the slide,
`--snapshot file.png` saves the finished painting, `--seed N` repeats a
painting, `--simulate-prep S` paces the painting as if the slides took S
seconds, `--interrupt-after S` presses a key S seconds into the painting,
and `--simulate-display-change S` brings the picker up S seconds into the
show. A test run leaves the user's Paint settings as they were.

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
| A key during the painting | the painting stops within a stroke; slide 1 0.6 s after the key |
| A display change (simulated), same screen chosen | resumed at the same slide in 2.6 s |

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
- **A lost focus loses a paste**: Ctrl+V reaches another window. The stage
  refocuses Paint and tries once more before it reports a change as
  unconfirmed.
