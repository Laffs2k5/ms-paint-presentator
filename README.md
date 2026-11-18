# ms-paint-presentator

Run presentations in Microsoft Paint instead of PowerPoint. Windows 11 only.

Status: **POC in progress.** Environment captured and `paintctl` working on
Windows 11 ARM64, the DoomPaint baseline runs, and E11, E14, E6, E2/E3, E1, E7, E4 and E8 pass, E5 answered (nothing calls DAdvise), E15, E16 and E17 pass (a script can draw in Paint, live, choose its tools and colours, and place a small paste exactly), E10 passes for the keyboard (a hook takes the show keys before Paint sees them), and E12 passes (Marp, Slidev and PowerPoint render at a measured size, with notes). The presenter design is decided (see the [POC plan](docs/poc-plan.md)). E19 is answered (toasts show over Paint; only Do not disturb stops them). E13, the end-to-end mini presenter, is built and waiting for a verdict on how it feels; E18 (display changes) needs a second display. The proposed architecture is [research 05](docs/research/05-presenter-architecture.md); next are its experiments E20–E25, before building. See [POC results](docs/poc-results.md).

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
