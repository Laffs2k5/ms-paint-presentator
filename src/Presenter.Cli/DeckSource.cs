using Presenter.Core;

namespace Presenter.Cli;

/// <summary>A deck the show can present: its shape, and its slides at a size.</summary>
internal interface IDeckSource
{
    string Name { get; }
    double Aspect { get; }

    /// <summary>Start producing the slides at <paramref name="size"/> in the
    /// background; they arrive in the returned set as they become ready.</summary>
    SlideStore Prepare(PixelSize size, Action<string> log, CancellationToken ct);
}

/// <summary>A folder of slide images, fitted to the size.</summary>
internal sealed class ImageFolderSource(string folder) : IDeckSource
{
    private readonly ImageFolderDeck _deck = new(folder);

    public string Name => Path.GetFileName(Path.TrimEndingDirectorySeparator(folder));
    public double Aspect => _deck.Aspect;

    public SlideStore Prepare(PixelSize size, Action<string> log, CancellationToken ct)
    {
        var slides = new SlideStore();
        slides.SetCount(_deck.Files.Count);
        _ = Task.Run(() =>
        {
            for (int i = 1; i <= _deck.Files.Count && !ct.IsCancellationRequested; i++) slides.Set(i, _deck.Png(i, size));
        }, ct);
        return slides;
    }
}
