using Presenter.Core;

namespace Presenter.Cli;

/// <summary>A deck the show can present: its shape, and its slides at a size.</summary>
internal interface IDeckSource
{
    string Name { get; }

    /// <summary>What the loading painting writes.</summary>
    string Title { get; }

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
    public string Title => Name;
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

/// <summary>Demo mode: the built-in help deck, drawn by the presenter itself
/// at the size the screen needs (nothing to install).</summary>
internal sealed class HelpDeckSource : IDeckSource
{
    public string Name => "help";
    public string Title => Presenter.Ui.HelpDeck.Title;
    public double Aspect => 16.0 / 9;

    public SlideStore Prepare(PixelSize size, Action<string> log, CancellationToken ct)
    {
        var slides = new SlideStore();
        int n = Presenter.Ui.HelpDeck.Slides.Count;
        slides.SetCount(n);
        _ = Task.Run(() =>
        {
            try
            {
                for (int i = 1; i <= n && !ct.IsCancellationRequested; i++) slides.Set(i, Presenter.Ui.HelpDeck.Render(i, size));
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                slides.Fail(e);
            }
        }, ct);
        return slides;
    }
}
