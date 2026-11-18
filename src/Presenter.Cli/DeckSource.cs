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
            int i = 1;
            try
            {
                for (; i <= _deck.Files.Count && !ct.IsCancellationRequested; i++) slides.Set(i, _deck.Png(i, size));
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // GDI+ reports a file that is not an image as "out of memory"
                string why = e is OutOfMemoryException ? "not an image GDI+ can read" : e.Message;
                slides.Fail(new IOException($"slide image {i} could not be read: {why}", e));
            }
        }, ct);
        return slides;
    }
}
