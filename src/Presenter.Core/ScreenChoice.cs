namespace Presenter.Core;

/// <summary>A display the speaker can present on, as the screen picker shows it.</summary>
public sealed record ScreenOption(
    string Device, string Name, string Connection, bool BuiltIn, bool Primary,
    PixelSize Resolution, int ScalePercent, PixelSize Viewport, string Identity, PixelRect Bounds = default)
{
    /// <summary>The slide size a deck of this shape gets on this display.</summary>
    public PixelSize SlideFor(double aspect) => SlideGeometry.SlideSize(Viewport, aspect);

    public override string ToString() =>
        $"{Name} ({Connection}{(BuiltIn ? ", this laptop" : "")}): {Resolution} at {ScalePercent}%";
}

/// <summary>
/// Which display the picker offers first (research 05 §5, the user's rule):
/// the one the speaker chose last time, by monitor identity, if it is
/// connected; else the first that is not the laptop's own panel (the
/// projector); else the primary.
/// </summary>
public static class ScreenChoice
{
    public static int DefaultIndex(IReadOnlyList<ScreenOption> options, string? rememberedIdentity)
    {
        if (options.Count == 0) return -1;
        if (rememberedIdentity is not null)
            for (int i = 0; i < options.Count; i++)
                if (options[i].Identity == rememberedIdentity) return i;
        for (int i = 0; i < options.Count; i++)
            if (!options[i].BuiltIn) return i;
        for (int i = 0; i < options.Count; i++)
            if (options[i].Primary) return i;
        return 0;
    }

    /// <summary>
    /// After a display change and the speaker's choice: how much work the
    /// show needs (research 05 §5).
    /// </summary>
    public static ResumeWork WorkAfterChange(ScreenOption before, ScreenOption after, PixelSize slideBefore, double aspect)
    {
        if (after.SlideFor(aspect) != slideBefore) return ResumeWork.NewSlideSize;
        return before.Device == after.Device && before.Identity == after.Identity && before.Viewport == after.Viewport
            ? ResumeWork.Repaste
            : ResumeWork.MovePaint;
    }
}

public enum ResumeWork
{
    /// <summary>Same display, same size: put Paint back as it was and paste the current slide again.</summary>
    Repaste,
    /// <summary>Another display with the same slide size: move Paint there, then paste.</summary>
    MovePaint,
    /// <summary>A new slide size: Paint at the new size and the slides rendered again.</summary>
    NewSlideSize,
}
