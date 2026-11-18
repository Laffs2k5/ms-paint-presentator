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

    /// <summary>For tests: always paste the background way (E2), never with keys.</summary>
    public bool ForceBackgroundRoute { get; set; }

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
        bool background = ForceBackgroundRoute;
        if (!background && !paint.InFront() && !paint.Focus(1000))
        {
            _log?.Invoke("Paint is not in front: pasting in the background (E2)");
            background = true;
        }
        if (!background)
        {
            _clipboard.Offer(png);
            long? seen = null;
            if (paint.InFront())  // Ctrl+V only into Paint, never into another app
            {
                Keyboard.Ctrl('V');
                seen = WaitFor(paint, floats: true, timeoutMs: 2500);
            }
            paint.Key('S');  // posted instead if Paint lost the focus meanwhile
            if (seen is not null)
            {
                if (WaitFor(paint, floats: false, timeoutMs: 1000) is null) _log?.Invoke("the commit was not seen within 1 s");
                return seen;
            }
            // The paste went nowhere: another window had the focus, or Paint's
            // clipboard-error dialog was up. Clear the way and paste once more,
            // the background way, which needs no focus (E2).
            bool dialog = paint.DismissClipboardError();
            _log?.Invoke($"the paste was not seen within 2.5 s{(dialog ? " (Paint's error dialog closed)" : "")}{(paint.InFront() ? "" : " (Paint was not in front)")}: once more, in the background");
        }
        // The background route (E2): Paint reads the clipboard within ~8 ms of the
        // posted command, but does not enable its Crop button for it, so the paste
        // cannot be seen; give it time to draw, commit with a posted S (0.3-0.4 s),
        // and report the change as unconfirmed.
        _clipboard.Offer(png);
        paint.PostPaste();
        Thread.Sleep(400);
        paint.PostKey('S');
        Thread.Sleep(450);
        return null;
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
