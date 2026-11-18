using System.Drawing;
using System.Drawing.Text;
using Presenter.Core;
using Presenter.Windows;

namespace Presenter.Windows.Tests;

/// <summary>The parts of the Windows layer that need neither Paint nor the
/// screen: they run anywhere Windows does.</summary>
public class ScreenFreeTests
{
    private static bool HasComicSans() =>
        new InstalledFontCollection().Families.Any(f => f.Name == "Comic Sans MS");

    [Fact]
    public void GlyphOutlinesAreClosedContours()
    {
        if (!HasComicSans()) return;  // the painting needs it; doctor reports it missing
        var contours = GlyphOutlines.Of("Pa", px: 200);
        // P: outer + counter; a: outer + counter
        Assert.True(contours.Count >= 4, $"{contours.Count} contours");
        foreach (var c in contours)
        {
            Assert.True(c.Count > 3);
            Assert.Equal(c[0], c[^1]);  // closed
        }
        double width = contours.Max(c => c.Max(p => p.X)) - contours.Min(c => c.Min(p => p.X));
        Assert.InRange(width, 150, 400);  // two bold letters at 200 px
    }

    [Fact]
    public void AnEmptyTextHasNoOutlines() => Assert.Empty(GlyphOutlines.Of(""));

    [Fact]
    public void SolidPngHasTheSizeAndColour()
    {
        using var bmp = new Bitmap(new MemoryStream(ScreenCapture.SolidPng(new PixelSize(64, 40), Color.Black)));
        Assert.Equal(new Size(64, 40), bmp.Size);
        Assert.True(ScreenCapture.IsSolid(bmp, Color.Black));
        Assert.False(ScreenCapture.IsSolid(bmp, Color.White));
    }

    [Fact]
    public void TheBlankCanvasFileKeepsAPictureSavedOverIt()
    {
        var size = new PixelSize(123, 45);  // a size no show uses
        string path = PaintSession.BlankCanvasFile(size);
        using (var blank = new Bitmap(path)) Assert.True(ScreenCapture.IsSolid(blank, Color.White));

        // someone saves a picture over it in Paint (File > Save)
        File.WriteAllBytes(path, ScreenCapture.SolidPng(size, Color.Red));
        string again = PaintSession.BlankCanvasFile(size);
        Assert.Equal(path, again);
        using (var fresh = new Bitmap(again)) Assert.True(ScreenCapture.IsSolid(fresh, Color.White));
        var saved = Directory.GetFiles(Path.GetDirectoryName(path)!, "canvas-123x45-saved-*.png");
        Assert.NotEmpty(saved);  // the picture was kept, under another name
        foreach (var f in saved) File.Delete(f);
        File.Delete(path);
    }

    [Fact]
    public void TheActiveDisplaysHaveIdentities()
    {
        var monitors = Monitors.All();
        Assert.NotEmpty(monitors);
        Assert.Single(monitors, m => m.Primary);
        var ids = MonitorIdentity.Active();
        foreach (var m in monitors)
            if (ids.TryGetValue(m.Device, out var id))
                Assert.Matches("^[0-9A-F]{16}$", id.Identity);  // a hash, never the serial itself
    }
}
