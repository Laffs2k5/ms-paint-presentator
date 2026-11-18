using System.Diagnostics;
using System.Threading.Channels;

namespace Presenter.Core;

/// <summary>Puts frames on Paint's canvas. One frame at a time; the
/// implementation (the stage) owns Paint.</summary>
public interface IStage
{
    /// <summary>Show the frame and return when it is on screen, with the
    /// <see cref="Stopwatch"/> timestamp it was seen at (null if it could not
    /// be confirmed).</summary>
    Task<long?> ShowAsync(Frame frame, CancellationToken ct);
}

/// <summary>The deck's slides as they become ready (the painting, index 0,
/// always is).</summary>
public interface ISlideSet
{
    /// <summary>The deck's number of slides, 0 until known.</summary>
    int Count { get; }
    bool IsReady(int index);
    Task WaitReadyAsync(int index, CancellationToken ct);
}

/// <summary>One change on the canvas, timed from the first key of its batch.</summary>
public sealed record SlideChange(string From, string To, double? Milliseconds, int Keys);

/// <summary>
/// The show's controller (research 05 §2, §3): reads key batches, decides
/// with the <see cref="Navigator"/>, and has the stage show the result. While
/// the stage is busy, keys queue up and become one batch, so a burst of Next
/// pastes only the last slide (E13).
/// </summary>
public sealed class ShowController
{
    private readonly IStage _stage;
    private readonly ISlideSet _slides;
    private readonly ChannelReader<KeyPress> _keys;
    private readonly Action<string>? _log;
    private readonly List<SlideChange> _changes = [];

    public Navigator Navigator { get; }
    public IReadOnlyList<SlideChange> Changes => _changes;

    public ShowController(IStage stage, ISlideSet slides, ChannelReader<KeyPress> keys,
                          bool startAtPainting = true, Action<string>? log = null)
    {
        _stage = stage;
        _slides = slides;
        _keys = keys;
        _log = log;
        Navigator = new Navigator(startAtPainting);
    }

    /// <summary>Run until the speaker ends the show (Esc) or <paramref name="ct"/> fires.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        var batch = new List<KeyPress>();
        while (!ct.IsCancellationRequested)
        {
            batch.Clear();
            batch.Add(await _keys.ReadAsync(ct).ConfigureAwait(false));
            await Task.Delay(10, ct).ConfigureAwait(false);  // a key's twin (key repeat, a remote's double code)
            while (_keys.TryRead(out var more)) batch.Add(more);

            string from = Describe();
            var result = Navigator.Apply(batch, _slides.Count);
            if (result.End) return;
            if (result.Show is not { } frame) continue;
            if (frame is Frame.Slide { Index: var i } && !_slides.IsReady(i))
            {
                _log?.Invoke($"{frame} is not rendered yet: waiting");
                await _slides.WaitReadyAsync(i, ct).ConfigureAwait(false);
            }
            long? seen = await _stage.ShowAsync(frame, ct).ConfigureAwait(false);
            double? ms = seen is { } s ? (s - batch[0].Timestamp) * 1000.0 / Stopwatch.Frequency : null;
            var change = new SlideChange(from, frame.ToString()!, ms is { } m ? Math.Round(m, 1) : null, batch.Count);
            _changes.Add(change);
            _log?.Invoke($"{change.From} -> {change.To}: {(ms is null ? "not confirmed" : $"{ms:F0} ms")}"
                         + (batch.Count > 1 ? $" ({batch.Count} keys)" : ""));
        }
    }

    private string Describe() => Navigator.Blanked switch
    {
        BlankColour.Black => "black",
        BlankColour.White => "white",
        _ => new Frame.Slide(Navigator.Index).ToString()!,
    };
}
