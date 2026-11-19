using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Presenter.Core;

namespace Presenter.Ui;

/// <summary>One slide of the built-in deck. A line starting with "$ " is a
/// command, set in a monospaced font; one starting with "> " is what to do
/// next, set larger and bold. <paramref name="Kicker"/> is a short line above
/// the title.</summary>
public sealed record HelpSlide(string Title, IReadOnlyList<string> Lines, string? Kicker = null);

/// <summary>
/// The deck the presenter shows when it is run without one (demo mode): how
/// to use it, drawn by the presenter itself at whatever size the screen
/// needs, so it needs no Node, no PowerPoint, nothing installed.
/// </summary>
public static class HelpDeck
{
    public const string Title = "Paint Presentator";

    public static IReadOnlyList<HelpSlide> Slides { get; } =
    [
        new("Paint Presentator", [
            "This tool helps you give your talk in Microsoft Paint: PowerPoint, PDF, Marp or Slidev, "
                + "every slide pasted in live, pixel for pixel, while the room wonders how.",
            "> Press Space for the keys",
            "Esc ends this help deck. Your own deck:",
            "$ presenter present <your deck>",
        ], Kicker: "Welcome to"),
        new("Keys during a show", [
            "Right, Down, Space, Page Down, Enter or N: the next slide",
            "Left, Up, Backspace, Page Up or P: back",
            "Home: the loading painting  ·  End: the last slide",
            "Digits, then Enter: that slide (0 is the painting)",
            "B or full stop: black  ·  W or comma: white  ·  any key: back",
            "Esc: the end. Paint closes without saving.",
        ]),
        new("Your own deck", [
            "$ presenter present talk.pptx",
            "PowerPoint (.pptx, .ppt), PDF, Marp Markdown, a Slidev project, or a folder of images",
            "Rendered by the deck's own tool, at the size this screen needs, and cached",
            "No animations: each slide shows in its final state",
            "Any key during the loading painting goes straight to slide 1",
        ]),
        new("Before a talk", [
            "$ presenter doctor",
            "checks this computer and Paint, in about half a minute",
            "$ presenter render <deck>",
            "renders the deck ahead, so the show starts at once",
            "Switch Do not disturb on: notifications show over the slides",
            "Plug in the charger: Paint is slower on battery",
        ]),
        new("Screens", [
            "The screen picker comes first: a screen's number, or Enter for the highlighted one",
            "Identify shows each screen's number on it",
            "A cable pulled mid-show? The picker comes back and waits.",
            "Plug it in again and pick it: the show carries on where it was",
        ]),
        new("When something goes wrong", [
            "Paint closed, moved, un-maximised or zoomed: the presenter puts it back",
            "Ctrl+Z, Delete and the F keys cannot change Paint during the show",
            "Ctrl+C in the terminal ends the show cleanly",
            "Each show's timings are kept in %LOCALAPPDATA%\\PaintPresentator\\logs",
        ]),
        new("Setting up", [
            "Windows 11 with Paint from the Microsoft Store",
            "Marp decks: Node.js, then",
            "$ presenter setup",
            "Slidev decks: the project's own npm install",
            "PowerPoint decks: PowerPoint",
        ]),
        new("How it works", [
            "Each slide is rendered to a PNG the exact size of Paint's canvas",
            "It goes on the clipboard, then Ctrl+V and S: about 200 ms a slide",
            "The loading painting is drawn live, with a synthetic pen and Paint's own tools",
            "Open source, MIT licence",
        ]),
    ];

    private static readonly Color Background = Color.FromRgb(0xFA, 0xFA, 0xF7);
    private static readonly Color Ink = Color.FromRgb(0x22, 0x22, 0x22);
    private static readonly Color Heading = Color.FromRgb(0x1F, 0x3A, 0x5F);
    private static readonly Color Muted = Color.FromRgb(0x8A, 0x8A, 0x8A);
    private static readonly Color CommandBack = Color.FromRgb(0xEC, 0xEE, 0xF2);

    /// <summary>Slide <paramref name="number"/> (1-based) as a PNG of exactly <paramref name="size"/>.</summary>
    public static byte[] Render(int number, PixelSize size) => OnSta(() => Draw(Slides[number - 1], number, size));

    private static byte[] Draw(HelpSlide slide, int number, PixelSize size)
    {
        double w = size.Width, h = size.Height, margin = 0.07 * w;
        var typeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var bold = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        var mono = new Typeface(new FontFamily("Cascadia Mono, Consolas"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        FormattedText Text(string s, Typeface t, double em, Color c, double maxWidth) =>
            new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, t, em, new SolidColorBrush(c), 1.0)
            { MaxTextWidth = Math.Max(1, maxWidth), Trimming = TextTrimming.None };

        var visual = new DrawingVisual();
        using (var g = visual.RenderOpen())
        {
            g.DrawRectangle(new SolidColorBrush(Background), null, new Rect(0, 0, w, h));
            bool cover = number == 1;
            double titleEm = (cover ? 0.11 : 0.075) * h, bodyEm = 0.042 * h;
            var title = Text(slide.Title, bold, titleEm, Heading, w - 2 * margin);
            double y = cover ? 0.24 * h : 0.09 * h;
            if (slide.Kicker is { } kicker)
            {
                var k = Text(kicker, typeface, 0.05 * h, Muted, w - 2 * margin);
                y = (cover ? 0.15 : 0.06) * h;
                g.DrawText(k, new Point(margin, y));
                y += k.Height;
            }
            g.DrawText(title, new Point(margin, y));
            y += title.Height + 0.05 * h;
            foreach (string line in slide.Lines)
            {
                if (line.StartsWith("> "))
                {
                    var next = Text(line[2..], bold, bodyEm * 1.15, Heading, w - 2 * margin);
                    g.DrawText(next, new Point(margin, y + 0.01 * h));
                    y += next.Height + 0.045 * h;
                }
                else if (line.StartsWith("$ "))
                {
                    var cmd = Text(line[2..], mono, bodyEm * 0.95, Ink, w - 2 * margin - 0.04 * w);
                    double pad = 0.012 * h;
                    g.DrawRoundedRectangle(new SolidColorBrush(CommandBack), null,
                        new Rect(margin, y, cmd.WidthIncludingTrailingWhitespace + 4 * pad, cmd.Height + 2 * pad), pad, pad);
                    g.DrawText(cmd, new Point(margin + 2 * pad, y + pad));
                    y += cmd.Height + 2 * pad + 0.025 * h;
                }
                else
                {
                    double indent = cover ? 0 : 0.03 * w;
                    if (!cover) g.DrawText(Text("•", typeface, bodyEm, Heading, w), new Point(margin, y));
                    var text = Text(line, typeface, bodyEm, Ink, w - 2 * margin - indent);
                    g.DrawText(text, new Point(margin + indent, y));
                    y += text.Height + 0.022 * h;
                }
            }
            var foot = Text($"{Title}  ·  {number} / {Slides.Count}", typeface, 0.028 * h, Muted, w);
            g.DrawText(foot, new Point(w - margin - foot.Width, h - 0.06 * h - foot.Height / 2));
        }
        var bitmap = new RenderTargetBitmap(size.Width, size.Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bitmap));
        using var ms = new MemoryStream();
        png.Save(ms);
        return ms.ToArray();
    }

    /// <summary>WPF drawing needs an STA thread.</summary>
    private static T OnSta<T>(Func<T> work)
    {
        T result = default!;
        Exception? error = null;
        var t = new Thread(() =>
        {
            try { result = work(); }
            catch (Exception e) { error = e; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error is not null) throw new InvalidOperationException("drawing the help deck failed", error);
        return result;
    }
}
