using System.Diagnostics;
using Presenter.Core;
using Xunit.Abstractions;

namespace Presenter.Slides.Tests;

/// <summary>Marp and Slidev through Node and headless Edge (throwaway profiles
/// in the cache folder). Skipped when the tools are not installed.</summary>
[Collection("renders")]
public class NodeRenderTests(ITestOutputHelper output)
{
    /// <summary>The laptop panel's viewport from the POC (E11): the slide sizes
    /// the presenter would ask for, for a 16:9 and a 4:3 deck.</summary>
    private static readonly PixelSize Viewport = new(2784, 1499);

    private static RenderOptions Options(TempDir dir) =>
        new(Path.Combine(dir.Path, "cache"), NodeToolsDir: TestTools.NodeToolsDir);

    [RequiresFact(Tool.Marp)]
    public async Task Marp_renders_at_two_sizes_with_progress_and_hits_the_cache()
    {
        using var dir = new TempDir();
        var deck = Decks.Inspect(dir.File("talk.md", SampleDecks.Marp(6)));
        var options = Options(dir);
        var fitted = SlideGeometry.SlideSize(Viewport, deck.Aspect);
        foreach (var size in new[] { new PixelSize(1920, 1080), fitted })
        {
            var run = await RenderChecks.RunAsync(deck, size, options);
            output.WriteLine($"marp {size}: {run}");
            Assert.False(run.FromCache);
            Assert.Equal(6, run.TotalAtStart);
            Assert.Equal(6, run.Done);
            AssertProgress(run);
            var again = await RenderChecks.RunAsync(deck, size, options);
            output.WriteLine($"marp {size} again: {again}");
            Assert.True(again.FromCache);
            Assert.Equal(6, again.Done);
        }
        AssertNothingLeftRunning(options.CacheDir);
    }

    [RequiresFact(Tool.Marp)]
    public async Task Marp_4_3_deck_at_its_fitted_size()
    {
        using var dir = new TempDir();
        var deck = Decks.Inspect(dir.File("old.md", SampleDecks.Marp(3, "4:3")));
        Assert.Equal(4.0 / 3, deck.Aspect, 3);
        var size = SlideGeometry.SlideSize(Viewport, deck.Aspect);
        var run = await RenderChecks.RunAsync(deck, size, Options(dir));
        output.WriteLine($"marp 4:3 {size}: {run}");
        Assert.Equal(3, run.Done);
    }

    [RequiresFact(Tool.Marp)]
    public async Task Marp_unknown_theme_size_is_measured_from_the_first_image()
    {
        using var dir = new TempDir();
        // the theme is in a folder the inspector does not search, so the design size is a guess
        dir.File("elsewhere/deep/er/wide.css", "/* @theme wider */\n@import 'default';\nsection { width: 1600px; height: 700px; }\n");
        dir.File(".marprc.yml", "themeSet:\n  - ./elsewhere/deep/er/wide.css\n");
        var deck = Decks.Inspect(dir.File("deck.md", "---\nmarp: true\ntheme: wider\n---\n\n# One\n\n---\n\n# Two\n"));
        var run = await RenderChecks.RunAsync(deck, new PixelSize(1600, 700), Options(dir));
        output.WriteLine($"marp unknown theme: {run}");
        Assert.Contains(run.Notes, n => n.Contains("rendering again", StringComparison.Ordinal));
        Assert.Equal(2, run.Done);
    }

    [RequiresFact(Tool.Marp)]
    public async Task Pdf_made_by_marp_renders_at_two_sizes()
    {
        using var dir = new TempDir();
        var md = dir.File("talk.md", SampleDecks.Marp(5));
        var pdf = Path.Combine(dir.Path, "talk.pdf");
        await MarpToPdf(md, pdf, dir.Path);
        var deck = Decks.Inspect(pdf);
        Assert.Equal(DeckKind.Pdf, deck.Kind);
        Assert.Equal(5, deck.SlideCount);
        Assert.Equal(16.0 / 9, deck.Aspect, 2);
        var options = Options(dir);
        foreach (var size in new[] { new PixelSize(1920, 1080), SlideGeometry.SlideSize(Viewport, deck.Aspect) })
        {
            var run = await RenderChecks.RunAsync(deck, size, options);
            output.WriteLine($"pdf (marp) {size}: {run}");
            Assert.Equal(5, run.Done);
            AssertProgress(run);
            var again = await RenderChecks.RunAsync(deck, size, options);
            output.WriteLine($"pdf (marp) {size} again: {again}");
            Assert.True(again.FromCache);
        }
    }

    [RequiresFact(Tool.Slidev)]
    public async Task Slidev_renders_each_slide_in_its_final_state()
    {
        using var dir = new TempDir();
        dir.File("slides.md", SampleDecks.Slidev(4));
        var deck = Decks.Inspect(dir.Path);
        var options = Options(dir);
        var size = SlideGeometry.SlideSize(Viewport, deck.Aspect);
        var run = await RenderChecks.RunAsync(deck, size, options);
        output.WriteLine($"slidev {size}: {run}");
        Assert.Equal(4, run.TotalAtStart);
        Assert.Equal(4, run.Done); // one image per slide (no click steps), the hidden one left out
        AssertProgress(run);
        var again = await RenderChecks.RunAsync(deck, size, options);
        output.WriteLine($"slidev {size} again: {again}");
        Assert.True(again.FromCache);
        AssertNothingLeftRunning(options.CacheDir);
    }

    [RequiresFact(Tool.Marp)]
    public async Task Disposing_a_running_render_stops_its_processes()
    {
        using var dir = new TempDir();
        var deck = Decks.Inspect(dir.File("talk.md", SampleDecks.Marp(4)));
        var options = Options(dir);
        var render = SlideRender.Start(deck, new PixelSize(1280, 720), options);
        for (int i = 0; i < 100 && (render.Job?.ActiveProcesses ?? 0) < 2; i++) await Task.Delay(50); // node, then Edge
        output.WriteLine($"processes in the job before dispose: {render.Job?.ActiveProcesses}");
        await render.DisposeAsync();
        Assert.True(render.Completion.IsCanceled, render.Completion.Status.ToString());
        AssertNothingLeftRunning(options.CacheDir);
        // nothing complete was cached, so the next start renders again
        await using var next = SlideRender.Start(deck, new PixelSize(1280, 720), options);
        Assert.False(next.FromCache);
        await next.Completion;
        Assert.Equal(4, next.Done);
    }

    private static void AssertProgress(RenderRun run)
    {
        // SlideReady came in order, one per slide, and Done never passed Total
        var slides = run.Ready.Select(r => r.Slide).ToList();
        Assert.NotEmpty(slides);
        Assert.Equal(Enumerable.Range(slides[0], slides.Count), slides);
        Assert.Equal(run.Done, slides[^1]);
        Assert.All(run.Ready, r => Assert.True(r.Slide <= r.Total));
    }

    /// <summary>No Node or Edge process whose command line names our cache
    /// folder (where the throwaway profiles live) is still running.</summary>
    private void AssertNothingLeftRunning(string cacheDir)
    {
        var ps = "Get-CimInstance Win32_Process | Where-Object { $_.ProcessId -ne $PID -and $_.CommandLine -like '*" +
                 cacheDir.Replace("'", "''") + "*' } | ForEach-Object { $_.ProcessId }";
        var psi = new ProcessStartInfo("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", ps])
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi)!;
        var pids = p.StandardOutput.ReadToEnd().Trim();
        p.WaitForExit();
        output.WriteLine($"processes naming the cache folder: [{pids}]");
        Assert.Equal("", pids);
    }

    /// <summary>Marp's own PDF export, run the way the renderer runs tools.</summary>
    private static async Task MarpToPdf(string md, string pdf, string work)
    {
        var pkg = NodeTools.InDir(TestTools.NodeToolsDir, "@marp-team/marp-cli", "marp")!;
        var tmp = Path.Combine(work, "tmp");
        Directory.CreateDirectory(tmp);
        using var job = new JobObject();
        using var p = ChildProcess.Start(job, NodeTools.FindNode(pkg.ProjectDir),
            [pkg.Script, md, "--no-stdin", "--browser", "edge", "--browser-path", NodeTools.FindEdge(null), "--pdf", "-o", pdf],
            work, new Dictionary<string, string?> { ["TEMP"] = tmp, ["TMP"] = tmp }, Path.Combine(work, "marp-pdf.log"));
        var code = await p.Exited.WaitAsync(TimeSpan.FromMinutes(2));
        Assert.True(code == 0, p.LogTail());
        job.Terminate();
    }
}
