using System.Collections.Concurrent;
using Presenter.Core;

namespace Presenter.Cli;

/// <summary>A deck the show can present: its shape, and its slides at a size.</summary>
internal interface IDeckSource
{
    string Name { get; }
    double Aspect { get; }

    /// <summary>Start producing the slides at <paramref name="size"/> in the
    /// background; they arrive in the returned set as they become ready.</summary>
    ReadySlides Prepare(PixelSize size, Action<string> log, CancellationToken ct);
}

/// <summary>A folder of slide images, fitted to the size.</summary>
internal sealed class ImageFolderSource(string folder) : IDeckSource
{
    private readonly ImageFolderDeck _deck = new(folder);

    public string Name => Path.GetFileName(Path.TrimEndingDirectorySeparator(folder));
    public double Aspect => _deck.Aspect;

    public ReadySlides Prepare(PixelSize size, Action<string> log, CancellationToken ct)
    {
        var slides = new ReadySlides();
        slides.SetCount(_deck.Files.Count);
        _ = Task.Run(() =>
        {
            for (int i = 1; i <= _deck.Files.Count && !ct.IsCancellationRequested; i++) slides.Set(i, _deck.Png(i, size));
        }, ct);
        return slides;
    }
}

/// <summary>Slide PNGs as they become ready; index 0 is the loading painting.
/// The deck's slide count can arrive after the set is made.</summary>
internal sealed class ReadySlides : ISlideSet
{
    private readonly ConcurrentDictionary<int, byte[]> _png = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource> _waiting = new();
    private volatile int _count;
    private volatile Exception? _failure;

    public int Count => _count;

    public void SetCount(int count) => _count = count;

    /// <summary>Rendering failed: waiters get the error.</summary>
    public void Fail(Exception e)
    {
        _failure = e;
        foreach (var w in _waiting.Values) w.TrySetException(e);
    }

    public Exception? Failure => _failure;

    /// <summary>How many slides from 1 on are ready without a gap.</summary>
    public int ReadyCount
    {
        get
        {
            int n = 0;
            while (_png.ContainsKey(n + 1)) n++;
            return n;
        }
    }

    public bool IsReady(int index) => _png.ContainsKey(index);

    public Task WaitReadyAsync(int index, CancellationToken ct)
    {
        if (_png.ContainsKey(index)) return Task.CompletedTask;
        if (_failure is { } f) return Task.FromException(f);
        var tcs = _waiting.GetOrAdd(index, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        if (_png.ContainsKey(index)) tcs.TrySetResult();  // set in between
        return tcs.Task.WaitAsync(ct);
    }

    public byte[] Get(int index) => _png.TryGetValue(index, out var png) ? png : throw new InvalidOperationException($"slide {index} is not ready");

    public void Set(int index, byte[] png)
    {
        _png[index] = png;
        if (_waiting.TryGetValue(index, out var tcs)) tcs.TrySetResult();
    }
}
