using System.Collections.Concurrent;

namespace Presenter.Core;

/// <summary>
/// A deck's slides (PNG bytes) as they become ready, from whatever renders
/// them, for the show and the loading painting. Index 0 is the loading
/// painting; 1..<see cref="Count"/> the deck. The count can arrive after the
/// store is made (a renderer may only know it later), and slides may arrive
/// on any thread, in any order.
/// </summary>
public sealed class SlideStore : ISlideSet
{
    private readonly ConcurrentDictionary<int, byte[]> _png = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource> _waiting = new();
    private readonly CancellationTokenSource _failed = new();
    private volatile int _count;
    private volatile Exception? _failure;

    /// <summary>The deck's number of slides, 0 until known.</summary>
    public int Count => _count;

    public void SetCount(int count) => _count = count;

    /// <summary>Why rendering failed, if it did.</summary>
    public Exception? Failure => _failure;

    /// <summary>Fires when rendering fails (the loading painting stops on it).</summary>
    public CancellationToken FailedToken => _failed.Token;

    /// <summary>Rendering failed: waiters, now and later, get the error.</summary>
    public void Fail(Exception e)
    {
        _failure = e;
        foreach (var w in _waiting.Values) w.TrySetException(e);
        _failed.Cancel();
    }

    /// <summary>How many slides from 1 on are ready without a gap: what the
    /// loading painting's progress bar shows.</summary>
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
        // set or failed between the checks and the registration
        if (_png.ContainsKey(index)) tcs.TrySetResult();
        else if (_failure is { } f2) tcs.TrySetException(f2);
        return tcs.Task.WaitAsync(ct);
    }

    public byte[] Get(int index) =>
        _png.TryGetValue(index, out var png) ? png : throw new InvalidOperationException($"slide {index} is not ready");

    public void Set(int index, byte[] png)
    {
        _png[index] = png;
        if (_waiting.TryGetValue(index, out var tcs)) tcs.TrySetResult();
    }
}
