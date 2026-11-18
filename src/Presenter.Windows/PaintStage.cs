using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using Presenter.Core;

namespace Presenter.Windows;

/// <summary>
/// The stage (research 05 §2): the only writer to Paint. Every Paint action
/// (pastes, keys, pen strokes, UIA) runs on this one thread, one command at a
/// time, because Paint is one UI thread and interleaved sequences would
/// confuse it. The session is created on this thread too, so its UIA objects
/// belong here.
/// </summary>
public sealed class PaintStage : IStage, IDisposable
{
    private readonly BlockingCollection<Action> _work = new();
    private readonly Thread _thread;
    private readonly ClipboardHost _clipboard;
    private readonly Action<string>? _log;
    private Func<int, byte[]>? _slidePng;
    private byte[]? _black, _white;

    public PaintSession? Paint { get; private set; }

    public PaintStage(ClipboardHost clipboard, Action<string>? log = null)
    {
        _clipboard = clipboard;
        _log = log;
        _thread = new Thread(Loop) { IsBackground = true, Name = "stage" };
        _thread.Start();
    }

    private void Loop()
    {
        foreach (var action in _work.GetConsumingEnumerable()) action();
        Paint?.Dispose();
    }

    /// <summary>Run work on the stage thread.</summary>
    public Task<T> Run<T>(Func<PaintSession?, T> work)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _work.Add(() =>
        {
            try { done.SetResult(work(Paint)); }
            catch (Exception e) { done.SetException(e); }
        });
        return done.Task;
    }

    public Task Run(Action<PaintSession?> work) => Run<bool>(p => { work(p); return true; });

    /// <summary>Open Paint (on the stage thread) for a canvas of the size.</summary>
    public Task<PaintSession> OpenPaint(Monitor monitor, PixelSize canvas) => Run(old =>
    {
        old?.Close(discard: true);
        old?.Dispose();
        Paint = PaintSession.Launch(monitor, canvas, _log);
        _black = ScreenCapture.SolidPng(canvas, Color.Black);
        _white = ScreenCapture.SolidPng(canvas, Color.White);
        return Paint;
    });

    /// <summary>Where the PNG for slide i comes from (0 is the loading painting).</summary>
    public void SetSlides(Func<int, byte[]> slidePng) => _slidePng = slidePng;

    public Task<long?> ShowAsync(Frame frame, CancellationToken ct) => Run(paint =>
    {
        if (paint is null) throw new InvalidOperationException("Paint is not open");
        byte[] png = frame switch
        {
            Frame.Blank { Colour: BlankColour.Black } => _black!,
            Frame.Blank => _white!,
            Frame.Slide s => (_slidePng ?? throw new InvalidOperationException("no slides set"))(s.Index),
            _ => throw new ArgumentException(frame.ToString()),
        };
        return PasteAndCommitGuarded(paint, png);
    });

    /// <summary>
    /// A slide change (research 05 §3): a new clipboard object with the PNG,
    /// Ctrl+V tagged as ours, wait until the Crop button says the paste
    /// floats (E21), then S commits it (E14). Returns when it was seen.
    /// </summary>
    private long? PasteAndCommit(PaintSession paint, byte[] png)
    {
        long? seen = null;
        for (int attempt = 0; attempt < 2 && seen is null; attempt++)
        {
            if (attempt > 0)
            {
                // Ctrl+V went nowhere: another window had the focus, or Paint's
                // clipboard-error dialog was up. Clear the way and try once more.
                bool dialog = paint.DismissClipboardError();
                _log?.Invoke($"the paste was not seen within 2.5 s{(dialog ? " (Paint's error dialog closed)" : "")}{(paint.InFront() ? "" : " (Paint was not in front)")}: once more");
            }
            if (!paint.InFront() && !paint.Focus(1000))
                _log?.Invoke("Paint is not in front: the paste may go elsewhere");
            _clipboard.Offer(png);
            Keyboard.Ctrl('V');
            seen = WaitFor(paint, floats: true, timeoutMs: 2500);
        }
        Keyboard.Tap('S');
        if (seen is null)
        {
            _log?.Invoke("the paste was not seen");
            return null;
        }
        if (WaitFor(paint, floats: false, timeoutMs: 1000) is null)
            _log?.Invoke("the commit was not seen within 1 s");
        return seen;
    }

    private long? PasteAndCommitGuarded(PaintSession paint, byte[] png)
    {
        try
        {
            return PasteAndCommit(paint, png);
        }
        catch (PaintException e)
        {
            // Paint is gone or its UI changed under us: the show's watchdog
            // reopens it; this change counts as unconfirmed.
            _log?.Invoke($"the paste failed: {e.Message}");
            return null;
        }
    }

    private static long? WaitFor(PaintSession paint, bool floats, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (paint.SelectionFloats() == floats) return Stopwatch.GetTimestamp();
            Thread.Sleep(2);
        }
        return null;
    }

    public void Dispose()
    {
        _work.CompleteAdding();
        _thread.Join(5000);
    }
}
