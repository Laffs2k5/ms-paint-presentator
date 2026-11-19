using System.IO.Compression;
using Presenter.Slides;

namespace Presenter.Slides.Tests;

public class DeckTitleTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("deck-titles-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Write(string name, string text)
    {
        string path = Path.Combine(_dir, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public void MarpFrontMatterTitleWins()
    {
        var deck = Decks.Inspect(Write("talk.md", "---\nmarp: true\ntitle: \"Paint, but faster\"\n---\n\n# Something else\n"));
        Assert.Equal("Paint, but faster", deck.Title);
    }

    [Fact]
    public void MarpFirstHeadingWithoutATitle()
    {
        var deck = Decks.Inspect(Write("talk.md", "---\nmarp: true\n---\n\n```md\n# not this\n```\n\n# The **real** one\n\n---\n\n# Second\n"));
        Assert.Equal("The real one", deck.Title);
    }

    [Fact]
    public void FileNameWhenNothingElse()
    {
        var deck = Decks.Inspect(Write("quarterly_review-2026.md", "---\nmarp: true\n---\n\nNo heading at all\n"));
        Assert.Equal("quarterly review 2026", deck.Title);
    }

    [Fact]
    public void ASlidevProjectWithoutTitleUsesItsFolder()
    {
        string entry = Write(Path.Combine("my-talk", "slides.md"), "---\ntheme: default\ncanvasWidth: 980\n---\n\nhello\n");
        Assert.Equal("my talk", DeckTitles.For(new DeckInfo(entry, DeckKind.Slidev, 16.0 / 9, 1)));
    }

    [Fact]
    public void PowerPointTitleFromItsDocumentProperties()
    {
        string path = Path.Combine(_dir, "deck.pptx");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        using (var w = new StreamWriter(zip.CreateEntry("docProps/core.xml").Open()))
            w.Write("<?xml version=\"1.0\"?><cp:coreProperties xmlns:cp=\"http://schemas.openxmlformats.org/package/2006/metadata/core-properties\" "
                    + "xmlns:dc=\"http://purl.org/dc/elements/1.1/\"><dc:title>Roadmap 2027</dc:title></cp:coreProperties>");
        Assert.Equal("Roadmap 2027", DeckTitles.FromOpenXml(path));
    }

    [Fact]
    public void APdfIsNamedAfterItsFile() =>
        Assert.Equal("all hands", DeckTitles.For(new DeckInfo(Path.Combine(_dir, "all-hands.pdf"), DeckKind.Pdf, 16.0 / 9, 3)));
}
