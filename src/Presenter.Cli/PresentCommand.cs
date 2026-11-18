using System.Diagnostics;
using System.Text.Json;
using Presenter.Core;
using Presenter.Windows;

namespace Presenter.Cli;

internal static class PresentCommand
{
    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    public static void Log(string message) => Console.Error.WriteLine($"{Clock.Elapsed.TotalSeconds,6:F1}s {message}");

    public static async Task<int> Run(string[] args)
    {
        string? deckPath = null, monitorArg = null;
        bool painting = true, auto = false, verify = false;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--monitor" when i + 1 < args.Length: monitorArg = args[++i]; break;
                case "--no-painting": painting = false; break;
                case "--auto": auto = true; break;
                case "--verify": verify = true; break;
                case var a when a.StartsWith("--"): throw new UsageException($"unknown option {a}");
                default: deckPath = deckPath is null ? args[i] : throw new UsageException("one deck at a time"); break;
            }
        }
        if (deckPath is null) throw new UsageException("which deck?");
        if (!ImageFolderDeck.Is(deckPath)) throw new UsageException($"not a folder of slide images: {deckPath}");
        var deck = new ImageFolderDeck(deckPath);
        var monitor = Monitors.Find(monitorArg);
        var summary = new Dictionary<string, object?> { ["screen"] = monitor.ToString() };

        // pre-flight (E19, E6)
        if (!Power.OnMains()) Log("pre-flight: on battery; Paint is slower and less steady (E6). Plug in the charger.");
        Log("pre-flight: switch Do not disturb on, or notifications show over the slides (E19).");
        Power.KeepAwake(true);

        using var clipboard = new ClipboardHost();
        using var stage = new PaintStage(clipboard, Log);
        PaintSession? paint = null;
        KeyHook? hook = null;
        try
        {
            // Paint once, at the predicted size; again only if the prediction was off (E13)
            var predicted = SlideGeometry.PredictedViewport(monitor.WorkArea.Size, monitor.Dpi);
            var size = SlideGeometry.SlideSize(predicted, deck.Aspect);
            paint = await stage.OpenPaint(monitor, size);
            int launches = 1;
            var viewport = await stage.Run(p => p!.Viewport()) ?? throw new PaintException("Paint's viewport was not found");
            if (SlideGeometry.SlideSize(viewport.Size, deck.Aspect) != size)
            {
                Log($"viewport {viewport.Size}, not {predicted} as predicted: reopening Paint");
                size = SlideGeometry.SlideSize(viewport.Size, deck.Aspect);
                paint = await stage.OpenPaint(monitor, size);
                launches++;
            }
            Log($"viewport {viewport.Size}, slide {size}");
            summary["slide size"] = size.ToString();
            summary["Paint launches"] = launches;

            // the slides, encoded ahead in the background (research 05 §7.5)
            var slides = new ReadySlides(deck.Files.Count);
            _ = Task.Run(() =>
            {
                for (int i = 1; i <= deck.Files.Count; i++) slides.Set(i, deck.Png(i, size));
            });

            var (ox, oy, dx, dy) = await stage.Run(p =>
            {
                p!.Focus();
                Keyboard.Tap('S');  // the Select tool: no brush panel over the canvas
                Thread.Sleep(300);
                return p.MeasureOrigin();
            });
            summary["origin offset from UIA"] = new[] { dx, dy };
            hook = new KeyHook(paint.Window);
            if (!hook.Alive()) Log("the keyboard hook did not answer its heartbeat");

            if (painting)
            {
                Log("loading painting: not ported yet; waiting for the slides");
            }
            await slides.WaitReadyAsync(1, CancellationToken.None);

            // the canvas as it is now becomes slide 0
            slides.Set(0, await stage.Run(p =>
            {
                var c = p!.CanvasOnScreen() ?? throw new PaintException("the canvas was not found");
                using var bmp = ScreenCapture.Grab(PixelRect.FromSize(c.Left + dx, c.Top + dy, size));
                return ScreenCapture.Png(bmp);
            }));
            stage.SetSlides(i => slides.Get(i));

            VerifyingStage? verifier = verify
                ? new VerifyingStage(stage, slides.Get, p =>
                  {
                      var c = p.CanvasOnScreen() ?? throw new PaintException("the canvas was not found");
                      return PixelRect.FromSize(c.Left + dx, c.Top + dy, size);
                  }, size)
                : null;
            var controller = new ShowController((IStage?)verifier ?? stage, slides, hook.Keys, startAtPainting: true, Log);
            using var stopAuto = new CancellationTokenSource();
            Task autoKeys = auto ? Task.Run(() => AutoKeys(stopAuto.Token)) : Task.CompletedTask;
            Log("Ready: Right, Space or Enter for the first slide; Esc ends.");
            await controller.RunAsync(CancellationToken.None);
            stopAuto.Cancel();
            await autoKeys;

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
        finally
        {
            hook?.Dispose();
            if (paint is not null) await stage.Run(p => { p?.Close(discard: true); });
            clipboard.Clear();
            Power.KeepAwake(false);
        }
        Console.WriteLine(JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
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

/// <summary>Slide PNGs as they become ready; index 0 is the loading painting.</summary>
internal sealed class ReadySlides(int count) : ISlideSet
{
    private readonly byte[]?[] _png = new byte[count + 1][];
    private readonly TaskCompletionSource[] _ready = Enumerable.Range(0, count + 1)
        .Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();

    public int Count => count;
    public bool IsReady(int index) => index >= 0 && index <= count && Volatile.Read(ref _png[index]) is not null;
    public Task WaitReadyAsync(int index, CancellationToken ct) => _ready[index].Task.WaitAsync(ct);
    public byte[] Get(int index) => _png[index] ?? throw new InvalidOperationException($"slide {index} is not ready");

    public void Set(int index, byte[] png)
    {
        Volatile.Write(ref _png[index], png);
        _ready[index].TrySetResult();
    }
}
