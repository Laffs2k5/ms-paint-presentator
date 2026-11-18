using System.Collections.Concurrent;
using Presenter.Core;

namespace Presenter.Slides;

/// <summary>Where and with what a deck is rendered.</summary>
/// <param name="CacheDir">The cache folder: a folder per rendered deck and
/// size goes in it, and the renderers' throwaway work (Edge's profile, Node's
/// temp files) happens inside that folder.</param>
/// <param name="NodeToolsDir">Where the presenter's own Node tools live, used
/// when a deck has no node_modules of its own (default
/// %LOCALAPPDATA%\PaintPresentator\node-tools).</param>
/// <param name="EdgePath">msedge.exe (default: the installed Edge).</param>
public sealed record RenderOptions(string CacheDir, string? NodeToolsDir = null, string? EdgePath = null)
{
    /// <summary>Complete cache entries kept; the least recently used beyond this are removed.</summary>
    public int KeepEntries { get; init; } = 20;

    /// <summary>How long a Node renderer (Marp, Slidev) may run before it is stopped.</summary>
    public TimeSpan ToolTimeout { get; init; } = TimeSpan.FromMinutes(5);

    internal string ToolsDir => NodeToolsDir ?? NodeTools.DefaultToolsDir;

    /// <summary>Where the presenter's own Node tools go when none is given
    /// (what `presenter setup` installs into).</summary>
    public static string DefaultNodeToolsDir => NodeTools.DefaultToolsDir;
}

/// <summary>A render failed; the message says why and, for a missing tool, how to install it.</summary>
public sealed class SlideRenderException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Renders a deck's slides to PNG files of exactly one pixel size, in the
/// background, through the cache. Slides finish in order: slides 1 to
/// <see cref="Done"/> are on disk at <see cref="SlidePath"/>.
/// </summary>
/// <remarks>
/// <para><b>Total:</b> PDF and .pptx know it at <see cref="Start"/>; a .ppt
/// once PowerPoint has opened it. Marp and Slidev start with a count parsed
/// from the Markdown, which the render corrects to the number of images it
/// made (raised as soon as more appear; lowered at the end if fewer came).
/// <see cref="TotalChanged"/> tells each change.</para>
/// <para><b>Events</b> are raised on worker threads. A cache hit is complete
/// when <see cref="Start"/> returns and raises none. Subscribe first, then
/// read <see cref="Done"/>, so no slide is missed.</para>
/// </remarks>
public sealed class SlideRender : IAsyncDisposable
{
    private readonly CancellationTokenSource _cts;
    private readonly ConcurrentQueue<string> _notes = new();
    private readonly List<(int Slide, TaskCompletionSource Tcs)> _waiters = [];
    private readonly object _gate = new();
    private readonly SemaphoreSlim _writeSlots = new(Math.Clamp(Environment.ProcessorCount / 2, 1, 4));
    private int _total, _done, _queued;
    private Task _written = Task.CompletedTask;
    private JobObject? _job;

    /// <summary>The deck being rendered.</summary>
    public DeckInfo Deck { get; }

    /// <summary>The exact pixel size of every slide image.</summary>
    public PixelSize Size { get; }

    /// <summary>The cache entry's folder, which holds the slide images.</summary>
    public string Folder { get; }

    /// <summary>Number of slides; 0 until known (see the remarks).</summary>
    public int Total => Volatile.Read(ref _total);

    /// <summary>Slides 1..Done are ready on disk, in order.</summary>
    public int Done => Volatile.Read(ref _done);

    /// <summary>True when every slide came from the cache.</summary>
    public bool FromCache { get; }

    /// <summary>Completes when every slide is done; faults with a
    /// <see cref="SlideRenderException"/> on failure; cancelled on cancellation.</summary>
    public Task Completion { get; }

    /// <summary>A slide is ready (1-based), on any thread.</summary>
    public event Action<int>? SlideReady;

    /// <summary>The total changed (it became known or was corrected), on any thread.</summary>
    public event Action<int>? TotalChanged;

    /// <summary>What the renderer reported along the way (tool versions, timings): for diagnostics.</summary>
    internal IReadOnlyCollection<string> Notes => _notes;

    /// <summary>The Node renderers' job object (internal: tests check it is empty).</summary>
    internal JobObject? Job => _job;

    private SlideRender(DeckInfo deck, PixelSize size, string folder, CancellationToken ct, bool fromCache, int total,
        Func<SlideRender, CancellationToken, Task>? run, Exception? startError = null)
    {
        Deck = deck;
        Size = size;
        Folder = folder;
        _total = Math.Max(0, total);
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (startError is not null)
        {
            Completion = Task.FromException(startError);
        }
        else if (fromCache)
        {
            FromCache = true;
            _done = total;
            Completion = Task.CompletedTask;
        }
        else
        {
            // not Task.Run(..., token): the body must always run, to release the
            // entry's lock; it observes the token itself (and ends cancelled)
            var token = _cts.Token;
            Completion = Task.Run(() => run!(this, token), CancellationToken.None);
        }
        Completion.ContinueWith(t => FailWaiters(t), TaskScheduler.Default);
    }

    /// <summary>
    /// Starts rendering <paramref name="deck"/> at <paramref name="size"/>, or
    /// finds it in the cache. Returns at once; the work runs in the background.
    /// Problems (a missing tool, a broken deck) fault <see cref="Completion"/>
    /// rather than throw here.
    /// </summary>
    public static SlideRender Start(DeckInfo deck, PixelSize size, RenderOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(deck);
        ArgumentNullException.ThrowIfNull(options);
        if (size.Width < 1 || size.Height < 1) throw new ArgumentOutOfRangeException(nameof(size), size, "empty size");

        RenderPlan plan;
        string key;
        try
        {
            plan = RenderPlan.For(deck, options);
            key = SlideCache.Key(deck.Kind, plan.Renderer, size, plan.Files);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                       or NotSupportedException or InvalidDataException)
        {
            return new SlideRender(deck, size, options.CacheDir, ct, false, 0, null,
                new SlideRenderException($"Cannot render {System.IO.Path.GetFileName(deck.Path)}: {ex.Message}", ex));
        }

        var entry = System.IO.Path.Combine(options.CacheDir, key);
        if (SlideCache.TryHit(entry) is { } cached)
            return new SlideRender(deck, size, entry, ct, true, cached, null);

        // render into the entry, or (if another render holds it) into a sibling
        EntryLock? lck = null;
        for (int i = 0; i < 10 && lck is null; i++)
        {
            var dir = i == 0 ? entry : $"{entry}~{i}";
            lck = EntryLock.TryTake(dir);
            if (lck is not null) entry = dir;
        }
        if (lck is null)
            return new SlideRender(deck, size, entry, ct, false, 0, null,
                new SlideRenderException("The cache entry for this deck is busy in other renders."));

        // Total as early as possible: the deck's own count, else the renderer's estimate
        var initialTotal = deck.SlideCount ?? plan.EstimatedTotal ?? 0;
        return new SlideRender(deck, size, entry, ct, false, initialTotal, (r, token) => r.RunAsync(plan, options, lck, token));
    }

    /// <summary>The image of a slide (1-based): a PNG of exactly <see cref="Size"/>
    /// once the slide is ready (slide &lt;= <see cref="Done"/>).</summary>
    public string SlidePath(int slide)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(slide, 1);
        return SlideCache.SlideFile(Folder, slide);
    }

    /// <summary>Completes when slide <paramref name="slide"/> is ready; faults
    /// or is cancelled if the render ends before that.</summary>
    public Task WaitForSlideAsync(int slide, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(slide, 1);
        TaskCompletionSource tcs;
        lock (_gate)
        {
            if (Done >= slide) return Task.CompletedTask;
            if (Completion.IsCompleted)
                return Completion.IsCompletedSuccessfully
                    ? Task.FromException(new SlideRenderException($"The deck has only {Done} slides."))
                    : Completion;
            tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add((slide, tcs));
        }
        return ct.CanBeCanceled ? tcs.Task.WaitAsync(ct) : tcs.Task;
    }

    /// <summary>Cancels a render still running, waits for it to stop (its
    /// processes ended), and keeps the slides made so far until the cache
    /// removes them.</summary>
    public async ValueTask DisposeAsync()
    {
        if (!Completion.IsCompleted) await _cts.CancelAsync();
        try
        {
            await Completion.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // the outcome is Completion's to report
        }
        _job?.Dispose();
        _cts.Dispose();
        _writeSlots.Dispose();
    }

    // ---- the renderers' side ------------------------------------------------------------------

    /// <summary>A fresh job object for the next tool run, ending the last one's.
    /// A fresh one each time: a job still ending its processes refuses new
    /// ones (access denied).</summary>
    internal JobObject NewToolJob()
    {
        if (_job is { } old)
        {
            old.Terminate();
            old.Dispose();
        }
        return _job = new JobObject();
    }

    internal void Note(FormattableString text) => _notes.Enqueue(text.ToString(System.Globalization.CultureInfo.InvariantCulture));

    internal void SetTotal(int total)
    {
        if (total < 1) return;
        lock (_gate)
        {
            total = Math.Max(total, Done);
            if (total == _total) return;
            Volatile.Write(ref _total, total);
        }
        Raise(TotalChanged, total);
    }

    /// <summary>Slides handed to the writers so far (the renderer's side of <see cref="Done"/>).</summary>
    internal int Queued => _queued;

    /// <summary>Fits a rendered file to the size as the next slide.</summary>
    internal Task QueueFileAsync(string rendered, CancellationToken ct) =>
        QueueAsync((target, token) => SlideImage.FitFileAsync(rendered, target, Size, moveIfExact: true, token), ct);

    /// <summary>Writes an image as the next slide.</summary>
    internal Task QueueAsync(SlideImage.Bitmap bitmap, CancellationToken ct) =>
        QueueAsync((target, token) => SlideImage.WritePngAsync(SlideImage.Fit(bitmap, Size), target, token), ct);

    /// <summary>
    /// Writes the next slide in the background. Encoding a PNG costs 50 to
    /// 150 ms, so a few slides are written at once; this waits while that many
    /// are in flight (which bounds the memory held), and slides become done
    /// strictly in order. Called from one renderer thread at a time.
    /// </summary>
    private async Task QueueAsync(Func<string, CancellationToken, Task> write, CancellationToken ct)
    {
        if (_written.IsFaulted) await _written.ConfigureAwait(false); // report a failed write now
        await _writeSlots.WaitAsync(ct).ConfigureAwait(false);
        int n = ++_queued;
        var task = Task.Run(async () =>
        {
            try
            {
                await write(SlidePath(n), ct).ConfigureAwait(false);
            }
            finally
            {
                _writeSlots.Release();
            }
        }, CancellationToken.None);
        _written = InOrder(_written, task, n);
    }

    private async Task InOrder(Task previous, Task write, int n)
    {
        await previous.ConfigureAwait(false);
        await write.ConfigureAwait(false);
        MarkDone(n);
    }

    private void MarkDone(int n)
    {
        bool grew;
        List<TaskCompletionSource> ready = [];
        lock (_gate)
        {
            grew = n > _total;
            if (grew) Volatile.Write(ref _total, n);
            Volatile.Write(ref _done, n);
            _waiters.RemoveAll(w =>
            {
                if (w.Slide > n) return false;
                ready.Add(w.Tcs);
                return true;
            });
        }
        if (grew) Raise(TotalChanged, n);
        foreach (var t in ready) t.TrySetResult();
        Raise(SlideReady, n);
    }

    private void Raise(Action<int>? handler, int value)
    {
        if (handler is null) return;
        foreach (var h in handler.GetInvocationList().Cast<Action<int>>())
        {
            try
            {
                h(value);
            }
            catch (Exception ex)
            {
                Note($"an event handler threw: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private void FailWaiters(Task completion)
    {
        List<TaskCompletionSource> left;
        lock (_gate)
        {
            left = _waiters.Select(w => w.Tcs).ToList();
            _waiters.Clear();
        }
        foreach (var t in left)
        {
            if (completion.IsCanceled) t.TrySetCanceled();
            else if (completion.Exception is { } ex) t.TrySetException(ex.InnerExceptions);
            else t.TrySetException(new SlideRenderException($"The deck has only {Done} slides."));
        }
    }

    private async Task RunAsync(RenderPlan plan, RenderOptions options, EntryLock lck, CancellationToken ct)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        var work = System.IO.Path.Combine(Folder, "work");
        try
        {
            // a fresh start: whatever an earlier, unfinished render left goes
            foreach (var f in Directory.EnumerateFiles(Folder))
                if (!System.IO.Path.GetFileName(f).Equals(EntryLock.FileName, StringComparison.OrdinalIgnoreCase))
                    File.Delete(f);
            if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
            Directory.CreateDirectory(work);

            await plan.Run(this, work, ct).ConfigureAwait(false);
            await _written.ConfigureAwait(false);

            if (Done == 0) throw new InvalidOperationException("the renderer made no slides");
            lock (_gate)
            {
                if (_total != Done) Volatile.Write(ref _total, Done);
            }
            Raise(TotalChanged, Done);
            SlideCache.MarkComplete(Folder, new SlideCache.MarkerData(Done, Deck.Kind.ToString(), Size.ToString(), plan.Renderer));
            Note($"rendered {Done} slides in {started.Elapsed.TotalSeconds:0.00} s ({plan.Renderer})");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not SlideRenderException)
        {
            throw new SlideRenderException(
                $"Rendering {System.IO.Path.GetFileName(Deck.Path)} ({Deck.Kind}) failed: {ex.Message}", ex);
        }
        finally
        {
            if (_job is not null)
            {
                _job.Terminate(); // nothing of ours outlives the render
                _job.Dispose();
                _job = null;
            }
            try
            {
                await _written.ConfigureAwait(false); // writes still in flight read from the work folder
            }
            catch (Exception)
            {
                // reported above, or the render was cancelled
            }
            DeleteWork(work);
            lck.Dispose();
            try
            {
                SlideCache.Evict(options.CacheDir, options.KeepEntries, current: Folder);
            }
            catch (IOException)
            {
            }
        }
    }

    private static void DeleteWork(string work)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(200); // Edge may take a moment to let go of its profile
            }
        }
    }
}
