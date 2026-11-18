using System.Globalization;
using System.Text.RegularExpressions;
using Presenter.Core;

namespace Presenter.Slides;

/// <summary>
/// What the presenter reads from a Marp deck without running Marp: its design
/// canvas (the theme's slide size in CSS pixels) and its slide count.
/// </summary>
internal sealed partial class MarpDeck
{
    public required string Path { get; init; }
    public required PixelSize Design { get; init; }
    /// <summary>The theme CSS the design size came from, if not a built-in theme.</summary>
    public string? ThemeFile { get; init; }
    public required int SlideCount { get; init; }

    private static readonly string[] BuiltInThemes = ["default", "gaia", "uncover"];

    /// <summary>Marp's built-in themes: 16:9 is 1280x720, 4:3 is 960x720.</summary>
    private static PixelSize BuiltInSize(string? preset) =>
        preset?.Trim() == "4:3" ? new PixelSize(960, 720) : new PixelSize(1280, 720);

    public static bool IsMarp(string[] lines) => FrontMatter.Parse(lines).IsTrue("marp");

    public static MarpDeck Read(string path)
    {
        var lines = FrontMatter.SplitLines(File.ReadAllText(path));
        var fm = FrontMatter.Parse(lines);
        var body = lines.AsSpan(fm.BodyLine).ToArray();
        var comments = FrontMatter.CommentDirectives(HtmlComments(body));
        string? Directive(string key) => comments.TryGetValue(key, out var v) ? v : fm[key];

        var theme = Directive("theme")?.Trim();
        var size = Directive("size");
        var (design, themeFile) = DesignSize(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!, theme, size);
        return new MarpDeck
        {
            Path = System.IO.Path.GetFullPath(path),
            Design = design,
            ThemeFile = themeFile,
            SlideCount = CountSlides(body, Directive("headingDivider")),
        };
    }

    private static (PixelSize, string?) DesignSize(string deckDir, string? theme, string? sizePreset)
    {
        if (string.IsNullOrEmpty(theme) || BuiltInThemes.Contains(theme, StringComparer.OrdinalIgnoreCase))
            return (BuiltInSize(sizePreset), null);
        var css = FindThemeCss(deckDir, theme);
        if (css is null) return (BuiltInSize(sizePreset), null); // unknown theme: checked on the first image
        var text = File.ReadAllText(css);
        // @size presets declared by the theme: /* @size 4:3 960px 720px */
        foreach (Match m in SizeMetaRegex().Matches(text))
        {
            if (sizePreset is not null && m.Groups[1].Value == sizePreset.Trim())
                return (new PixelSize(Px(m.Groups[2].Value), Px(m.Groups[3].Value)), css);
        }
        // the theme's own section size
        int? w = null, h = null;
        foreach (Match block in SectionBlockRegex().Matches(text))
        {
            var wm = WidthRegex().Match(block.Groups[1].Value);
            var hm = HeightRegex().Match(block.Groups[1].Value);
            if (wm.Success) w = Px(wm.Groups[1].Value);
            if (hm.Success) h = Px(hm.Groups[1].Value);
        }
        var inherited = BuiltInSize(sizePreset);
        return (new PixelSize(w ?? inherited.Width, h ?? inherited.Height), css);
    }

    private static int Px(string v) =>
        (int)Math.Round(double.Parse(v, CultureInfo.InvariantCulture), MidpointRounding.AwayFromZero);

    /// <summary>A CSS file next to the deck (or a folder or two below it)
    /// that declares <c>@theme name</c>.</summary>
    private static string? FindThemeCss(string deckDir, string theme)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 2, IgnoreInaccessible = true };
        int seen = 0;
        foreach (var css in Directory.EnumerateFiles(deckDir, "*.css", options))
        {
            if (css.Contains($"{System.IO.Path.DirectorySeparatorChar}node_modules{System.IO.Path.DirectorySeparatorChar}",
                    StringComparison.OrdinalIgnoreCase)) continue;
            if (++seen > 200) break;
            string head;
            using (var reader = new StreamReader(css))
            {
                var buf = new char[4096];
                head = new string(buf, 0, reader.Read(buf, 0, buf.Length));
            }
            var m = ThemeMetaRegex().Match(head);
            if (m.Success && m.Groups[1].Value == theme) return css;
        }
        return null;
    }

    /// <summary>The contents of the HTML comments in the body (where Marp's directives may be).</summary>
    private static IEnumerable<string> HtmlComments(string[] body)
    {
        var text = string.Join('\n', body);
        foreach (Match m in CommentRegex().Matches(text)) yield return m.Groups[1].Value;
    }

    /// <summary>
    /// Counts slides as Marp splits them: at thematic breaks (<c>---</c>,
    /// <c>***</c>, <c>___</c>) outside code and HTML comments, but not a
    /// <c>---</c> that underlines a paragraph line (a setext heading), and
    /// before headings when <c>headingDivider</c> is set. An estimate: the
    /// render corrects it to the number of images.
    /// </summary>
    internal static int CountSlides(string[] body, string? headingDivider)
    {
        var dividerLevels = ParseHeadingDivider(headingDivider);
        int slides = 1;
        bool hasContent = false, inComment = false;
        string? fence = null;
        string prev = "";
        foreach (var raw in body)
        {
            var line = raw.TrimEnd();
            if (fence is not null)
            {
                if (line.TrimStart().StartsWith(fence, StringComparison.Ordinal) && line.Trim().Trim(fence[0]).Length == 0)
                    fence = null;
                prev = line;
                hasContent = true;
                continue;
            }
            if (inComment)
            {
                if (line.Contains("-->", StringComparison.Ordinal)) inComment = false;
                prev = "";
                continue;
            }
            var fm = FenceRegex().Match(line);
            if (fm.Success)
            {
                fence = fm.Groups[1].Value;
                hasContent = true;
                prev = line;
                continue;
            }
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("<!--", StringComparison.Ordinal))
            {
                if (!trimmed.Contains("-->", StringComparison.Ordinal)) inComment = true;
                prev = "";
                continue;
            }
            if (BreakRegex().IsMatch(line))
            {
                bool setext = line.Trim() == new string('-', line.Trim().Length) && IsParagraphLine(prev);
                if (!setext)
                {
                    slides++;
                    hasContent = false;
                    prev = "";
                    continue;
                }
            }
            var hm = AtxHeadingRegex().Match(line);
            if (hm.Success && dividerLevels.Contains(hm.Groups[1].Value.Length) && hasContent)
                slides++;
            if (line.Length > 0) hasContent = true;
            prev = line;
        }
        return slides;
    }

    private static bool IsParagraphLine(string line)
    {
        var t = line.TrimStart();
        return t.Length > 0 && !t.StartsWith('#') && !t.StartsWith('>') && !t.StartsWith("```", StringComparison.Ordinal)
               && !t.StartsWith("~~~", StringComparison.Ordinal) && !t.StartsWith('<') && line.Length - t.Length < 4;
    }

    private static HashSet<int> ParseHeadingDivider(string? value)
    {
        var levels = new HashSet<int>();
        if (string.IsNullOrWhiteSpace(value)) return levels;
        var v = value.Trim();
        if (v.StartsWith('['))
        {
            foreach (var part in v.Trim('[', ']').Split(','))
                if (int.TryParse(part.Trim(), out var n)) levels.Add(n);
        }
        else if (int.TryParse(v, out var max))
        {
            for (int i = 1; i <= Math.Min(max, 6); i++) levels.Add(i);
        }
        return levels;
    }

    [GeneratedRegex(@"^ {0,3}(?:(?:-[ \t]*){3,}|(?:\*[ \t]*){3,}|(?:_[ \t]*){3,})$")]
    private static partial Regex BreakRegex();

    [GeneratedRegex(@"^ {0,3}(`{3,}|~{3,})")]
    private static partial Regex FenceRegex();

    [GeneratedRegex(@"^ {0,3}(#{1,6})(?:[ \t]|$)")]
    private static partial Regex AtxHeadingRegex();

    [GeneratedRegex(@"<!--(.*?)-->", RegexOptions.Singleline)]
    private static partial Regex CommentRegex();

    [GeneratedRegex(@"@theme\s+([\w-]+)")]
    private static partial Regex ThemeMetaRegex();

    [GeneratedRegex(@"@size\s+(\S+)\s+([\d.]+)px\s+([\d.]+)px")]
    private static partial Regex SizeMetaRegex();

    [GeneratedRegex(@"(?:^|[}\s;,])section\s*\{([^}]*)\}", RegexOptions.Multiline)]
    private static partial Regex SectionBlockRegex();

    [GeneratedRegex(@"(?:^|[;\s{])width\s*:\s*([\d.]+)px")]
    private static partial Regex WidthRegex();

    [GeneratedRegex(@"(?:^|[;\s{])height\s*:\s*([\d.]+)px")]
    private static partial Regex HeightRegex();
}
