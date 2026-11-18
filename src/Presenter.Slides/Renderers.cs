using System.Globalization;
using System.Threading.Channels;
using Presenter.Core;

namespace Presenter.Slides;

/// <summary>How one deck is rendered: the renderer's name and version and the
/// files that decide the pixels (for the cache key), and the render itself.</summary>
internal sealed record RenderPlan(
    string Renderer,
    IReadOnlyList<string> Files,
    int? EstimatedTotal,
    Func<SlideRender, string, CancellationToken, Task> Run)
{
    private static readonly string[] LockFiles = ["package-lock.json", "npm-shrinkwrap.json", "pnpm-lock.yaml", "yarn.lock", "bun.lockb", "bun.lock"];

    public static RenderPlan For(DeckInfo deck, RenderOptions options) => deck.Kind switch
    {
        DeckKind.PowerPoint => new RenderPlan($"powerpoint {PowerPointDeck.RegisteredVersion() ?? "?"}", [deck.Path], null,
            (sink, work, ct) => PowerPointRenderer.RenderAsync(deck.Path, sink, work, ct)),
        DeckKind.Pdf => new RenderPlan($"windows.data.pdf {Environment.OSVersion.Version}", [deck.Path], null,
            (sink, _, ct) => PdfDeck.RenderAsync(deck.Path, sink, ct)),
        DeckKind.Marp => MarpPlan(deck, options),
        DeckKind.Slidev => SlidevPlan(deck, options),
        _ => throw new NotSupportedException(deck.Kind.ToString()),
    };

    private static RenderPlan MarpPlan(DeckInfo deck, RenderOptions options)
    {
        var marp = MarpDeck.Read(deck.Path);
        var dir = System.IO.Path.GetDirectoryName(deck.Path)!;
        var pkg = NodeTools.FindUpwards(dir, "@marp-team/marp-cli", "marp")
                  ?? NodeTools.InDir(options.ToolsDir, "@marp-team/marp-cli", "marp");
        var files = new List<string> { deck.Path };
        if (marp.ThemeFile is not null) files.Add(marp.ThemeFile);
        foreach (var name in new[] { ".marprc", ".marprc.yml", ".marprc.yaml", ".marprc.json", "marp.config.js",
                     "marp.config.mjs", "marp.config.cjs", "marp.config.ts", "package.json" }.Concat(LockFiles))
        {
            var f = System.IO.Path.Combine(dir, name);
            if (File.Exists(f)) files.Add(f);
        }
        return new RenderPlan(pkg is null ? "marp-cli missing" : $"marp-cli {pkg.Version}", files, marp.SlideCount,
            (sink, work, ct) => MarpRenderer.RenderAsync(marp, pkg, options, sink, work, ct));
    }

    private static RenderPlan SlidevPlan(DeckInfo deck, RenderOptions options)
    {
        var slidev = SlidevDeck.Read(deck.Path);
        var pkg = NodeTools.FindUpwards(slidev.Root, "@slidev/cli", "slidev")
                  ?? NodeTools.InDir(options.ToolsDir, "@slidev/cli", "slidev");
        var files = new List<string> { slidev.Entry };
        foreach (var name in new[] { "package.json", "vite.config.ts", "vite.config.js", "uno.config.ts", "style.css" }.Concat(LockFiles))
        {
            var f = System.IO.Path.Combine(slidev.Root, name);
            if (File.Exists(f)) files.Add(f);
        }
        return new RenderPlan(pkg is null ? "slidev missing" : $"slidev {pkg.Version}", files,
            slidev.SlideCount ?? slidev.SlideEstimate,
            (sink, work, ct) => SlidevRenderer.RenderAsync(slidev, pkg, options, sink, work, ct));
    }
}

/// <summary>Runs a Node renderer that writes one PNG per slide into a folder,
/// and feeds the images to the render in order as they are finished.</summary>
internal static class NodeExport
{
    /// <summary>
    /// Runs <c>node script args</c> in <paramref name="cwd"/>, inside the
    /// render's job object, with TEMP and TMP in the render's work folder (so
    /// Edge's throwaway profile is created there, never in the user's
    /// profile). Returns null when done, or the first image's size when
    /// <paramref name="acceptFirst"/> rejected it (the tool is then stopped).
    /// </summary>
    public static async Task<PixelSize?> RunAsync(string toolName, SlideRender sink, RenderOptions options,
        NodePackage pkg, IReadOnlyList<string> args, string cwd, string work, string rawDir,
        Func<string, int?> slideNumber, Func<PixelSize, bool>? acceptFirst, CancellationToken ct)
    {
        var node = NodeTools.FindNode(pkg.ProjectDir);
        var tmp = System.IO.Path.Combine(work, "tmp");
        Directory.CreateDirectory(tmp);
        Directory.CreateDirectory(rawDir);
        var env = new Dictionary<string, string?>
        {
            ["TEMP"] = tmp,
            ["TMP"] = tmp,
            ["PLAYWRIGHT_SKIP_BROWSER_DOWNLOAD"] = "1",
            ["NO_COLOR"] = "1",
            ["FORCE_COLOR"] = "0",
            ["BROWSER"] = "none",
        };
        var job = sink.NewToolJob();
        var log = System.IO.Path.Combine(work, $"{toolName}.log");
        var clock = System.Diagnostics.Stopwatch.StartNew();
        using var proc = ChildProcess.Start(job, node, [pkg.Script, .. args], cwd, env, log);
        await using var onCancel = ct.Register(job.Terminate);
        int next = sink.Queued + 1;
        double? firstAt = null;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            bool exited = proc.Exited.IsCompleted;
            if (exited && proc.Exited.Result != 0) break; // a failed run's last image may be cut short
            var files = Ready(rawDir, slideNumber);
            while (files.TryGetValue(next, out var file) && (exited || SlideImage.IsCompletePng(file)))
            {
                if (next == 1 && acceptFirst is not null)
                {
                    var got = await SlideImage.SizeAsync(file).ConfigureAwait(false);
                    if (!acceptFirst(got))
                    {
                        job.Terminate();
                        await proc.Exited.ConfigureAwait(false);
                        return got;
                    }
                }
                firstAt ??= clock.Elapsed.TotalSeconds;
                await sink.QueueFileAsync(file, ct).ConfigureAwait(false);
                next++;
            }
            if (exited) break;
            ct.ThrowIfCancellationRequested();
            if (clock.Elapsed > options.ToolTimeout)
            {
                job.Terminate();
                throw new TimeoutException($"{toolName} took longer than {options.ToolTimeout.TotalSeconds:0} s and was stopped. "
                                           + proc.LogTail(600));
            }
            await Task.WhenAny(proc.Exited, Task.Delay(100, ct)).ConfigureAwait(false);
        }
        ct.ThrowIfCancellationRequested();
        int code = await proc.Exited.ConfigureAwait(false);
        var left = job.ActiveProcesses;
        if (left > 0)
        {
            sink.Note($"{toolName}: ended {left} process(es) it left behind");
            job.Terminate();
        }
        sink.Note($"{toolName} {pkg.Version}: {next - 1} images, first after {firstAt ?? 0:0.00} s, done after {clock.Elapsed.TotalSeconds:0.00} s");
        if (code != 0)
            throw new InvalidOperationException($"{toolName} failed (exit code {code}): {proc.LogTail()}");
        if (next == 1)
            throw new InvalidOperationException($"{toolName} made no images. {proc.LogTail()}");
        return null;
    }

    private static Dictionary<int, string> Ready(string dir, Func<string, int?> slideNumber)
    {
        var found = new Dictionary<int, string>();
        foreach (var f in Directory.EnumerateFiles(dir, "*.png"))
            if (slideNumber(System.IO.Path.GetFileName(f)) is { } n) found[n] = f;
        return found;
    }

    public static string Num(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);

    public static NodePackage Require(NodePackage? pkg, string what, string package, RenderOptions options, string deckDir) =>
        pkg ?? throw new SlideRenderException(
            $"{what} was not found: neither the deck's own node_modules (at or above {deckDir}) nor the presenter's " +
            $"tools folder {options.ToolsDir} has {package}. Install it there with:  npm install --prefix " +
            $"\"{options.ToolsDir}\" {package}");
}

/// <summary>
/// Marp through marp-cli (E12, E13): <c>--images png --image-scale s</c>,
/// where s fits the deck's design canvas into the slide size, through Edge
/// headless. The deck renders as it is, with its own config and themes.
/// </summary>
internal static class MarpRenderer
{
    public static async Task RenderAsync(MarpDeck marp, NodePackage? found, RenderOptions options, SlideRender sink,
        string work, CancellationToken ct)
    {
        var deckDir = System.IO.Path.GetDirectoryName(marp.Path)!;
        var pkg = NodeExport.Require(found, "Marp CLI", "@marp-team/marp-cli", options, deckDir);
        var edge = NodeTools.FindEdge(options.EdgePath);
        var raw = System.IO.Path.Combine(work, "raw");
        var design = marp.Design;
        for (int attempt = 0; ; attempt++)
        {
            double scale = Math.Min(sink.Size.Width / (double)design.Width, sink.Size.Height / (double)design.Height);
            var expected = new PixelSize((int)Math.Round(design.Width * scale), (int)Math.Round(design.Height * scale));
            string[] args =
            [
                marp.Path, "--no-stdin", "--allow-local-files",
                "--browser", "edge", "--browser-path", edge,
                "--images", "png", "--image-scale", NodeExport.Num(scale),
                "-o", System.IO.Path.Combine(raw, "deck.png"),
            ];
            var rejected = await NodeExport.RunAsync("marp", sink, options, pkg, args, deckDir, work, raw, SlideNumber,
                attempt == 0
                    ? got => Math.Abs(got.Width - expected.Width) <= 2 && Math.Abs(got.Height - expected.Height) <= 2
                    : null,
                ct).ConfigureAwait(false);
            if (rejected is not { } got) return;
            // the theme's canvas was not what the deck said: measure it from the image and go again
            design = new PixelSize((int)Math.Round(got.Width / scale), (int)Math.Round(got.Height / scale));
            sink.Note($"marp: design canvas is {design}, not {marp.Design}; rendering again");
            Directory.Delete(raw, recursive: true);
        }
    }

    /// <summary><c>deck.001.png</c> → 1.</summary>
    internal static int? SlideNumber(string file)
    {
        var parts = file.Split('.');
        return parts.Length >= 3 && int.TryParse(parts[^2], NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;
    }
}

/// <summary>
/// Slidev through the project's own Slidev: <c>slidev export --format png
/// --scale s</c>, without <c>--with-clicks</c> (each slide in its final
/// state), through Edge headless via its Playwright.
/// </summary>
internal static class SlidevRenderer
{
    public static async Task RenderAsync(SlidevDeck deck, NodePackage? found, RenderOptions options, SlideRender sink,
        string work, CancellationToken ct)
    {
        var pkg = NodeExport.Require(found, "Slidev", "@slidev/cli", options, deck.Root);
        var edge = NodeTools.FindEdge(options.EdgePath);
        var raw = System.IO.Path.Combine(work, "raw");
        double scale = Math.Min(sink.Size.Width / (double)deck.CanvasWidth, sink.Size.Height / (double)deck.CanvasHeight);
        string[] args =
        [
            "export", deck.Entry, "--format", "png", "--scale", NodeExport.Num(scale), "--output", raw,
            "--executable-path", edge, "--timeout", "60000",
        ];
        await NodeExport.RunAsync("slidev", sink, options, pkg, args, deck.Root, work, raw, SlideNumber, null, ct)
            .ConfigureAwait(false);
    }

    /// <summary><c>7.png</c> or <c>07.png</c> → 7 (click steps, <c>07-2.png</c>, are not slides).</summary>
    internal static int? SlideNumber(string file)
    {
        var stem = System.IO.Path.GetFileNameWithoutExtension(file);
        return int.TryParse(stem, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;
    }
}

/// <summary>
/// PowerPoint through COM (E12, E25): opened read-only without a window,
/// hidden slides skipped, each slide exported with <c>Slide.Export</c> at the
/// largest size of the deck's exact shape in the slide size, then fitted.
/// Exporting (PowerPoint's thread) and fitting (here) overlap.
/// </summary>
internal static class PowerPointRenderer
{
    public static async Task RenderAsync(string deckPath, SlideRender sink, string work, CancellationToken ct)
    {
        var raw = System.IO.Path.Combine(work, "raw");
        Directory.CreateDirectory(raw);
        var files = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var export = PowerPointSession.RunAsync(session =>
        {
            try
            {
                using var pres = session.Open(deckPath, System.IO.Path.Combine(work, "deck"));
                var opened = clock.Elapsed.TotalSeconds;
                var visible = pres.VisibleSlides();
                sink.SetTotal(visible.Count);
                var exportSize = SlideImage.Contain(sink.Size, pres.Aspect);
                for (int k = 0; k < visible.Count; k++)
                {
                    stop.Token.ThrowIfCancellationRequested();
                    var file = System.IO.Path.Combine(raw, $"{k + 1:000}.png");
                    pres.Export(visible[k], file, exportSize.Width, exportSize.Height);
                    files.Writer.TryWrite(file);
                }
                var how = session.StartedPowerPoint ? "started" : "already running";
                sink.Note($"powerpoint: {how}, open after {opened:0.00} s, {visible.Count} slides exported at {exportSize} after {clock.Elapsed.TotalSeconds:0.00} s");
                files.Writer.TryComplete();
                return 0;
            }
            catch (Exception ex)
            {
                files.Writer.TryComplete(ex);
                throw;
            }
        }, stop.Token);
        try
        {
            await foreach (var file in files.Reader.ReadAllAsync(stop.Token).ConfigureAwait(false))
                await sink.QueueFileAsync(file, stop.Token).ConfigureAwait(false);
        }
        catch
        {
            await stop.CancelAsync().ConfigureAwait(false);
            try
            {
                await export.ConfigureAwait(false); // let PowerPoint close the deck (and quit) first
            }
            catch (Exception)
            {
            }
            throw;
        }
        await export.ConfigureAwait(false);
    }
}
