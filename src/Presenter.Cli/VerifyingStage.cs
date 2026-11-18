using System.Drawing;
using System.Drawing.Imaging;
using Presenter.Core;
using Presenter.Windows;

namespace Presenter.Cli;

/// <summary>
/// For testing (`present --verify`): after each change, read the canvas back
/// from the screen and compare it with the frame that should be there. Screen
/// reads wait for a display refresh (E6), so this is never on in a real show.
/// </summary>
internal sealed class VerifyingStage(PaintStage inner, Func<int, byte[]> slidePng, Func<PaintSession, PixelRect> canvas,
                                     PixelSize size) : IStage
{
    public List<(string Frame, double Match)> Results { get; } = [];

    public async Task<long?> ShowAsync(Frame frame, CancellationToken ct)
    {
        long? seen = await inner.ShowAsync(frame, ct);
        double match = await inner.Run(paint =>
        {
            Thread.Sleep(150);  // the commit's redraw
            using var shot = ScreenCapture.Grab(canvas(paint!));
            using var want = frame switch
            {
                Frame.Slide s => new Bitmap(new MemoryStream(slidePng(s.Index))),
                Frame.Blank b => Solid(b.Colour == BlankColour.Black ? Color.Black : Color.White),
                _ => throw new ArgumentException(frame.ToString()),
            };
            double m = Match(shot, want);
            if (m < 0.999 && Environment.GetEnvironmentVariable("PRESENTER_VERIFY_DIR") is { Length: > 0 } dir)
            {
                Directory.CreateDirectory(dir);
                shot.Save(Path.Combine(dir, $"{frame.ToString()!.Replace(' ', '-')}-seen.png"), ImageFormat.Png);
                want.Save(Path.Combine(dir, $"{frame.ToString()!.Replace(' ', '-')}-want.png"), ImageFormat.Png);
            }
            return m;
        });
        Results.Add((frame.ToString()!, Math.Round(match, 4)));
        PresentCommand.Log($"  verify {frame}: {match:P2} of sampled pixels match");
        return seen;
    }

    private Bitmap Solid(Color c)
    {
        var b = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(b);
        g.Clear(c);
        return b;
    }

    /// <summary>The share of a grid of pixels (every 7th) within 8 of each
    /// other per channel.</summary>
    private static double Match(Bitmap a, Bitmap b)
    {
        if (a.Width != b.Width || a.Height != b.Height) return 0;
        int same = 0, n = 0;
        for (int y = 3; y < a.Height; y += 7)
            for (int x = 3; x < a.Width; x += 7)
            {
                Color p = a.GetPixel(x, y), q = b.GetPixel(x, y);
                n++;
                if (Math.Abs(p.R - q.R) <= 8 && Math.Abs(p.G - q.G) <= 8 && Math.Abs(p.B - q.B) <= 8) same++;
            }
        return (double)same / n;
    }
}
