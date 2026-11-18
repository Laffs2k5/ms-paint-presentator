using System.Text.RegularExpressions;

namespace Presenter.Slides;

/// <summary>
/// The YAML front matter of a Markdown deck, read only as far as the
/// presenter needs: top-level <c>key: value</c> scalars. Nested values,
/// lists and multi-line strings are kept as their raw text or skipped.
/// </summary>
internal sealed partial class FrontMatter
{
    private readonly Dictionary<string, string> _values;

    private FrontMatter(Dictionary<string, string> values, int bodyLine)
    {
        _values = values;
        BodyLine = bodyLine;
    }

    /// <summary>The index of the first line after the front matter (0 without one).</summary>
    public int BodyLine { get; }

    public bool Exists => BodyLine > 0;

    public string? this[string key] => _values.TryGetValue(key, out var v) ? v : null;

    public IEnumerable<string> Keys => _values.Keys;

    public bool IsTrue(string key) => this[key] is { } v && v.Equals("true", StringComparison.OrdinalIgnoreCase);

    public static string[] SplitLines(string text) =>
        text.TrimStart('﻿').Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

    /// <summary>Reads the front matter starting at <paramref name="start"/>
    /// (a line that is <c>---</c>), up to the closing <c>---</c>.</summary>
    public static FrontMatter Parse(string[] lines, int start = 0)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (start >= lines.Length || lines[start].TrimEnd() != "---") return new FrontMatter(values, 0);
        for (int i = start + 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.TrimEnd() == "---") return new FrontMatter(values, i + 1);
            ReadPair(line, values);
        }
        return new FrontMatter(new Dictionary<string, string>(), 0); // not closed: no front matter
    }

    /// <summary>Directives in HTML comments (<c>&lt;!-- size: 4:3 --&gt;</c>), as Marp
    /// allows for global directives. Later ones win.</summary>
    public static Dictionary<string, string> CommentDirectives(IEnumerable<string> comments)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var c in comments)
            foreach (var line in c.Split('\n'))
                ReadPair(line.Trim(), values);
        return values;
    }

    private static void ReadPair(string line, Dictionary<string, string> values)
    {
        var m = PairRegex().Match(line);
        if (!m.Success) return;
        var value = m.Groups[2].Value.Trim();
        if (value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[^1] == value[0])
            value = value[1..^1];
        else
        {
            int hash = value.IndexOf(" #", StringComparison.Ordinal);
            if (hash >= 0) value = value[..hash].TrimEnd();
        }
        values[m.Groups[1].Value] = value;
    }

    [GeneratedRegex(@"^\$?([A-Za-z_][\w-]*)\s*:\s*(.*)$")]
    private static partial Regex PairRegex();
}
