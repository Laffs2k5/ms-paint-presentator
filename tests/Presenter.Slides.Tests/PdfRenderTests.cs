using Presenter.Core;
using Xunit.Abstractions;

namespace Presenter.Slides.Tests;

/// <summary>PDF through Windows.Data.Pdf: needs nothing installed, so it always runs.</summary>
[Collection("renders")]
public class PdfRenderTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Handmade_pdf_renders_at_exact_sizes_and_hits_the_cache()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "deck.pdf");
        File.WriteAllBytes(path, SampleDecks.Pdf(960, 540, (1, 0, 0), (0, 0.6, 0), (0, 0, 1), (0.5, 0.5, 0.5)));
        var deck = Decks.Inspect(path);
        var options = new RenderOptions(Path.Combine(dir.Path, "cache"));
        foreach (var size in new[] { new PixelSize(1920, 1080), new PixelSize(2546, 1433), new PixelSize(1910, 1433) })
        {
            var run = await RenderChecks.RunAsync(deck, size, options);
            output.WriteLine($"pdf {size}: {run}");
            Assert.False(run.FromCache);
            Assert.Equal(4, run.TotalAtStart);
            Assert.Equal(4, run.Done);
            Assert.Equal(4, run.Total);
            var again = await RenderChecks.RunAsync(deck, size, options);
            output.WriteLine($"pdf {size} again: {again}");
            Assert.True(again.FromCache);
            Assert.Equal(4, again.Done);
        }
    }
}
