using System.IO;
using System.Windows.Media.Imaging;
using Presenter.Core;
using Presenter.Ui;

namespace Presenter.Ui.Tests;

public class HelpDeckTests
{
    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(1910, 1433)]  // a 4:3 screen shape: the 16:9 deck is drawn into it
    public void EverySlideRendersAtTheExactSize(int w, int h)
    {
        string? keep = Environment.GetEnvironmentVariable("PRESENTER_HELP_DECK_OUT");
        for (int i = 1; i <= HelpDeck.Slides.Count; i++)
        {
            byte[] png = HelpDeck.Render(i, new PixelSize(w, h));
            var frame = BitmapDecoder.Create(new MemoryStream(png), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
            Assert.Equal(w, frame.PixelWidth);
            Assert.Equal(h, frame.PixelHeight);
            Assert.True(png.Length > 10_000, $"slide {i} looks empty ({png.Length} bytes)");
            if (keep is { Length: > 0 }) File.WriteAllBytes(Path.Combine(keep, $"help-{w}x{h}-{i}.png"), png);
        }
    }

    [Fact]
    public void TheDeckHasATitleAndSlides()
    {
        Assert.Equal("Paint Presentator", HelpDeck.Title);
        Assert.True(HelpDeck.Slides.Count >= 5);
        Assert.All(HelpDeck.Slides, s => Assert.NotEmpty(s.Lines));
    }

    [Fact]
    public void TheFirstSlideWelcomesAndSpaceLeadsToTheKeys()
    {
        var first = HelpDeck.Slides[0];
        Assert.Equal("Welcome to", first.Kicker);
        Assert.Contains(first.Lines, l => l.StartsWith("> ") && l.Contains("Space"));
        Assert.Equal("Keys during a show", HelpDeck.Slides[1].Title);  // where Space goes
    }
}
