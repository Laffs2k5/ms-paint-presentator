using System.Globalization;
using System.Text;
using Presenter.Core;

namespace Presenter.Slides.Tests;

/// <summary>Tools the integration tests need; a test that lacks one is skipped.</summary>
public enum Tool
{
    Marp,
    Slidev,
    PowerPoint,
}

/// <summary>A fact that is skipped (not failed) when a tool it needs is missing.</summary>
public sealed class RequiresFactAttribute : FactAttribute
{
    public RequiresFactAttribute(params Tool[] tools)
    {
        foreach (var t in tools)
        {
            var missing = TestTools.Missing(t);
            if (missing is not null)
            {
                Skip = missing;
                return;
            }
        }
    }
}

/// <summary>
/// Where the tests find Node tools: PRESENTER_NODE_TOOLS, else the POC's
/// %TEMP%\e12-node (see poc/experiments/e12_render.py), which has marp-cli,
/// @slidev/cli, @slidev/theme-default and playwright-chromium.
/// </summary>
public static class TestTools
{
    public static string NodeToolsDir =>
        Environment.GetEnvironmentVariable("PRESENTER_NODE_TOOLS") is { Length: > 0 } dir
            ? dir
            : Path.Combine(Path.GetTempPath(), "e12-node");

    public static string? Missing(Tool tool)
    {
        switch (tool)
        {
            case Tool.Marp:
                return NodeAndEdge() ?? (NodeTools.InDir(NodeToolsDir, "@marp-team/marp-cli", "marp") is null
                    ? $"marp-cli not installed in {NodeToolsDir}" : null);
            case Tool.Slidev:
                return NodeAndEdge() ?? (NodeTools.InDir(NodeToolsDir, "@slidev/cli", "slidev") is null
                    ? $"@slidev/cli not installed in {NodeToolsDir}" : null);
            case Tool.PowerPoint:
                return Type.GetTypeFromProgID("PowerPoint.Application") is null ? "PowerPoint is not installed" : null;
            default:
                return $"unknown tool {tool}";
        }
    }

    private static string? NodeAndEdge()
    {
        try
        {
            NodeTools.FindNode(NodeToolsDir);
            NodeTools.FindEdge(null);
            return null;
        }
        catch (InvalidOperationException ex)
        {
            return ex.Message;
        }
    }
}

/// <summary>A temporary folder, deleted afterwards.</summary>
public sealed class TempDir : IDisposable
{
    public string Path { get; } =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "presenter-slides-tests", Guid.NewGuid().ToString("N")[..12]);

    public TempDir() => Directory.CreateDirectory(Path);

    public string File(string name, string content)
    {
        var p = System.IO.Path.Combine(Path, name);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(p)!);
        System.IO.File.WriteAllText(p, content, new UTF8Encoding(false));
        return p;
    }

    public void Dispose()
    {
        for (int i = 0; i < 5; i++)
        {
            try
            {
                if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
                return;
            }
            catch (IOException)
            {
                Thread.Sleep(200);
            }
            catch (UnauthorizedAccessException)
            {
                Thread.Sleep(200);
            }
        }
    }
}

/// <summary>Synthetic decks, written for these tests.</summary>
public static class SampleDecks
{
    /// <summary>A Marp deck of <paramref name="slides"/> slides; with a size directive when given.</summary>
    public static string Marp(int slides, string? size = null)
    {
        var sb = new StringBuilder("---\nmarp: true\n");
        if (size is not null) sb.Append("size: ").Append(size).Append('\n');
        sb.Append("---\n\n");
        for (int i = 1; i <= slides; i++)
        {
            if (i > 1) sb.Append("\n---\n\n");
            sb.Append("# Test slide ").Append(i).Append("\n\n- A point made up for the test\n- Another one\n");
        }
        return sb.ToString();
    }

    /// <summary>A Slidev deck of <paramref name="slides"/> slides; the last
    /// has a click step (exported in its final state) and one more hidden.</summary>
    public static string Slidev(int slides) =>
        "---\ntheme: default\ntitle: Test deck\n---\n\n# Test slide 1\n\nMade up for the test\n" +
        string.Concat(Enumerable.Range(2, slides - 1).Select(i =>
            i == slides
                ? $"\n---\nlayout: default\n---\n\n# Test slide {i}\n\n<v-clicks>\n\n- First\n- Second\n\n</v-clicks>\n"
                : $"\n---\n\n# Test slide {i}\n\n- A point\n")) +
        "\n---\nhide: true\n---\n\n# Hidden\n";

    /// <summary>
    /// A minimal PDF written by hand: one page per colour, each <paramref name="w"/>
    /// x <paramref name="h"/> points with a filled rectangle in that colour.
    /// </summary>
    public static byte[] Pdf(double w, double h, params (double R, double G, double B)[] pages)
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "", // pages, filled below
        };
        var kids = new List<string>();
        foreach (var (r, g, b) in pages)
        {
            var content = string.Create(CultureInfo.InvariantCulture,
                $"{r} {g} {b} rg {w * 0.1} {h * 0.1} {w * 0.5} {h * 0.5} re f");
            objects.Add($"<< /Length {content.Length} >>\nstream\n{content}\nendstream");
            int contentId = objects.Count;
            objects.Add(string.Create(CultureInfo.InvariantCulture,
                $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {w} {h}] /Contents {contentId} 0 R >>"));
            kids.Add($"{objects.Count} 0 R");
        }
        objects[1] = $"<< /Type /Pages /Kids [{string.Join(' ', kids)}] /Count {pages.Length} >>";
        var sb = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Count; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(objects.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append(o.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(objects.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n")
            .Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}

/// <summary>Checks on a render, shared by the integration tests.</summary>
public static class RenderChecks
{
    /// <summary>Starts a render and records what it reports: the total right
    /// after Start, every SlideReady in order, and the time to completion.</summary>
    public static async Task<RenderRun> RunAsync(DeckInfo deck, PixelSize size, RenderOptions options)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        await using var render = SlideRender.Start(deck, size, options);
        var startMs = clock.Elapsed.TotalMilliseconds;
        var totalAtStart = render.Total;
        var ready = new List<(int Slide, double Ms, int Total)>();
        render.SlideReady += n =>
        {
            lock (ready) ready.Add((n, clock.Elapsed.TotalMilliseconds, render.Total));
        };
        await render.Completion.WaitAsync(TimeSpan.FromMinutes(5));
        var run = new RenderRun(render.FromCache, totalAtStart, render.Total, render.Done, startMs,
            clock.Elapsed.TotalMilliseconds, ready.ToList(),
            Enumerable.Range(1, render.Done).Select(render.SlidePath).ToList(), render.Notes.ToList(),
            render.Job?.ActiveProcesses ?? 0);
        foreach (var f in run.Files)
            Assert.Equal(size, await SlideImage.SizeAsync(f));
        return run;
    }
}

public sealed record RenderRun(bool FromCache, int TotalAtStart, int Total, int Done, double StartMs, double TotalMs,
    List<(int Slide, double Ms, int Total)> Ready, List<string> Files, List<string> Notes, int LeftoverProcesses)
{
    public override string ToString() =>
        $"fromCache={FromCache} totalAtStart={TotalAtStart} total={Total} done={Done} start={StartMs:0.0} ms " +
        $"all={TotalMs:0} ms ready=[{string.Join(", ", Ready.Select(r => $"{r.Slide}@{r.Ms:0}ms"))}] " +
        $"notes=[{string.Join(" | ", Notes)}]";
}
