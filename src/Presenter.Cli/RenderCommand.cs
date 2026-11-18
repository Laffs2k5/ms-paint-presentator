using System.Diagnostics;
using Presenter.Core;
using Presenter.Slides;
using Presenter.Windows;

namespace Presenter.Cli;

/// <summary>`presenter render`: a deck's slides as PNGs at a size, through
/// the cache, without Paint (research 05 §9). Useful to warm the cache before
/// a talk, or to check that a deck renders.</summary>
internal static class RenderCommand
{
    public static async Task<int> Run(string[] args)
    {
        string? deckPath = null, outDir = null, nodeTools = null, monitorArg = null;
        PixelSize? size = null;
        for (int i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new UsageException($"{args[i]} needs a value");
            switch (args[i])
            {
                case "--size": size = PixelSize.Parse(Next()); break;
                case "--monitor": monitorArg = Next(); break;
                case "--out": outDir = Next(); break;
                case "--node-tools": nodeTools = Next(); break;
                case var a when a.StartsWith("--"): throw new UsageException($"unknown option {a}");
                default: deckPath = deckPath is null ? args[i] : throw new UsageException("one deck at a time"); break;
            }
        }
        if (deckPath is null) throw new UsageException("which deck?");
        DeckInfo info;
        try { info = Decks.Inspect(deckPath); }
        catch (Exception e) when (e is FileNotFoundException or NotSupportedException) { throw new UsageException(e.Message); }
        // the size a show on that display would use, unless one is given
        size ??= ScreenOptions.For(Monitors.Find(monitorArg)).SlideFor(info.Aspect);
        Console.Error.WriteLine($"{info.Kind} deck, {(info.SlideCount is int n ? $"{n} slides" : "slides not counted yet")}, "
                                + $"shape {info.Aspect:F3}: rendering {size}");
        var sw = Stopwatch.StartNew();
        nodeTools ??= Environment.GetEnvironmentVariable("PRESENTER_NODE_TOOLS");
        await using var render = SlideRender.Start(info, size.Value, new RenderOptions(DeckSources.CacheDir, nodeTools));
        double? first = render.Done >= 1 ? 0 : null;
        render.SlideReady += i => { if (i == 1) first ??= sw.Elapsed.TotalSeconds; };
        try
        {
            await render.Completion;
        }
        catch (SlideRenderException e)
        {
            Console.Error.WriteLine($"presenter: {e.Message}");
            return 1;
        }
        Console.Error.WriteLine($"{render.Total} slides in {sw.Elapsed.TotalSeconds:F1} s"
                                + (render.FromCache ? " (from the cache)" : first is { } f ? $", the first after {f:F1} s" : ""));
        if (outDir is not null)
        {
            Directory.CreateDirectory(outDir);
            for (int i = 1; i <= render.Total; i++)
                File.Copy(render.SlidePath(i), Path.Combine(outDir, $"slide{i:000}.png"), overwrite: true);
            Console.Error.WriteLine($"copied to {Path.GetFullPath(outDir)}");
        }
        else
        {
            Console.Error.WriteLine($"in the cache: {render.Folder}");
        }
        return 0;
    }
}

/// <summary>`presenter setup`: installs Marp's command line into the
/// presenter's own Node tools folder, for Marp decks that bring none (Slidev
/// decks use their project's own Slidev). Needs Node and npm.</summary>
internal static class SetupCommand
{
    public static int Run(string[] args)
    {
        string dir = args.Length > 0 ? args[0] : RenderOptions.DefaultNodeToolsDir;
        Directory.CreateDirectory(dir);
        Console.Error.WriteLine($"installing @marp-team/marp-cli into {dir}");
        using var p = Process.Start(new ProcessStartInfo("cmd.exe")
        {
            ArgumentList = { "/c", "npm", "install", "--prefix", dir, "@marp-team/marp-cli" },
            UseShellExecute = false,
        }) ?? throw new InvalidOperationException("npm did not start");
        p.WaitForExit();
        Console.Error.WriteLine(p.ExitCode == 0 ? "done" : $"npm failed ({p.ExitCode}): is Node installed?");
        return p.ExitCode == 0 ? 0 : 1;
    }
}
