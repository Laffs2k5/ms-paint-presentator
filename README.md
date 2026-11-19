# ms-paint-presentator

Run presentations in Microsoft Paint instead of PowerPoint. Windows 11 only.

Status: **the POC is done; the presenter works end to end.** The proof
of concept (`poc/`, [plan](docs/poc-plan.md), [results](docs/poc-results.md))
answered how each part works, and the architecture is
[research 05](docs/research/05-presenter-architecture.md). The presenter,
`presenter.exe` in C#, shows decks from PowerPoint, PDF, Marp and Slidev
in Paint: the screen picker, the loading painting
([The sky leaks](docs/loading-painting.md)) while the deck renders, slides
by key at about 190 ms, display changes, watchdogs and a `doctor` check.
See [src/README.md](src/README.md). Still open: E18 (display changes) with
a second display.

## Research

1. [Streaming pictures/video into MS Paint](docs/research/01-streaming-frames-into-ms-paint.md):
   the podcast episode, DoomPaint, slow vs fast clipboard methods, measured frame
   rates, prior art.
2. [OLE in DoomPaint and Paint's integration surface](docs/research/02-ole-and-paint-integration-surface.md):
   the live `IDataObject`, what Paint consumes and exposes over OLE/COM/UIA, and the
   background-paste hypothesis.
3. [Authoring, rendering and PPTX](docs/research/03-authoring-rendering-and-pptx.md):
   build-then-present architecture, the deck bundle format, Slidev/Marp/reveal.js
   adapters, live rendering options, and tiered PPTX import.

4. [Instrumenting Paint](docs/research/04-instrumenting-paint.md): Windows
   automation MCPs vs a purpose-built tool, and how we measure the render area in
   physical pixels.

## The presenter

- [`src/`](src/README.md): `presenter.exe`, its build, use and what was
  measured.
- [Installing](docs/install.md) a release, without admin rights.

## Tools

- [`poc/paintctl`](poc/paintctl): CLI + MCP server to launch, size, maximise,
  measure, zoom, screenshot and UIA-dump Paint. CLI tested on Windows 11.

## POC

- [POC plan](docs/poc-plan.md): experiments to run on the laptop and the
  decisions they unlock.
- [POC results](docs/poc-results.md): what each step found.
