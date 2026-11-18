using System.Diagnostics;
using System.Threading.Channels;
using Presenter.Core;

namespace Presenter.Core.Tests;

public class ShowControllerTests
{
    private sealed class FakeStage(TimeSpan pasteTime) : IStage
    {
        public List<Frame> Shown { get; } = [];
        public TaskCompletionSource FirstStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<long?> ShowAsync(Frame frame, CancellationToken ct)
        {
            lock (Shown) Shown.Add(frame);
            FirstStarted.TrySetResult();
            await Task.Delay(pasteTime, ct);
            return Stopwatch.GetTimestamp();
        }
    }

    private sealed class FakeSlides(int count, int ready) : ISlideSet
    {
        private readonly TaskCompletionSource _all = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Ready { get; private set; } = ready;
        public int Count => count;
        public bool IsReady(int index) => index <= Ready;
        public async Task WaitReadyAsync(int index, CancellationToken ct) { if (!IsReady(index)) await _all.Task.WaitAsync(ct); }
        public void FinishAll() { Ready = count; _all.TrySetResult(); }
    }

    private static KeyPress K(int vk) => KeyMap.Press(vk, Stopwatch.GetTimestamp())!.Value;

    [Fact]
    public async Task KeysDuringAPasteAreCombined()
    {
        var keys = Channel.CreateUnbounded<KeyPress>();
        var stage = new FakeStage(TimeSpan.FromMilliseconds(200));
        var c = new ShowController(stage, new FakeSlides(9, 9), keys.Reader, startAtPainting: false);
        var run = c.RunAsync(CancellationToken.None);
        keys.Writer.TryWrite(K(0x27));  // Right: slide 2, takes 200 ms
        await stage.FirstStarted.Task;
        for (int i = 0; i < 4; i++) keys.Writer.TryWrite(K(0x27));  // four more while it pastes
        await Task.Delay(400);
        keys.Writer.TryWrite(K(0x1B));
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal([new Frame.Slide(2), new Frame.Slide(6)], stage.Shown);
        Assert.Equal(4, c.Changes[1].Keys);
        Assert.All(c.Changes, ch => Assert.NotNull(ch.Milliseconds));
    }

    [Fact]
    public async Task WaitsForASlideThatIsNotRenderedYet()
    {
        var keys = Channel.CreateUnbounded<KeyPress>();
        var stage = new FakeStage(TimeSpan.Zero);
        var slides = new FakeSlides(5, 1);
        var c = new ShowController(stage, slides, keys.Reader);
        var run = c.RunAsync(CancellationToken.None);
        keys.Writer.TryWrite(K(0x23));  // End, as the first key: slide 1 (ready)
        await Task.Delay(100);
        keys.Writer.TryWrite(K(0x23));  // End: slide 5, not ready
        await Task.Delay(100);
        Assert.Single(stage.Shown);
        slides.FinishAll();
        await Task.Delay(100);
        keys.Writer.TryWrite(K(0x1B));
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal([new Frame.Slide(1), new Frame.Slide(5)], stage.Shown);
    }

    [Fact]
    public async Task CancellingThrowsAndARunCarriesOn()
    {
        var keys = Channel.CreateUnbounded<KeyPress>();
        var stage = new FakeStage(TimeSpan.Zero);
        var c = new ShowController(stage, new FakeSlides(9, 9), keys.Reader, startAtPainting: false);
        using var cts = new CancellationTokenSource();
        var run = c.RunAsync(cts.Token);
        keys.Writer.TryWrite(K(0x27));
        await Task.Delay(100);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Equal(new Frame.Slide(2), c.Current);
        var again = c.RunAsync(CancellationToken.None);  // after a display change
        keys.Writer.TryWrite(K(0x27));
        await Task.Delay(100);
        keys.Writer.TryWrite(K(0x1B));
        await again.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal([new Frame.Slide(2), new Frame.Slide(3)], stage.Shown);
    }

    [Fact]
    public async Task EscEnds()
    {
        var keys = Channel.CreateUnbounded<KeyPress>();
        var stage = new FakeStage(TimeSpan.Zero);
        var c = new ShowController(stage, new FakeSlides(3, 3), keys.Reader);
        var run = c.RunAsync(CancellationToken.None);
        keys.Writer.TryWrite(K(0x1B));
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(stage.Shown);
    }
}
