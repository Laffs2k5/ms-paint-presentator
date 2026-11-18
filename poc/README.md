# POC code

Code for the experiments in the [POC plan](../docs/poc-plan.md). Results go in
[docs/poc-results.md](../docs/poc-results.md).

- [`paintctl/`](paintctl): instrumentation for Paint (launch, size, measure,
  screenshot, close). Its README covers installing, including Windows on ARM64.
- [`experiments/`](experiments): one script per experiment. They import
  `paintctl`, so run them with its venv.

```powershell
cd poc\paintctl
uv sync                     # on ARM64: see paintctl's README first
uv run python ..\experiments\e11_display_area.py --out $env:TEMP\e11
```

| Experiment | Script | What it prints |
|---|---|---|
| E11 display area | [`e11_display_area.py`](experiments/e11_display_area.py) | Per monitor: display area, slide size (`slide_rule` = viewport − 66), pixel test |
| E14 commit a paste | [`e14_commit_paste.py`](experiments/e14_commit_paste.py) | Per commit method: clean or not, how fast, mouse moved |
| E6 paste speed | [`e6_paste_speed.py`](experiments/e6_paste_speed.py) | Per slide size: Ctrl+V → GetData and Ctrl+V → on screen (p50/p95/max). `--trigger wm-command` and `--background` for E2's route |
| E1 clipboard formats | [`e1_formats.py`](experiments/e1_formats.py) | Which format Paint reads (call log), alpha per format, paste speed per format. Closes the error dialog itself |
| E7 undo history | [`e7_undo_history.py`](experiments/e7_undo_history.py) | Pastes N slides, then walks Ctrl+Z and Ctrl+Y: depth, step time, Paint's memory |
| E4 UI Automation survey | [`e4_uia_survey.py`](experiments/e4_uia_survey.py) | Menus, drop-downs, Layers panel, zoom: type, AutomationId, name, patterns, toggle state |
| E8 clipboard history | [`e8_clipboard_history.py`](experiments/e8_clipboard_history.py) + [`clipboard_history.ps1`](experiments/clipboard_history.ps1) | Per opt-out format: does the slide land in Win+V history, does Paint still paste |
| E5 DAdvise | [`e5_dadvise.py`](experiments/e5_dadvise.py) | Every IDataObject call per phase (offered, pasted, focus round-trip, pasted again), DAdvise refused or accepted |
| E2/E3 background paste | [`e2_background_paste.py`](experiments/e2_background_paste.py) | Per candidate: does Paint paste (and commit) while another window keeps focus |
| E15 drawing | [`e15_draw.py`](experiments/e15_draw.py) | Per input route (mouse, synthetic pen, posted messages) and speed: does a stroke land on its path, live; rapid strokes; drawing with another window in front; pen pressure per brush |
| E16 tools and colours | [`e16_tools.py`](experiments/e16_tools.py) | How each tool, brush, shape, colour, size and the text font can be chosen by script, the palette's RGB, and what Paint keeps after a relaunch (it restores what it changed) |
| E17 placing a paste | [`e17_place_paste.py`](experiments/e17_place_paste.py) | Where a small paste lands, how arrow keys and a pen drag move it, an alpha stamp moved over a background, paste time by stamp size |
| E10 keys | [`e10_keys.py`](experiments/e10_keys.py) | A low-level keyboard hook: swallowing the show keys while Paint is in front, keys for other apps, key-to-slide latency, what a stalled callback does, the heartbeat, the `RegisterHotKey` fallback. `--listen 30` prints a presenter remote's keys |
| E12 rendering | [`e12_render.py`](experiments/e12_render.py) | A sample deck rendered by Marp, Slidev and PowerPoint at given pixel sizes: exact sizes, layout per size, border width, notes, render time. Needs the Node tools installed in a temp folder (see its docstring); Edge runs headless with throwaway profiles |

[`experiments/doompaint_clipserve.py`](experiments/doompaint_clipserve.py) is
DoomPaint's live OLE clipboard (MIT, © Mark Russinovich), vendored unchanged
with its licence notice. E6 uses it.

## Before running an experiment

- **Keep your hands off the keyboard and mouse.** The scripts open, move and
  close real Paint windows, and send Ctrl+V and mouse clicks to them.
- **Don't run them with the screen locked.** Windows blocks synthetic input,
  focus changes and screen grabs on a locked session, so results are wrong or
  the script fails.
- **For timing runs, turn off clipboard sharing** in PowerToys Mouse Without
  Borders. It reads every image put on the clipboard.
- **Experiments put slides into your clipboard history (Win+V).** Every slide
  offered as a new `CF_DIB` clipboard item can be recorded, and history keeps
  only 25 items, so a run can push out your own. Turn clipboard history off
  while experimenting, or accept that. Do not clean up with the WinRT delete
  API: after `DeleteItemFromHistory` on one item the whole history was gone (E8).
- **E16 changes Paint's text font** (the one setting Paint keeps across
  sessions) and puts it back, even when it fails. If a run is killed, check the
  font of the Text tool in Paint.
- **Don't commit screen grabs.** Notification toasts that pop up during a run
  (Teams, Outlook) end up in them, with names and addresses.
