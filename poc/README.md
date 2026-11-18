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
| E2/E3 background paste | [`e2_background_paste.py`](experiments/e2_background_paste.py) | Per candidate: does Paint paste (and commit) while another window keeps focus |

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
- **Don't commit screen grabs.** Notification toasts that pop up during a run
  (Teams, Outlook) end up in them, with names and addresses.
