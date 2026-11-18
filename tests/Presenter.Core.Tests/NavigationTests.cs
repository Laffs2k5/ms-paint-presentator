using Presenter.Core;

namespace Presenter.Core.Tests;

public class NavigationTests
{
    private static KeyPress K(int vk) => KeyMap.Press(vk, 0) ?? throw new ArgumentException($"not a show key: {vk:X}");
    private const int Right = 0x27, Left = 0x25, Space = 0x20, Enter = 0x0D, Home = 0x24, End = 0x23,
                      Esc = 0x1B, B = 0x42, W = 0x57;
    private static int D(int d) => 0x30 + d;

    private static Frame? Step(Navigator nav, int slides, params int[] vks) =>
        nav.Apply(vks.Select(K).ToList(), slides).Show;

    [Fact]
    public void FirstKeyAlwaysGoesToSlideOne()
    {
        foreach (var vk in new[] { Right, Left, Home, End, B, W, D(5), Space })
        {
            var nav = new Navigator();
            Assert.Equal(new Frame.Slide(1), Step(nav, 7, vk));
            Assert.Equal(1, nav.Index);
            Assert.Null(nav.Blanked);
        }
    }

    [Fact]
    public void EscEndsEvenAsTheFirstKey()
    {
        Assert.True(new Navigator().Apply([K(Esc)], 7).End);
    }

    [Fact]
    public void NextAndPrevStayInRange()
    {
        var nav = new Navigator(startAtPainting: false);
        Assert.Equal(new Frame.Slide(2), Step(nav, 3, Right));
        Assert.Equal(new Frame.Slide(3), Step(nav, 3, Space));
        Assert.Null(Step(nav, 3, Right));  // already the last
        Assert.Equal(new Frame.Slide(2), Step(nav, 3, Left));
        Assert.Equal(new Frame.Slide(1), Step(nav, 3, Left));
        Assert.Equal(new Frame.Slide(0), Step(nav, 3, Left));  // back to the painting
        Assert.Null(Step(nav, 3, Left));
    }

    [Fact]
    public void HomeIsThePaintingAndEndTheLastSlide()
    {
        var nav = new Navigator(startAtPainting: false);
        Assert.Equal(new Frame.Slide(7), Step(nav, 7, End));
        Assert.Equal(new Frame.Slide(0), Step(nav, 7, Home));
    }

    [Fact]
    public void DigitsThenEnterJump()
    {
        var nav = new Navigator(startAtPainting: false);
        Assert.Null(Step(nav, 12, D(1)));
        Assert.Equal(new Frame.Slide(11), Step(nav, 12, D(1), Enter));
        Assert.Equal(new Frame.Slide(0), Step(nav, 12, D(0), Enter));  // 0 + Enter: the painting
        Assert.Equal(new Frame.Slide(12), Step(nav, 12, D(9), D(9), Enter));  // clamped
        Assert.Equal(new Frame.Slide(1), Step(nav, 12, Home, Enter));  // Enter alone is Next
    }

    [Fact]
    public void ANavigationKeyClearsPendingDigits()
    {
        var nav = new Navigator(startAtPainting: false);
        Step(nav, 9, D(5));
        Assert.Equal(new Frame.Slide(2), Step(nav, 9, Right));
        Assert.Equal(new Frame.Slide(3), Step(nav, 9, Enter));  // plain Next, not slide 5
    }

    [Fact]
    public void BlankAndBack()
    {
        var nav = new Navigator(startAtPainting: false);
        Assert.Equal(new Frame.Blank(BlankColour.Black), Step(nav, 5, B));
        Assert.Equal(BlankColour.Black, nav.Blanked);
        Assert.Equal(new Frame.Slide(1), Step(nav, 5, B));  // B again: back
        Assert.Equal(new Frame.Blank(BlankColour.White), Step(nav, 5, W));
        Assert.Equal(new Frame.Slide(1), Step(nav, 5, Right));  // any key comes back, it does not move on
        Assert.Null(nav.Blanked);
    }

    [Fact]
    public void ABatchShowsOnlyItsFinalTarget()
    {
        var nav = new Navigator(startAtPainting: false);
        Assert.Equal(new Frame.Slide(5), Step(nav, 9, Right, Right, Right, Right));
        Assert.Equal(new Frame.Slide(4), Step(nav, 9, Right, Left, Left));
        Assert.Null(Step(nav, 9, Right, Left));  // back where it was: nothing to paste
    }

    [Fact]
    public void NumpadDigitsWork()
    {
        var nav = new Navigator(startAtPainting: false);
        Assert.Equal(new Frame.Slide(4), nav.Apply([KeyMap.Press(0x64, 0)!.Value, K(Enter)], 9).Show);
    }

    [Fact]
    public void UnknownKeysAreNotShowKeys()
    {
        Assert.Null(KeyMap.Press(0x41, 0));  // A
        Assert.Null(KeyMap.Press(0x74, 0));  // F5
    }
}
