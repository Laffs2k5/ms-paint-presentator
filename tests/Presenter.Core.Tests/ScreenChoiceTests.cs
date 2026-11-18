using Presenter.Core;

namespace Presenter.Core.Tests;

public class ScreenChoiceTests
{
    private static ScreenOption Screen(string device, bool builtIn, bool primary, string id, int w = 1920, int h = 1080, int scale = 100)
    {
        var res = new PixelSize(w, h);
        var vp = SlideGeometry.PredictedViewport(new PixelSize(w, h - 48), scale * 96 / 100);
        return new ScreenOption(device, device, builtIn ? "built-in" : "HDMI", builtIn, primary, res, scale, vp, id);
    }

    private static readonly ScreenOption Laptop = Screen(@"\\.\DISPLAY1", builtIn: true, primary: true, "laptop", 2880, 1920, 200);
    private static readonly ScreenOption Projector = Screen(@"\\.\DISPLAY2", builtIn: false, primary: false, "projector");
    private static readonly ScreenOption Monitor = Screen(@"\\.\DISPLAY3", builtIn: false, primary: false, "monitor", 2560, 1440);

    [Fact]
    public void TheProjectorBeforeTheLaptop() =>
        Assert.Equal(1, ScreenChoice.DefaultIndex([Laptop, Projector], null));

    [Fact]
    public void TheRememberedScreenFirst() =>
        Assert.Equal(2, ScreenChoice.DefaultIndex([Laptop, Projector, Monitor], "monitor"));

    [Fact]
    public void ARememberedScreenThatIsGoneIsIgnored() =>
        Assert.Equal(1, ScreenChoice.DefaultIndex([Laptop, Projector], "monitor"));

    [Fact]
    public void TheLaptopAloneIsTheDefault() =>
        Assert.Equal(0, ScreenChoice.DefaultIndex([Laptop], null));

    [Fact]
    public void NoScreens() => Assert.Equal(-1, ScreenChoice.DefaultIndex([], null));

    [Fact]
    public void TheSameScreenBackIsARepaste()
    {
        var slide = Projector.SlideFor(16.0 / 9);
        Assert.Equal(ResumeWork.Repaste, ScreenChoice.WorkAfterChange(Projector, Projector with { }, slide, 16.0 / 9));
    }

    [Fact]
    public void AnotherScreenOfTheSameSizeMovesPaint()
    {
        var other = Projector with { Device = @"\\.\DISPLAY4", Identity = "projector 2" };
        var slide = Projector.SlideFor(16.0 / 9);
        Assert.Equal(ResumeWork.MovePaint, ScreenChoice.WorkAfterChange(Projector, other, slide, 16.0 / 9));
    }

    [Fact]
    public void ANewSizeNeedsNewSlides()
    {
        var slide = Projector.SlideFor(16.0 / 9);
        Assert.Equal(ResumeWork.NewSlideSize, ScreenChoice.WorkAfterChange(Projector, Monitor, slide, 16.0 / 9));
    }
}
