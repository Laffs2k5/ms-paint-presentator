using Presenter.Core;

namespace Presenter.Core.Tests;

public class GeometryTests
{
    [Fact]
    public void PredictsTheLaptopViewport()
    {
        // E13: the laptop panel at 200%, taskbar on the left: work area 2784x1920
        Assert.Equal(new PixelSize(2784, 1499), SlideGeometry.PredictedViewport(new PixelSize(2784, 1920), 192));
    }

    [Fact]
    public void PredictsAt100Percent()
    {
        // 210.5 rounds half up to 211
        Assert.Equal(new PixelSize(1920, 1829), SlideGeometry.PredictedViewport(new PixelSize(1920, 2040), 96));
    }

    [Fact]
    public void ReferenceShapeMatchesThePoc()
    {
        // E13: viewport 2784x1499 gave a 2718x1283 slide for the 1854x875 reference shape
        Assert.Equal(new PixelSize(2718, 1283), SlideGeometry.SlideSize(new PixelSize(2784, 1499), 1854.0 / 875));
    }

    [Theory]
    [InlineData(2784, 1499, 16.0 / 9)]
    [InlineData(2784, 1499, 4.0 / 3)]
    [InlineData(1920, 869, 16.0 / 9)]
    [InlineData(1280, 589, 16.0 / 10)]
    public void SlideFitsWithEvenMargins(int vw, int vh, double aspect)
    {
        var vp = new PixelSize(vw, vh);
        var s = SlideGeometry.SlideSize(vp, aspect);
        Assert.True(s.Width <= vw - SlideGeometry.ViewportMargin);
        Assert.True(s.Height <= vh - SlideGeometry.ViewportMargin);
        Assert.Equal(0, (vw - s.Width) % 2);
        Assert.Equal(0, (vh - s.Height) % 2);
        Assert.InRange(s.Aspect, aspect * 0.995, aspect * 1.005);
        // as large as it can be: one of the sides is within 2 px of its limit
        Assert.True(vw - SlideGeometry.ViewportMargin - s.Width <= 2 || vh - SlideGeometry.ViewportMargin - s.Height <= 2);
    }

    [Fact]
    public void CentresTheCanvas()
    {
        var vp = new PixelRect(100, 200, 2884, 1699);
        Assert.Equal(PixelRect.FromSize(133, 308, new PixelSize(2718, 1283)),
                     SlideGeometry.CentredIn(vp, new PixelSize(2718, 1283)));
    }

    [Fact]
    public void ParsesSizes()
    {
        Assert.Equal(new PixelSize(1920, 1080), PixelSize.Parse("1920x1080"));
        Assert.Throws<FormatException>(() => PixelSize.Parse("1920"));
    }
}
