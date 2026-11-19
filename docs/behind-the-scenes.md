# Behind the scenes

Paint Presentator started as a question: can Microsoft Paint be driven fast
and reliably enough to give a whole talk in it? Before any of the presenter
was written, a proof of concept measured each part on a real laptop. This
page collects what came out of it.

## How it works, briefly

Each slide becomes a PNG the exact size of Paint's canvas on the chosen
screen, rendered by the deck's own tool (PowerPoint, a PDF renderer, Marp or
Slidev) and cached. The presenter offers it on the clipboard through a live
OLE data object (the [DoomPaint](https://github.com/markrussinovich/DoomPaint)
technique), presses Ctrl+V and S in Paint, and checks that it landed. The
loading painting is drawn live through Windows' synthetic pen input, picking
Paint's tools over UI Automation. The details, and everything that was
measured, are in [`src/README.md`](../src/README.md).

## Research

1. [Streaming pictures/video into MS Paint](research/01-streaming-frames-into-ms-paint.md):
   the podcast episode, DoomPaint, slow vs fast clipboard methods, measured
   frame rates, prior art.
2. [OLE in DoomPaint and Paint's integration surface](research/02-ole-and-paint-integration-surface.md):
   the live `IDataObject`, what Paint consumes and exposes over OLE/COM/UIA,
   and the background-paste hypothesis.
3. [Authoring, rendering and PPTX](research/03-authoring-rendering-and-pptx.md):
   build-then-present architecture, the deck bundle format,
   Slidev/Marp/reveal.js adapters, live rendering options, and tiered PPTX
   import.
4. [Instrumenting Paint](research/04-instrumenting-paint.md): Windows
   automation MCPs vs a purpose-built tool, and how the render area is
   measured in physical pixels.
5. [The presenter's architecture](research/05-presenter-architecture.md):
   the components, the path of a key press, display changes, decks from
   other tools, and why C#.

## The proof of concept

- [The plan](poc-plan.md): the experiments, and the decisions each one
  unlocks.
- [The results](poc-results.md): what each experiment found, with the
  numbers.
- [`poc/`](../poc): the scripts and small programs that ran them.

## The loading painting

[The sky leaks](loading-painting.md): the design of the picture Paint draws
while the deck renders. Composition, sequence and time budgets, and what had
to change once it met the real Paint.

## Tools

- [`poc/paintctl`](../poc/paintctl): a CLI and MCP server to launch, size,
  maximise, measure, zoom, screenshot and UIA-dump Paint.
- [`poc/experiments/readme_gif.py`](../poc/experiments/readme_gif.py): records
  the README's GIF, keeping everything but Paint's window out of it.
