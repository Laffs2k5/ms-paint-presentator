using Presenter.Core;

namespace Presenter.Core.Tests;

public class SlideStoreTests
{
    private static readonly byte[] Png = [1, 2, 3];

    [Fact]
    public async Task AWaiterBeforeTheSlideIsReleasedWhenItArrives()
    {
        var s = new SlideStore();
        var wait = s.WaitReadyAsync(3, CancellationToken.None);
        Assert.False(wait.IsCompleted);
        s.Set(3, Png);
        await wait.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Same(Png, s.Get(3));
    }

    [Fact]
    public async Task AWaiterAfterTheSlideReturnsAtOnce()
    {
        var s = new SlideStore();
        s.Set(1, Png);
        await s.WaitReadyAsync(1, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void ReadyCountStopsAtTheFirstGap()
    {
        var s = new SlideStore();
        s.SetCount(5);
        s.Set(1, Png);
        s.Set(2, Png);
        s.Set(4, Png);
        Assert.Equal(2, s.ReadyCount);
        s.Set(3, Png);
        Assert.Equal(4, s.ReadyCount);
        s.Set(0, Png);  // the painting does not count as a slide
        Assert.Equal(4, s.ReadyCount);
    }

    [Fact]
    public async Task AFailureReachesWaitersAndTheToken()
    {
        var s = new SlideStore();
        var before = s.WaitReadyAsync(2, CancellationToken.None);
        s.Fail(new InvalidOperationException("renderer gone"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => before);
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.WaitReadyAsync(5, CancellationToken.None));
        Assert.True(s.FailedToken.IsCancellationRequested);
        Assert.Equal("renderer gone", s.Failure!.Message);
    }

    [Fact]
    public async Task ManyThreadsSettingAndWaiting()
    {
        var s = new SlideStore();
        s.SetCount(200);
        var waits = Enumerable.Range(1, 200).Select(i => s.WaitReadyAsync(i, CancellationToken.None)).ToList();
        await Task.WhenAll(Enumerable.Range(1, 200).Select(i => Task.Run(() => s.Set(i, Png))));
        await Task.WhenAll(waits).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(200, s.ReadyCount);
    }

    [Fact]
    public async Task AWaitCanBeCancelled()
    {
        var s = new SlideStore();
        using var cts = new CancellationTokenSource();
        var wait = s.WaitReadyAsync(1, cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
    }

    [Fact]
    public void GettingASlideNotReadyThrows() =>
        Assert.Throws<InvalidOperationException>(() => new SlideStore().Get(1));
}
