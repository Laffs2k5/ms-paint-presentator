namespace Presenter.Core;

public enum BlankColour { Black, White }

/// <summary>What the canvas should show: a slide (0 is the loading painting,
/// 1..n the deck) or a blank screen.</summary>
public abstract record Frame
{
    public sealed record Slide(int Index) : Frame
    {
        public override string ToString() => Index == 0 ? "painting" : $"slide {Index}";
    }

    public sealed record Blank(BlankColour Colour) : Frame
    {
        public override string ToString() => Colour == BlankColour.Black ? "black" : "white";
    }
}

/// <summary>The outcome of a batch of keys.</summary>
public readonly record struct NavResult(bool End, Frame? Show);

/// <summary>
/// The show's navigation, as the POC's E13 settled it. Index 0 is the loading
/// painting, 1..<c>slides</c> the deck. Every key in a batch moves the target;
/// only the final target is shown (keys pressed during a paste are combined).
/// The very first key always goes to slide 1, whatever it is (the loading
/// painting's concept). Digits then Enter jump to that slide (0 is the
/// painting). B and W blank the screen; any key brings the slide back.
/// </summary>
public sealed class Navigator
{
    private string _digits = "";
    private bool _first = true;

    public int Index { get; private set; }
    public BlankColour? Blanked { get; private set; }

    /// <param name="startAtPainting">false when the show starts on a slide (no painting shown).</param>
    public Navigator(bool startAtPainting = true)
    {
        _first = startAtPainting;
        Index = startAtPainting ? 0 : 1;
    }

    /// <summary>Apply a batch of keys. <paramref name="slides"/> is the deck's
    /// number of slides (the painting not counted).</summary>
    public NavResult Apply(IReadOnlyList<KeyPress> batch, int slides)
    {
        if (batch.Count == 0) return new NavResult(false, null);
        int last = Math.Max(slides, 0), target = Index;
        bool back = false;
        BlankColour? blank = null;
        foreach (var key in batch)
        {
            if (key.Action == ShowAction.End) return new NavResult(true, null);
            if (Blanked is not null) { back = true; continue; }  // any key: the slide again
            switch (key.Action)
            {
                case ShowAction.Digit:
                    _digits += (char)('0' + key.Digit);
                    continue;
                case ShowAction.Next when key.IsEnter && _digits.Length > 0:
                    target = Math.Min(int.Parse(_digits), last);
                    break;
                case ShowAction.Next: target = Math.Min(target + 1, last); break;
                case ShowAction.Prev: target = Math.Max(target - 1, 0); break;
                case ShowAction.First: target = 0; break;
                case ShowAction.Last: target = last; break;
                case ShowAction.Black: blank = BlankColour.Black; break;
                case ShowAction.White: blank = BlankColour.White; break;
            }
            _digits = "";
        }
        if (_first)
        {
            _first = false;
            _digits = "";
            if (last >= 1) { blank = null; back = false; target = 1; }
        }
        if (blank is { } colour)
        {
            Blanked = colour;
            return new NavResult(false, new Frame.Blank(colour));
        }
        if (back || target != Index)
        {
            Blanked = null;
            Index = target;
            return new NavResult(false, new Frame.Slide(target));
        }
        return new NavResult(false, null);
    }
}
