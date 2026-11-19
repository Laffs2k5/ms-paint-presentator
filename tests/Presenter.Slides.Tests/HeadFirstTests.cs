using Presenter.Slides;

namespace Presenter.Slides.Tests;

public class HeadFirstTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("head-first-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static string Deck(int slides, string extraFront = "") =>
        $"---\nmarp: true\n{extraFront}---\n\n" + string.Join("\n\n---\n\n",
            Enumerable.Range(1, slides).Select(i => $"# Slide {i}\n\n```md\n---\nnot a break\n```"));

    [Fact]
    public void TheHeadKeepsTheFrontMatterAndTheFirstSlides()
    {
        string path = Path.Combine(_dir, "deck.md");
        File.WriteAllText(path, Deck(12, "theme: gaia\n"));
        string head = MarpDeck.HeadText(path, 5)!;
        Assert.StartsWith("---\nmarp: true\ntheme: gaia\n---", head);
        var lines = FrontMatter.SplitLines(head);
        var fm = FrontMatter.Parse(lines);
        Assert.Equal(5, MarpDeck.CountSlides(lines[fm.BodyLine..], null));
        Assert.Contains("# Slide 5", head);
        Assert.DoesNotContain("# Slide 6", head);
    }

    [Fact]
    public void AShortDeckHasNoHead()
    {
        string path = Path.Combine(_dir, "deck.md");
        File.WriteAllText(path, Deck(5));
        Assert.Null(MarpDeck.HeadText(path, 5));
    }

    [Fact]
    public void HeadingDividerDecksAreCutAtHeadings()
    {
        string path = Path.Combine(_dir, "deck.md");
        File.WriteAllText(path, "---\nmarp: true\nheadingDivider: 1\n---\n\n" + string.Join("\n\n", Enumerable.Range(1, 8).Select(i => $"# Part {i}\n\ntext")));
        string head = MarpDeck.HeadText(path, 3)!;
        Assert.Contains("# Part 3", head);
        Assert.DoesNotContain("# Part 4", head);
    }
}
