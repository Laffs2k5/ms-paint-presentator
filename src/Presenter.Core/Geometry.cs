namespace Presenter.Core;

/// <summary>A size in physical pixels.</summary>
public readonly record struct PixelSize(int Width, int Height)
{
    public double Aspect => (double)Width / Height;
    public long Area => (long)Width * Height;
    public override string ToString() => $"{Width}x{Height}";

    public static PixelSize Parse(string text)
    {
        var parts = text.Split('x', 'X', '×');
        if (parts.Length != 2 || !int.TryParse(parts[0], out var w) || !int.TryParse(parts[1], out var h)
            || w <= 0 || h <= 0)
            throw new FormatException($"not a size like 1920x1080: {text}");
        return new PixelSize(w, h);
    }
}

/// <summary>A rectangle in physical screen pixels (right and bottom exclusive).</summary>
public readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
    public PixelSize Size => new(Width, Height);
    public static PixelRect FromSize(int left, int top, PixelSize size) =>
        new(left, top, left + size.Width, top + size.Height);
    public override string ToString() => $"({Left},{Top}) {Width}x{Height}";
}

/// <summary>
/// Where Paint shows a slide, from the POC: Paint maximised at 100% zoom, the
/// canvas exactly the slide's size (docs/poc-results.md, E11 and E12).
/// </summary>
public static class SlideGeometry
{
    /// <summary>The margin between Paint's viewport and the largest canvas
    /// that always fits it without scrollbars (E11).</summary>
    public const int ViewportMargin = 66;

    /// <summary>
    /// E11's rule for maximised Paint at 100%: the viewport is as wide as the
    /// monitor's work area, and 210.5 px times the display scale less high,
    /// rounded half up.
    /// </summary>
    public static PixelSize PredictedViewport(PixelSize workArea, int dpi) =>
        new(workArea.Width, workArea.Height - (int)Math.Floor(210.5 * dpi / 96 + 0.5));

    /// <summary>
    /// The largest slide of the deck's shape that fits the viewport less the
    /// margin, with viewport - slide even both ways, so Paint centres it on
    /// whole pixels (E11). The renderer crops or pads the odd pixel this
    /// leaves against the exact shape (E12).
    /// </summary>
    public static PixelSize SlideSize(PixelSize viewport, double aspect)
    {
        if (aspect <= 0 || double.IsNaN(aspect)) throw new ArgumentOutOfRangeException(nameof(aspect));
        int aw = viewport.Width - ViewportMargin, ah = viewport.Height - ViewportMargin;
        if (aw < 2 || ah < 2) throw new ArgumentException($"viewport too small: {viewport}");
        int w = Math.Min(aw, (int)Math.Floor(ah * aspect));
        int h = Math.Min(ah, (int)Math.Round(w / aspect, MidpointRounding.AwayFromZero));
        w -= (viewport.Width - w) % 2;
        h -= (viewport.Height - h) % 2;
        return new PixelSize(w, h);
    }

    /// <summary>Where a canvas of <paramref name="canvas"/> sits when Paint
    /// centres it in <paramref name="viewport"/>.</summary>
    public static PixelRect CentredIn(PixelRect viewport, PixelSize canvas) =>
        PixelRect.FromSize(viewport.Left + (viewport.Width - canvas.Width) / 2,
                           viewport.Top + (viewport.Height - canvas.Height) / 2, canvas);
}
