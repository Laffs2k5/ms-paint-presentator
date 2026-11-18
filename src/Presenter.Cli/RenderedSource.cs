using System.Diagnostics;
using Presenter.Core;
using Presenter.Slides;

namespace Presenter.Cli;

/// <summary>A deck made in another tool (PowerPoint, PDF, Marp, Slidev),
/// rendered by its own toolchain at the slide size, through the cache.</summary>
internal sealed class RenderedSource(DeckInfo info, RenderOptions options) : IDeckSource
{
    public string Name => Path.GetFileName(Path.TrimEndingDirectorySeparator(info.Path));
    public double Aspect => info.Aspect;
    public DeckInfo Info => info;

    public SlideStore Prepare(PixelSize size, Action<string> log, CancellationToken ct)
    {
        var slides = new SlideStore();
        if (info.SlideCount is int n) slides.SetCount(n);
        var sw = Stopwatch.StartNew();
        var render = SlideRender.Start(info, size, options, ct);
        void Ready(int i)
        {
            if (slides.IsReady(i)) return;
            try { slides.Set(i, File.ReadAllBytes(render.SlidePath(i))); }
            catch (IOException e) { log($"slide {i} could not be read: {e.Message}"); }
        }
        render.TotalChanged += total => slides.SetCount(total);
        render.SlideReady += Ready;
        if (render.Total > 0) slides.SetCount(render.Total);
        for (int i = 1; i <= render.Done; i++) Ready(i);  // ready before we listened (a cache hit)
        log($"{info.Kind} deck: rendering {size}{(render.FromCache ? " (from the cache)" : "")}");
        _ = render.Completion.ContinueWith(t =>
        {
            if (t.IsFaulted)
            {
                var e = t.Exception!.InnerException ?? t.Exception;
                log($"rendering failed: {e.Message}");
                slides.Fail(e);
            }
            else if (!t.IsCanceled)
            {
                for (int i = 1; i <= render.Total; i++) Ready(i);
                log($"{render.Total} slides ready in {sw.Elapsed.TotalSeconds:F1} s{(render.FromCache ? " (cache)" : "")}");
            }
        }, TaskScheduler.Default);
        return slides;
    }
}
