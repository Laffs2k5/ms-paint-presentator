using System.Diagnostics;
using System.Text.Json;
using Presenter.Core;
using Presenter.Windows;

namespace Presenter.Cli;

/// <summary>Options of `presenter present`.</summary>
internal sealed record PresentOptions
{
    public string? Deck { get; set; }
    public string? Monitor { get; set; }
    public bool Pick { get; set; }
    public bool Painting { get; set; } = true;
    public string Title { get; set; } = "Paint Presentator";
    // for tests and demos
    public bool Auto { get; set; }
    public bool Verify { get; set; }
    public string? Snapshot { get; set; }
    public int? Seed { get; set; }
    public double SimulatePrep { get; set; }
    public double InterruptAfter { get; set; }
    public double SimulateDisplayChange { get; set; }
    public bool BackgroundPaste { get; set; }

    public static PresentOptions Parse(string[] args)
    {
        var o = new PresentOptions();
        for (int i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new UsageException($"{args[i]} needs a value");
            switch (args[i])
            {
                case "--monitor": o.Monitor = Next(); break;
                case "--pick": o.Pick = true; break;
                case "--no-painting": o.Painting = false; break;
                case "--title": o.Title = Next(); break;
                case "--auto": o.Auto = true; break;
                case "--verify": o.Verify = true; break;
                case "--snapshot": o.Snapshot = Next(); break;
                case "--seed": o.Seed = int.Parse(Next()); break;
                case "--simulate-prep": o.SimulatePrep = double.Parse(Next()); break;
                case "--interrupt-after": o.InterruptAfter = double.Parse(Next()); break;
                case "--simulate-display-change": o.SimulateDisplayChange = double.Parse(Next()); break;
                case "--background-paste": o.BackgroundPaste = true; break;
                case var a when a.StartsWith("--"): throw new UsageException($"unknown option {a}");
                default: o.Deck = o.Deck is null ? args[i] : throw new UsageException("one deck at a time"); break;
            }
        }
        return o;
    }
}

/// <summary>
/// `presenter present`: the whole show (research 05 §4). Screen, Paint at the
/// slide size, the loading painting while the slides are made, then the
/// slides by key; on a display change the picker comes back and the show
/// resumes where it was.
/// </summary>
internal static class PresentCommand
{
    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    public static void Log(string message) => Console.Error.WriteLine($"{Clock.Elapsed.TotalSeconds,6:F1}s {message}");

    public static async Task<int> Run(string[] args)
    {
        var o = PresentOptions.Parse(args);
        if (o.Deck is null) throw new UsageException("which deck?");
        IDeckSource deck = DeckSources.Open(o.Deck);
        var summary = new Dictionary<string, object?> { ["started"] = DateTime.Now.ToString("s") };

        // the screen: named, picked, or the only one there is
        Presenter.Windows.Monitor monitor;
        ScreenOption option;
        if (o.Monitor is not null || (!o.Pick && Monitors.All().Count == 1))
        {
            monitor = Monitors.Find(o.Monitor);
            option = ScreenOptions.For(monitor);
        }
        else
        {
            var picked = await ScreenOptions.Pick(deck.Aspect, "Where do you present?");
            if (picked is null) return 1;
            (option, monitor) = picked.Value;
        }
        summary["screen"] = option.ToString();

        // pre-flight (E19, E6)
        if (!Power.OnMains()) Log("pre-flight: on battery; Paint is slower and less steady (E6). Plug in the charger.");
        Log("pre-flight: switch Do not disturb on, or notifications show over the slides (E19).");
        Power.KeepAwake(true);

        using var clipboard = new ClipboardHost();
        using var stage = new PaintStage(clipboard, Log) { ForceBackgroundRoute = o.BackgroundPaste };
        using var renders = new CancellationTokenSource();
        // Ctrl+C in the terminal ends the show like Esc: Paint closed, settings put back
        using var ending = new CancellationTokenSource();
        ConsoleCancelEventHandler onCtrlC = (_, e) => { e.Cancel = true; ending.Cancel(); };
        Console.CancelKeyPress += onCtrlC;
        PaintSession? paint = null;
        KeyHook? hook = null;
        SyntheticPen? pen = null;
        Toolbox? tools = null;
        (int X, int Y) origin = default;
        try
        {
            // Paint once, at the predicted size; again only if the prediction was off (E13)
            var size = option.SlideFor(deck.Aspect);
            paint = await stage.OpenPaint(monitor, size);
            int launches = 1;
            var viewport = await stage.Run(p => p!.Viewport()) ?? throw new PaintException("Paint's viewport was not found");
            if (SlideGeometry.SlideSize(viewport.Size, deck.Aspect) != size)
            {
                Log($"viewport {viewport.Size}, not {option.Viewport} as predicted: reopening Paint");
                size = SlideGeometry.SlideSize(viewport.Size, deck.Aspect);
                paint = await stage.OpenPaint(monitor, size);
                launches++;
            }
            Log($"viewport {viewport.Size}, slide {size}");
            summary["slide size"] = size.ToString();
            summary["Paint launches"] = launches;

            var slides = deck.Prepare(size, Log, renders.Token);
            var (ox, oy, dx, dy) = await stage.Run(p =>
            {
                p!.Focus();
                Keyboard.Tap('S');  // the Select tool: no brush panel over the canvas
                Thread.Sleep(300);
                return p.MeasureOrigin();
            });
            summary["origin offset from UIA"] = new[] { dx, dy };
            origin = (ox, oy);
            // the hook is on during the painting: the speaker's first key stops it
            hook = new KeyHook(paint.Window);
            if (!hook.Alive()) Log("the keyboard hook did not answer its heartbeat");
            (pen, tools) = await stage.Run(p => { var pn = new SyntheticPen(); return (pn, new Toolbox(p!, pn)); });

            if (o.Painting)
                summary["painting"] = await Paint(o, stage, tools, pen, hook, slides, origin, size, ending.Token);
            await slides.WaitReadyAsync(1, ending.Token);

            // the canvas as it is now becomes slide 0
            byte[] painting = await stage.Run(p => GrabCanvas(p!, (dx, dy), size));
            slides.Set(0, painting);
            stage.SetSlides(slides.Get);
            if (o.Snapshot is not null) File.WriteAllBytes(o.Snapshot, painting);

            VerifyingStage? verifier = null;
            IStage Staged(ReadySlides s, PixelSize sz) => o.Verify
                ? verifier = new VerifyingStage(stage, s.Get, p => CanvasRect(p, (dx, dy), sz), sz)
                : stage;
            var controller = new ShowController(Staged(slides, size), slides, hook.Keys, startAtPainting: true, Log);
            using var stopAuto = new CancellationTokenSource();
            Task autoKeys = o.Auto ? Task.Run(() => AutoKeys(stopAuto.Token)) : Task.CompletedTask;
            Log("Ready: Right, Space or Enter for the first slide; Esc ends.");
            // no blocking collections while the show runs (research 05 §7.5, E23)
            var gcMode = System.Runtime.GCSettings.LatencyMode;
            System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency;

            // the show, until Esc (research 05 §4, §5, §7.4). A display change brings
            // the picker back; Paint closed or crashed is reopened; Paint moved or
            // un-maximised is put back. Each resumes at the current slide.
            var resumes = new List<object>();
            var current = paint;
            bool paused = false;
            using var stopWatch = new CancellationTokenSource();
            CancellationTokenSource? interrupted = null;
            string? reason = null;
            void Interrupt(string why)
            {
                if (paused || interrupted is null || interrupted.IsCancellationRequested) return;
                reason = why;
                interrupted.Cancel();
            }
            var watchdog = Task.Run(async () =>
            {
                for (int tick = 1; !stopWatch.IsCancellationRequested; tick++)
                {
                    try { await Task.Delay(500, stopWatch.Token); } catch (OperationCanceledException) { return; }
                    if (paused) continue;
                    if (!current.Alive) Interrupt("paint-gone");
                    else if (!current.Maximised) Interrupt("paint-moved");
                    // the hook: a heartbeat every 5 s; two misses in a row and it is reinstalled
                    if (tick % 10 == 0 && !hook.Alive(1000) && !hook.Alive(1000))
                        Log(hook.Reinstall() ? "the keyboard hook stopped answering: reinstalled" : "the keyboard hook stopped answering, and reinstalling failed");
                }
            });
            void OnDisplayChange(object? s, EventArgs e) => Interrupt("display-change");
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplayChange;
            using var onEnd = ending.Token.Register(() => { paused = false; Interrupt("end"); });
            try
            {
                while (true)
                {
                    using var cts = new CancellationTokenSource();
                    interrupted = cts;
                    paused = false;
                    if (o.SimulateDisplayChange > 0 && resumes.Count == 0)
                        _ = Task.Delay(TimeSpan.FromSeconds(o.SimulateDisplayChange)).ContinueWith(_ => Interrupt("display-change"));
                    try
                    {
                        await controller.RunAsync(cts.Token);
                        break;  // Esc
                    }
                    catch (OperationCanceledException) when (cts.IsCancellationRequested)
                    {
                        paused = true;
                    }
                    if (reason == "end" || ending.IsCancellationRequested)
                    {
                        Log("Ctrl+C: the show ends");
                        break;
                    }
                    var sw = Stopwatch.StartNew();
                    string work;
                    if (reason == "display-change")
                    {
                        Log("the displays changed: the screen picker waits for the speaker");
                        var picked = await ScreenOptions.Pick(deck.Aspect, "The displays changed. Where now?");
                        if (picked is null)
                        {
                            Log("no screen chosen: the show ends");
                            break;
                        }
                        var resume = ScreenChoice.WorkAfterChange(option, picked.Value.Option, size, deck.Aspect);
                        (option, monitor) = picked.Value;
                        work = resume.ToString();
                        Log($"resuming on {option}: {work}");
                        if (resume == ResumeWork.NewSlideSize)
                        {
                            // Paint at the new size, the slides again, the painting fitted (research 05 §5)
                            size = option.SlideFor(deck.Aspect);
                            current = paint = await stage.OpenPaint(monitor, size);
                            (dx, dy, origin, pen, tools) = await Reopened(stage, hook, paint, pen);
                            var resized = deck.Prepare(size, Log, renders.Token);
                            resized.Set(0, ImageFolderDeck.Fit(painting, size));
                            slides = resized;
                            stage.SetSlides(slides.Get);
                            controller = new ShowController(Staged(slides, size), slides, hook.Keys, startAtPainting: false, Log, controller.Navigator);
                        }
                        else
                        {
                            await stage.Run(p => { p!.PlaceOn(monitor); p.Focus(); });
                        }
                    }
                    else if (reason == "paint-gone")
                    {
                        work = "Paint reopened";
                        Log("Paint is gone (closed or crashed): reopening it");
                        current = paint = await stage.OpenPaint(monitor, size);
                        (dx, dy, origin, pen, tools) = await Reopened(stage, hook, paint, pen);
                    }
                    else
                    {
                        work = "Paint put back";
                        Log("Paint is no longer maximised: putting it back");
                        await stage.Run(p => { p!.PlaceOn(monitor); p.Focus(); });
                    }
                    await controller.ShowCurrentAsync();
                    resumes.Add(new { reason, work, screen = option.ToString(), seconds = Math.Round(sw.Elapsed.TotalSeconds, 1) });
                }
            }
            finally
            {
                Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplayChange;
                stopWatch.Cancel();
                await watchdog;
            }
            System.Runtime.GCSettings.LatencyMode = gcMode;
            stopAuto.Cancel();
            await autoKeys;
            if (resumes.Count > 0) summary["resumed"] = resumes;

            var timed = controller.Changes.Where(c => c.Milliseconds is not null).Select(c => c.Milliseconds!.Value).Order().ToList();
            summary["slide changes"] = controller.Changes;
            if (verifier is not null)
                summary["verified"] = new { min = verifier.Results.Min(r => r.Match), all = verifier.Results.Select(r => $"{r.Frame}: {r.Match}") };
            summary["key to slide ms"] = timed.Count == 0 ? null : new
            {
                median = timed[timed.Count / 2],
                max = timed[^1],
                n = timed.Count,
                not_confirmed = controller.Changes.Count - timed.Count,
            };
        }
        catch (OperationCanceledException) when (ending.IsCancellationRequested)
        {
            Log("Ctrl+C: the show ends");
        }
        finally
        {
            Console.CancelKeyPress -= onCtrlC;
            renders.Cancel();
            hook?.Dispose();
            if (paint is not null)
                await stage.Run(p =>
                {
                    // Paint keeps the Text font and size: put the user's back (E16, E27)
                    try
                    {
                        if (tools?.RestoreTextSettings(origin.X + 200, origin.Y + 200) == true)
                            Log("the Text tool's font, size and background fill put back");
                    }
                    catch (Exception e) when (e is PaintException or InvalidOperationException or System.Runtime.InteropServices.COMException)
                    {
                        Log($"could not put the Text settings back ({e.Message}); the next show will");
                    }
                    p?.Close(discard: true);
                    pen?.Dispose();
                });
            clipboard.Clear();
            Power.KeepAwake(false);
        }
        string json = JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true });
        Console.WriteLine(json);
        SaveLog(json);
        return 0;
    }

    /// <summary>Every show's timings to a log file (research 05 §7.8), so "it felt
    /// slow" has numbers behind it. Never the deck's name or its content.
    /// The newest 50 are kept.</summary>
    private static void SaveLog(string json)
    {
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PaintPresentator", "logs");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, $"show-{DateTime.Now:yyyyMMdd-HHmmss}.json"), json);
            foreach (var old in new DirectoryInfo(dir).GetFiles("show-*.json").OrderByDescending(f => f.Name).Skip(50)) old.Delete();
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static async Task<object> Paint(PresentOptions o, PaintStage stage, Toolbox tools, SyntheticPen pen, KeyHook hook,
                                            ReadySlides slides, (int X, int Y) origin, PixelSize size, CancellationToken ending)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(hook.FirstKey.Token, ending);
        var started = Stopwatch.StartNew();
        PaintingProgress Progress()
        {
            int done = slides.ReadyCount;
            if (o.SimulatePrep > 0)  // for tests: slides "become ready" over that many seconds
                done = Math.Min(done, (int)(slides.Count * started.Elapsed.TotalSeconds / o.SimulatePrep));
            return new PaintingProgress(done, slides.Count);
        }
        if (o.InterruptAfter > 0)  // for tests: the speaker presses Right during the painting
            _ = Task.Delay(TimeSpan.FromSeconds(o.InterruptAfter)).ContinueWith(_ => Keyboard.Tap(0x27, tagged: false));
        var (stopped, steps) = await stage.Run(p =>
        {
            var lp = new LoadingPainting(p!, tools, pen, origin, size, o.Title, Progress, stop.Token, o.Seed, Log);
            bool interrupted = lp.Run();
            return (interrupted, lp.Steps);
        });
        return new { seconds = Math.Round(started.Elapsed.TotalSeconds, 1), interrupted = stopped, steps };
    }

    /// <summary>After Paint was opened again: the hook on the new window, the
    /// blank canvas's origin measured, a pen and toolbox for it.</summary>
    private static async Task<(int Dx, int Dy, (int X, int Y) Origin, SyntheticPen Pen, Toolbox Tools)> Reopened(
        PaintStage stage, KeyHook hook, PaintSession paint, SyntheticPen oldPen)
    {
        hook.SetTargets(paint.Window);
        return await stage.Run(p =>
        {
            p!.Focus();
            Keyboard.Tap('S');
            Thread.Sleep(300);
            var (x, y, dx, dy) = p.MeasureOrigin();
            oldPen.Dispose();
            var pen = new SyntheticPen();
            return (dx, dy, (x, y), pen, new Toolbox(p, pen));
        });
    }

    private static PixelRect CanvasRect(PaintSession p, (int Dx, int Dy) offset, PixelSize size)
    {
        var c = p.CanvasOnScreen() ?? throw new PaintException("the canvas was not found");
        return PixelRect.FromSize(c.Left + offset.Dx, c.Top + offset.Dy, size);
    }

    private static byte[] GrabCanvas(PaintSession p, (int Dx, int Dy) offset, PixelSize size)
    {
        using var bmp = ScreenCapture.Grab(CanvasRect(p, offset, size));
        return ScreenCapture.Png(bmp);
    }

    /// <summary>Scripted "speaker" keys, untagged so the hook takes them as the
    /// speaker's (E13's script): next, back, the painting, a jump, black, a
    /// burst, Esc.</summary>
    private static void AutoKeys(CancellationToken stop)
    {
        (int Vk, int WaitMs)[] script =
        [
            (0x27, 1200), (0x27, 1200), (0x20, 1200), (0x25, 1200), (0x23, 1200),
            (0x24, 1200),                // Home: the painting
            (0x33, 200), (0x0D, 1200),   // 3 + Enter
            (0x30, 200), (0x0D, 1200),   // 0 + Enter: the painting
            (0x27, 1200), (0x25, 1200),  // Right to slide 1, Left back to the painting
            (0x42, 1200), (0x42, 1200),  // black and back
            (0x27, 50), (0x27, 50), (0x27, 50), (0x27, 1500), (0x1B, 0),
        ];
        Thread.Sleep(1500);
        foreach (var (vk, wait) in script)
        {
            if (stop.IsCancellationRequested) return;
            Keyboard.Tap(vk, tagged: false);
            Thread.Sleep(wait);
        }
    }
}

internal static class DeckSources
{
    public static IDeckSource Open(string path)
    {
        if (ImageFolderDeck.Is(path)) return new ImageFolderSource(path);
        throw new UsageException($"not a deck this presenter can show yet: {path}");
    }
}
