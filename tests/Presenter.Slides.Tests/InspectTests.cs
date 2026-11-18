using System.IO.Compression;
using System.Text;

namespace Presenter.Slides.Tests;

public class InspectTests
{
    [Fact]
    public void Marp_deck_is_recognised_by_its_front_matter()
    {
        using var dir = new TempDir();
        var path = dir.File("talk.md", SampleDecks.Marp(5));
        var deck = Decks.Inspect(path);
        Assert.Equal(DeckKind.Marp, deck.Kind);
        Assert.Equal(16.0 / 9, deck.Aspect, 3);
        Assert.Equal(5, deck.SlideCount);
        Assert.Equal(Path.GetFullPath(path), deck.Path);
    }

    [Fact]
    public void Marp_size_directive_gives_4_3()
    {
        using var dir = new TempDir();
        var deck = Decks.Inspect(dir.File("old.md", SampleDecks.Marp(3, "4:3")));
        Assert.Equal(4.0 / 3, deck.Aspect, 3);
        Assert.Equal(3, deck.SlideCount);
    }

    [Fact]
    public void Marp_custom_theme_size_comes_from_its_css()
    {
        using var dir = new TempDir();
        dir.File("themes/wide.css", "/* @theme wide */\n@import 'default';\n\nsection {\n  width: 1600px;\n  height: 900px;\n}\n" +
                                    "section.lead { width: 10px; }\n");
        var deck = Decks.Inspect(dir.File("deck.md", "---\nmarp: true\ntheme: wide\n---\n\n# One\n"));
        Assert.Equal(16.0 / 9, deck.Aspect, 3);
        var marp = MarpDeck.Read(deck.Path);
        Assert.Equal(new Core.PixelSize(1600, 900), marp.Design);
        Assert.EndsWith("wide.css", marp.ThemeFile);
    }

    [Fact]
    public void Marp_theme_size_preset_wins_over_section_size()
    {
        using var dir = new TempDir();
        dir.File("t.css", "/* @theme t\n * @size 4:3 1024px 768px\n */\nsection { width: 1280px; height: 720px; }\n");
        var marp = MarpDeck.Read(dir.File("deck.md", "---\nmarp: true\ntheme: t\nsize: 4:3\n---\n# x\n"));
        Assert.Equal(new Core.PixelSize(1024, 768), marp.Design);
    }

    [Fact]
    public void Marp_directives_in_comments_count()
    {
        using var dir = new TempDir();
        var marp = MarpDeck.Read(dir.File("deck.md", "---\nmarp: true\n---\n<!-- size: 4:3 -->\n# x\n"));
        Assert.Equal(new Core.PixelSize(960, 720), marp.Design);
    }

    [Theory]
    [InlineData("# a\n\n---\n\n# b\n\n---\n\n# c\n", null, 3)]
    [InlineData("# a\n\n***\n\n# b\n", null, 2)]
    [InlineData("# a\n\n```md\n---\n```\n\n---\n\n# b\n", null, 2)] // a break in code is not a slide
    [InlineData("Heading text\n---\n\nmore\n", null, 1)] // setext heading, not a break
    [InlineData("# a\n\n<!--\n---\n-->\n\n# still a\n", null, 1)] // a break in a comment
    [InlineData("# a\n\nx\n\n## b\n\ny\n\n## c\n", "2", 3)]
    [InlineData("# a\n\n## b\n", "[1]", 1)]
    [InlineData("", null, 1)]
    public void Marp_slides_are_counted_as_marp_splits_them(string body, string? headingDivider, int expected) =>
        Assert.Equal(expected, MarpDeck.CountSlides(FrontMatter.SplitLines(body), headingDivider));

    [Fact]
    public void Markdown_without_a_known_tool_is_not_a_deck()
    {
        using var dir = new TempDir();
        var path = dir.File("notes.md", "---\ntitle: Notes\nlayout: post\n---\n\n# Just notes\n");
        Assert.Throws<NotSupportedException>(() => Decks.Inspect(path));
    }

    [Fact]
    public void Missing_path_throws_file_not_found()
    {
        using var dir = new TempDir();
        Assert.Throws<FileNotFoundException>(() => Decks.Inspect(Path.Combine(dir.Path, "nope.pptx")));
    }

    [Fact]
    public void Slidev_folder_with_slides_md()
    {
        using var dir = new TempDir();
        dir.File("slides.md", SampleDecks.Slidev(4));
        var deck = Decks.Inspect(dir.Path);
        Assert.Equal(DeckKind.Slidev, deck.Kind);
        Assert.Equal(Path.Combine(dir.Path, "slides.md"), deck.Path);
        Assert.Equal(4, deck.SlideCount); // the hidden slide is not exported
        Assert.Equal(980.0 / 551, deck.Aspect, 6); // canvas 980 x round(980 / (16/9))
    }

    [Fact]
    public void Slidev_package_json_names_the_entry()
    {
        using var dir = new TempDir();
        dir.File("talk.md", "---\ntheme: default\naspectRatio: 4/3\ncanvasWidth: 1024\n---\n\n# One\n\n---\n\n# Two\n");
        var pkg = dir.File("package.json",
            "{ \"scripts\": { \"dev\": \"slidev talk.md --open\" }, \"dependencies\": { \"@slidev/cli\": \"^53.0.0\" } }");
        var deck = Decks.Inspect(pkg);
        Assert.Equal(DeckKind.Slidev, deck.Kind);
        Assert.EndsWith("talk.md", deck.Path);
        Assert.Equal(1024.0 / 768, deck.Aspect, 6);
        Assert.Equal(2, deck.SlideCount);
    }

    [Fact]
    public void Slidev_headmatter_alone_identifies_a_deck()
    {
        using var dir = new TempDir();
        var deck = Decks.Inspect(dir.File("deck.md", "---\ncanvasWidth: 800\n---\n\n# One\n"));
        Assert.Equal(DeckKind.Slidev, deck.Kind);
    }

    [Fact]
    public void Marp_wins_over_the_slides_md_name()
    {
        using var dir = new TempDir();
        Assert.Equal(DeckKind.Marp, Decks.Inspect(dir.File("slides.md", SampleDecks.Marp(2))).Kind);
    }

    [Theory]
    [InlineData("---\ntheme: default\n---\n\n# 1\n\n---\n\n# 2\n", 2)]
    [InlineData("# 1\n\n---\nlayout: center\nclass: x\n---\n\n# 2\n\n---\n\n# 3\n", 3)] // per-slide frontmatter
    [InlineData("# 1\n\n```yaml\n---\n```\n\n---\n\n# 2\n", 2)] // a separator in code
    [InlineData("# 1\n\n<!--\n---\n-->\n", 1)] // a separator in a comment
    [InlineData("# 1\n\n---\ndisabled: true\n---\n\n# off\n\n---\n\n# 2\n", 2)]
    public void Slidev_slides_are_counted_as_slidev_splits_them(string text, int expected) =>
        Assert.Equal(expected, SlidevDeck.CountSlides(FrontMatter.SplitLines(text)).Count);

    [Fact]
    public void Slidev_imports_make_the_count_unknown()
    {
        var (count, estimate) = SlidevDeck.CountSlides(FrontMatter.SplitLines("# 1\n\n---\nsrc: ./more.md\n---\n"));
        Assert.Null(count);
        Assert.Equal(2, estimate);
    }

    [Theory]
    [InlineData("16/9", 16.0 / 9)]
    [InlineData("4:3", 4.0 / 3)]
    [InlineData("1.6", 1.6)]
    [InlineData("'16/10'", 1.6)]
    public void Slidev_aspect_ratio_forms(string value, double expected) =>
        Assert.Equal(expected, SlidevDeck.ParseAspect(value.Trim('\''))!.Value, 6);

    [Fact]
    public void Pdf_shape_and_page_count_come_from_windows()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "deck.pdf");
        File.WriteAllBytes(path, SampleDecks.Pdf(720, 540, (1, 0, 0), (0, 0.5, 0), (0, 0, 1)));
        var deck = Decks.Inspect(path);
        Assert.Equal(DeckKind.Pdf, deck.Kind);
        Assert.Equal(4.0 / 3, deck.Aspect, 3);
        Assert.Equal(3, deck.SlideCount);
    }

    [Fact]
    public void Pptx_shape_and_visible_slides_come_from_its_xml()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "deck.pptx");
        WriteMinimalPptx(path, cx: 9144000, cy: 6858000, hidden: [false, true, false]);
        var deck = Decks.Inspect(path);
        Assert.Equal(DeckKind.PowerPoint, deck.Kind);
        Assert.Equal(4.0 / 3, deck.Aspect, 6);
        Assert.Equal(2, deck.SlideCount);
    }

    /// <summary>Just the parts the inspector reads: not a file PowerPoint would open.</summary>
    internal static void WriteMinimalPptx(string path, long cx, long cy, bool[] hidden)
    {
        const string p = "http://schemas.openxmlformats.org/presentationml/2006/main";
        const string r = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        void Put(string name, string xml)
        {
            using var w = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
            w.Write(xml);
        }
        var ids = string.Concat(hidden.Select((_, i) => $"<p:sldId id=\"{256 + i}\" r:id=\"rId{i + 1}\"/>"));
        Put("ppt/presentation.xml",
            $"<?xml version=\"1.0\"?><p:presentation xmlns:p=\"{p}\" xmlns:r=\"{r}\"><p:sldIdLst>{ids}</p:sldIdLst>" +
            $"<p:sldSz cx=\"{cx}\" cy=\"{cy}\"/></p:presentation>");
        var rels = string.Concat(hidden.Select((_, i) =>
            $"<Relationship Id=\"rId{i + 1}\" Type=\"{r}/slide\" Target=\"slides/slide{i + 1}.xml\"/>"));
        Put("ppt/_rels/presentation.xml.rels",
            $"<?xml version=\"1.0\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">{rels}</Relationships>");
        for (int i = 0; i < hidden.Length; i++)
            Put($"ppt/slides/slide{i + 1}.xml", $"<?xml version=\"1.0\"?><p:sld xmlns:p=\"{p}\"{(hidden[i] ? " show=\"0\"" : "")}/>");
    }
}
