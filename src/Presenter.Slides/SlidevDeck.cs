using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Presenter.Slides;

/// <summary>
/// What the presenter reads from a Slidev project without running Slidev:
/// the entry (<c>slides.md</c>), the canvas and shape from its headmatter,
/// and the slide count.
/// </summary>
internal sealed partial class SlidevDeck
{
    /// <summary>The entry Markdown file.</summary>
    public required string Entry { get; init; }
    /// <summary>The project folder (the entry's folder: Slidev's user root).</summary>
    public string Root => System.IO.Path.GetDirectoryName(Entry)!;
    /// <summary>Slidev's <c>canvasWidth</c> (default 980).</summary>
    public required int CanvasWidth { get; init; }
    /// <summary>Slidev's <c>aspectRatio</c> (default 16/9).</summary>
    public required double Aspect { get; init; }
    /// <summary>Slides that will be exported, or null when the deck imports
    /// other files (<c>src:</c>) and counting them is not worth it.</summary>
    public required int? SlideCount { get; init; }
    /// <summary>An estimate when <see cref="SlideCount"/> is null.</summary>
    public required int SlideEstimate { get; init; }

    /// <summary>The canvas height Slidev uses: round(canvasWidth / aspect).</summary>
    public int CanvasHeight => (int)Math.Round(CanvasWidth / Aspect, MidpointRounding.AwayFromZero);

    /// <summary>Headmatter keys only Slidev uses: a deck with any of them is
    /// Slidev's. Generic keys (title, layout, theme) are left out: other
    /// Markdown has them too.</summary>
    private static readonly HashSet<string> SlidevKeys =
    [
        "canvasWidth", "aspectRatio", "highlighter", "drawings", "mdc", "addons", "themeConfig", "monaco",
        "exportFilename", "colorSchema", "routerMode", "selectable", "lineNumbers", "seoMeta", "wakeLock",
        "contextMenu", "htmlAttrs", "remoteAssets", "plantUmlServer", "codeCopy",
    ];

    /// <summary>
    /// The Slidev entry for a path: a folder holding <c>slides.md</c>, a
    /// <c>package.json</c> that depends on <c>@slidev/cli</c>, or a Markdown
    /// file that is Slidev's (named <c>slides.md</c>, Slidev-only headmatter,
    /// or next to a package.json that depends on Slidev). Null if not Slidev.
    /// </summary>
    public static string? FindEntry(string path)
    {
        if (Directory.Exists(path))
        {
            var slides = System.IO.Path.Combine(path, "slides.md");
            if (File.Exists(slides)) return slides;
            var pkg = System.IO.Path.Combine(path, "package.json");
            return File.Exists(pkg) ? FindEntry(pkg) : null;
        }
        if (!File.Exists(path)) return null;
        var name = System.IO.Path.GetFileName(path);
        if (name.Equals("package.json", StringComparison.OrdinalIgnoreCase))
        {
            if (!DependsOnSlidev(path)) return null;
            var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!;
            var entry = EntryFromScripts(path) ?? "slides.md";
            var full = System.IO.Path.Combine(dir, entry);
            return File.Exists(full) ? full : null;
        }
        if (!System.IO.Path.GetExtension(path).Equals(".md", StringComparison.OrdinalIgnoreCase)) return null;
        var fm = FrontMatter.Parse(FrontMatter.SplitLines(ReadHead(path)));
        if (fm.IsTrue("marp")) return null;
        if (name.Equals("slides.md", StringComparison.OrdinalIgnoreCase)) return path;
        if (fm.Keys.Any(SlidevKeys.Contains)) return path;
        var pkgNear = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!, "package.json");
        return File.Exists(pkgNear) && DependsOnSlidev(pkgNear) ? path : null;
    }

    private static string ReadHead(string path)
    {
        using var reader = new StreamReader(path);
        var buf = new char[16384];
        return new string(buf, 0, reader.Read(buf, 0, buf.Length));
    }

    internal static bool DependsOnSlidev(string packageJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(packageJson));
            foreach (var section in new[] { "dependencies", "devDependencies" })
                if (doc.RootElement.TryGetProperty(section, out var deps) && deps.ValueKind == JsonValueKind.Object
                    && deps.TryGetProperty("@slidev/cli", out _))
                    return true;
        }
        catch (JsonException)
        {
        }
        return false;
    }

    /// <summary>The entry named in a script such as <c>"dev": "slidev talk.md"</c>.</summary>
    private static string? EntryFromScripts(string packageJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(packageJson));
            if (!doc.RootElement.TryGetProperty("scripts", out var scripts) || scripts.ValueKind != JsonValueKind.Object)
                return null;
            foreach (var s in scripts.EnumerateObject())
            {
                if (s.Value.ValueKind != JsonValueKind.String) continue;
                var m = ScriptEntryRegex().Match(s.Value.GetString()!);
                if (m.Success) return m.Groups[1].Value;
            }
        }
        catch (JsonException)
        {
        }
        return null;
    }

    public static SlidevDeck Read(string entry)
    {
        var lines = FrontMatter.SplitLines(File.ReadAllText(entry));
        var head = FrontMatter.Parse(lines);
        int canvas = int.TryParse(head["canvasWidth"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var cw) && cw > 0
            ? cw : 980;
        var (count, estimate) = CountSlides(lines);
        return new SlidevDeck
        {
            Entry = System.IO.Path.GetFullPath(entry),
            CanvasWidth = canvas,
            Aspect = ParseAspect(head["aspectRatio"]) ?? 16.0 / 9,
            SlideCount = count,
            SlideEstimate = estimate,
        };
    }

    /// <summary>Slidev's <c>aspectRatio</c>: <c>16/9</c>, <c>4:3</c> or a number.</summary>
    internal static double? ParseAspect(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var parts = value.Split('/', ':', 'x');
        if (parts.Length == 2
            && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var a)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var b) && a > 0 && b > 0)
            return a / b;
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var r) && r > 0 ? r : null;
    }

    /// <summary>
    /// Counts slides as Slidev's parser splits them: a <c>---</c> line outside
    /// code fences starts a slide; when the next line is not blank, the lines
    /// up to the following <c>---</c> are that slide's frontmatter. Slides
    /// with <c>hide: true</c> or <c>disabled: true</c> are not exported. With
    /// <c>src:</c> imports the count is unknown (null) and the estimate counts
    /// each import as one slide.
    /// </summary>
    internal static (int? Count, int Estimate) CountSlides(string[] lines)
    {
        // A port of the splitting loop in Slidev's parser (@slidev/parser, parse()).
        int start = 0, slides = 0;
        bool imports = false, inComment = false;
        FrontMatter? front = null; // the current segment's frontmatter

        void Slice(int end)
        {
            if (start == end) return;
            if (front is not null && (front.IsTrue("hide") || front.IsTrue("disabled"))) return;
            if (front?["src"] is not null) imports = true;
            slides++;
        }

        for (int i = 0; i < lines.Length; i++)
        {
            var raw = lines[i];
            var line = raw.TrimEnd();
            if (inComment)
            {
                inComment = AdvanceComment(raw, true);
                continue;
            }
            if (line.StartsWith("---", StringComparison.Ordinal))
            {
                Slice(i);
                start = i + 1;
                front = null;
                var next = i + 1 < lines.Length ? lines[i + 1] : null;
                if ((line.Length <= 3 || line[3] != '-') && !string.IsNullOrWhiteSpace(next))
                {
                    start = i;
                    front = FrontMatter.Parse(lines, i);
                    for (i += 1; i < lines.Length; i++)
                        if (lines[i].TrimEnd() == "---") break;
                }
            }
            else if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                var level = LeadingBackticks().Match(line).Value;
                int j = i + 1;
                for (; j < lines.Length; j++)
                    if (lines[j].StartsWith(level, StringComparison.Ordinal)) break;
                if (j != lines.Length) i = j;
            }
            else inComment = AdvanceComment(raw, false);
        }
        if (start <= lines.Length - 1) Slice(lines.Length);
        return (imports ? null : slides, Math.Max(1, slides));
    }

    /// <summary>Slidev's HTML comment state across a line.</summary>
    private static bool AdvanceComment(string line, bool inComment)
    {
        int cursor = 0;
        while (cursor < line.Length)
        {
            if (inComment)
            {
                int end = line.IndexOf("-->", cursor, StringComparison.Ordinal);
                if (end < 0) return true;
                inComment = false;
                cursor = end + 3;
            }
            else
            {
                int s = line.IndexOf("<!--", cursor, StringComparison.Ordinal);
                if (s < 0) return false;
                int end = line.IndexOf("-->", s + 4, StringComparison.Ordinal);
                if (end < 0) return true;
                cursor = end + 3;
            }
        }
        return inComment;
    }

    [GeneratedRegex(@"^\s*`+")]
    private static partial Regex LeadingBackticks();

    [GeneratedRegex(@"\bslidev(?:\s+(?:build|export|dev))?\s+(?:--?[\w-]+(?:[ =]\S+)?\s+)*([^\s-][^\s]*\.md)\b")]
    private static partial Regex ScriptEntryRegex();
}
