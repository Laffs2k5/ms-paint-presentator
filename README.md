# ms-paint-presentator

Run presentations in Microsoft Paint instead of PowerPoint. Windows 11 only.

Status: **POC in progress.** Environment captured and `paintctl` working on
Windows 11 ARM64, the DoomPaint baseline runs, and E11, E14, E6 and E2/E3 pass. Next: E1, which image formats Paint accepts. See [POC results](docs/poc-results.md).

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

## Tools

- [`poc/paintctl`](poc/paintctl): CLI + MCP server to launch, size, maximise,
  measure, zoom, screenshot and UIA-dump Paint. CLI tested on Windows 11.

## POC

- [POC plan](docs/poc-plan.md): experiments to run on the laptop and the
  decisions they unlock.
- [POC results](docs/poc-results.md): what each step found.
