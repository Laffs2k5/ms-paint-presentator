# POC plan: prove it on the Windows 11 laptop

Goal: answer the open questions from the research before choosing an architecture
and a language. Each experiment is small, standalone, and records its result in
`docs/poc-results.md`.

Research it builds on:
[01 streaming](research/01-streaming-frames-into-ms-paint.md) ·
[02 OLE surface](research/02-ole-and-paint-integration-surface.md) ·
[03 authoring/rendering/PPTX](research/03-authoring-rendering-and-pptx.md)

## 0. Environment facts to capture first

```powershell
winver                                                       # Windows build
Get-AppxPackage Microsoft.Paint | Select Name, Version        # Paint build
$PSVersionTable.PSVersion
python --version; node --version; dotnet --list-sdks          # what's allowed on the laptop
# Paint manifest: COM servers, protocols, file associations (doc 02 §3.6)
$loc = (Get-AppxPackage Microsoft.Paint).InstallLocation
Select-String -Path "$loc\AppxManifest.xml" -Pattern 'com:|ComServer|protocol|fileTypeAssociation|AppExecutionAlias'
reg query "HKCR\Paint.Picture" /s
```

Also: is PowerPoint installed? Is there a clipboard manager, DLP agent or EDR that
might object to `SendInput` or keyboard hooks?

## 0b. Instrumentation: get `paintctl` working

[`poc/paintctl`](../poc/paintctl) (see [research 04](research/04-instrumenting-paint.md))
is untested, so fix it first. It is the measuring tape for every experiment below.

```powershell
cd poc\paintctl; uv sync
uv run paintctl launch --canvas 1280x720 --maximize
uv run paintctl info                      # check: viewport, canvas_on_screen, canvas_px, zoom, dpi
uv run paintctl zoom 100; uv run paintctl info
uv run paintctl fit-viewport 1920 1080    # if the screen allows
uv run paintctl screenshot vp.png --region viewport
uv run paintctl uia-dump --depth 10 > docs\uia-paint-<build>.json   # commit this, it feeds E4
claude mcp add paintctl -- uv run --directory $PWD paintctl-mcp      # optional
```

Pass: `info` returns no `errors`, and `zoom_check` expected ≈ actual.

## 1. Baseline: does DoomPaint run?

Clone <https://github.com/markrussinovich/DoomPaint> and run `run.bat`, then
`test_quiet.py`. This confirms the OLE clipboard + Ctrl+V path works on *this*
Paint build. Note which paster the self-test picks (Ctrl+V or UIA menu) and the
`pastes/s` figure.

## 2. Experiments (priority order)

| # | Question | How | Pass |
|---|---|---|---|
| **E6** | How fast is a slide-sized paste? | Live `IDataObject`, paste N distinct frames at 960×540 / 1280×720 / 1920×1080, measure keypress→`GetData`→canvas-changed (`PrintWindow` polling) | p95 ≤ 150 ms at the chosen slide size |
| **E2/E3** | Can we paste **without focus/keystrokes**? | `PostMessage(hwndPaint, WM_COMMAND, 0xE125, 0)` with Paint in the background. If no luck, log `WM_COMMAND` while clicking Edit > Paste to find the real ID | `GetData` called and canvas updated with Paint not foreground |
| **E1** | Which formats does Paint consume? Does alpha work? | Offer `PNG` + `CF_DIBV5` + `CF_DIB`. Log `QueryGetData`/`GetData` order | Format preference recorded. Transparent overlay works or doesn't |
| **E7** | Undo stack as slide history | Paste 50 slides, then Ctrl+Z ×50 / Ctrl+Y ×50. Watch Paint's RAM | Max depth + RAM per step recorded |
| **E4** | What can UIA drive? | Accessibility Insights / `inspect.exe`: toolbar Paste button, Layers panel, View > Full screen, zoom | AutomationIds table recorded |
| **E8** | Keep slides out of Win+V history | Add `ExcludeClipboardContentFromMonitorProcessing` + `CanIncludeInClipboardHistory=0` + `CanUploadToCloudClipboard=0` | Win+V stays clean |
| **E5** | Does anything call `DAdvise`? | Implement it with `CreateDataAdviseHolder` and log | Documented yes or no |
| **E10** | Clicker | Your presenter remote's keycodes via an LL hook, swallowed before Paint | Next/prev/black work, Paint unaffected |
| **E11** | Full-screen look | Paint maximised, ribbon/panels hidden, Fit to window or View > Full screen, on a projector or second screen | Looks presentable, so we know which chrome remains |
| **E12** | Build pipeline smoke test | `npx @slidev/cli export --format png --with-clicks` and `npx @marp-team/marp-cli --images png` on a sample deck, plus a PowerPoint COM `Slide.Export` of a sample `.pptx` | PNGs at the right size, notes extracted |
| **E13** | End-to-end mini presenter | Folder of PNGs → arrow keys → paste. Measure the feel | "Snappy" by your own judgement |

## 3. Suggested POC tooling

- Installs are allowed on the laptop, so use **Python 3.11+ managed by `uv`**.
  Reuse DoomPaint modules directly (MIT; keep the attribution) plus `paintctl` for
  window management and measurement. Least code, fastest answers. Also install
  Node LTS (Slidev/Marp), and optionally the .NET SDK for a later C# host.
- Put POC code under `poc/` in this repo, one script per experiment. Make results
  reproducible with a `poc/README.md`.
- Let Claude Code run on the laptop and drive the experiments through `paintctl`
  (CLI or MCP). It can read the JSON geometry and iterate without you clicking.

## 4. Decision gates after the POC

1. **Slide resolution:** set by E6.
2. **Focus model:** E2 decides between a background paste (free presenter view) and
   Paint in the foreground (no-activate presenter view or phone view).
3. **Back navigation:** undo stack (E7) or re-paste.
4. **Host language:** Python vs C#. C# is the likely pick if E2 needs message
   hooks and we want a single exe.
5. **Authoring:** Slidev primary + Marp + PPTX adapters (per doc 03), unless E12
   surprises us.
