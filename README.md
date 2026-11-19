# Paint Presentator

### Your next talk. In Microsoft Paint. Live.

Have you ever wanted to walk up to the projector, open MS Paint, and give a
perfectly serious talk in it, while the room slowly works out what is going
on?

Point Paint Presentator at your deck. It opens Paint, paints a little
picture all by itself while your slides load, and then lands every slide in
Paint, pixel for pixel, the moment you press a key. No plugin. No video. No
PowerPoint on the screen. Just Paint. Mostly.

![Paint opens, a picture paints itself while the deck loads, a light bulb says to press Space, and the welcome slide appears](docs/media/demo.gif)

- **Any deck:** PowerPoint, PDF, Marp, Slidev, or a folder of images.
- **Under 200 ms** from key press to slide.
- **A loading painting**, drawn live with a synthetic pen and Paint's own
  brushes, and a progress bar that is real. Then the sky leaks.
- **It holds on:** a display that vanishes mid-talk, a closed or zoomed
  Paint, a stray Ctrl+Z. It puts things back and carries on at your slide.
- **No admin rights** to install.

## Try it

```powershell
irm https://github.com/Laffs2k5/ms-paint-presentator/releases/latest/download/install.ps1 -OutFile install.ps1
.\install.ps1
presenter                      # the built-in demo, in Paint
presenter present talk.pptx    # your own deck
```

Windows 11 with Paint from the Microsoft Store. [Installing](docs/install.md)
has the rest, including work computers that block new programs.

## Made possible by DoomPaint

This project exists because [Mark Russinovich](https://github.com/markrussinovich)
made Paint run DOOM. [**DoomPaint**](https://github.com/markrussinovich/DoomPaint)
showed that one live OLE clipboard object, read by Paint at the moment it
pastes, can feed Paint frames as fast as Doom can draw them. Paint
Presentator borrows that idea, swaps the demons for bullet points, and slows
down to one frame per key press. Thank you, Mark. If Paint can run Doom, it
can run your quarterly review.

## Read more

- [How it works, and what was measured](src/README.md)
- [Behind the scenes](docs/behind-the-scenes.md): the research, the proof of
  concept, and the design of the loading painting
- [Installing](docs/install.md), also on a locked-down work computer

[MIT](LICENSE). Not affiliated with or endorsed by Microsoft. Microsoft Paint
and PowerPoint are trademarks of Microsoft.
