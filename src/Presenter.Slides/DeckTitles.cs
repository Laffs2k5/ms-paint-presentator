using System.IO.Compression;
using System.Xml;

namespace Presenter.Slides;

/// <summary>
/// A deck's title, for the loading painting: what the deck's own tool calls
/// it. Marp and Slidev: the front matter's <c>title</c>, else the first
/// <c># heading</c>. PowerPoint: the document's Title property. Anything
/// without one (a PDF, a .ppt, a deck that sets none): the file name, or the
/// project folder's name for a Slidev <c>slides.md</c>.
/// </summary>
internal static class DeckTitles
{
    public static string For(DeckInfo deck)
    {
        string? title = null;
        try
        {
            title = deck.Kind switch
            {
                DeckKind.Marp or DeckKind.Slidev => FromMarkdown(deck.Path),
                DeckKind.PowerPoint when PowerPointDeck.IsOpenXml(deck.Path) => FromOpenXml(deck.Path),
                _ => null,
            };
        }
        catch (Exception e) when (e is IOException or InvalidDataException or XmlException or UnauthorizedAccessException) { }
        return Clean(title) ?? FromFileName(deck.Path);
    }

    internal static string? FromMarkdown(string path)
    {
        var lines = FrontMatter.SplitLines(File.ReadAllText(path));
        var fm = FrontMatter.Parse(lines);
        if (Clean(fm["title"]) is { } t) return t;
        bool fenced = false;
        for (int i = fm.BodyLine; i < lines.Length; i++)
        {
            string line = lines[i].TrimEnd();
            if (line.StartsWith("```") || line.StartsWith("~~~")) fenced = !fenced;
            if (!fenced && line.StartsWith("# ")) return line[2..].Trim();
        }
        return null;
    }

    internal static string? FromOpenXml(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var entry = zip.GetEntry("docProps/core.xml");
        if (entry is null) return null;
        var doc = new XmlDocument();
        using (var s = entry.Open()) doc.Load(s);
        var ns = new XmlNamespaceManager(doc.NameTable);
        ns.AddNamespace("dc", "http://purl.org/dc/elements/1.1/");
        return doc.SelectSingleNode("//dc:title", ns)?.InnerText;
    }

    internal static string FromFileName(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        if (name.Equals("slides", StringComparison.OrdinalIgnoreCase))  // a Slidev project: its folder
            name = Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(path))) ?? name;
        return name.Replace('_', ' ').Replace('-', ' ').Trim();
    }

    /// <summary>Quotes and Markdown emphasis off; null when nothing is left.</summary>
    internal static string? Clean(string? title)
    {
        if (title is null) return null;
        string t = title.Trim().Trim('"', '\'').Replace("**", "").Replace("__", "").Trim();
        return t.Length == 0 ? null : t;
    }
}
