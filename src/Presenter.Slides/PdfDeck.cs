using Presenter.Core;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Presenter.Slides;

/// <summary>
/// PDF decks through Windows' own renderer (Windows.Data.Pdf, E24): nothing
/// to install, about 50 ms a page, any page on its own.
/// </summary>
internal static class PdfDeck
{
    public static (double Aspect, int Pages) Inspect(string path) =>
        Task.Run(async () =>
        {
            var doc = await Load(path);
            using var page = doc.GetPage(0);
            return (page.Size.Width / page.Size.Height, (int)doc.PageCount);
        }).GetAwaiter().GetResult();

    public static async Task<PdfDocument> Load(string path)
    {
        var file = await StorageFile.GetFileFromPathAsync(System.IO.Path.GetFullPath(path));
        return await PdfDocument.LoadFromFileAsync(file);
    }

    /// <summary>
    /// Renders every page to the sink at the slide size. The destination
    /// size is in DIPs, which Windows turns into pixels by a display-dependent
    /// factor (1.8 on the POC laptop, E24). So page 1 is rendered once to
    /// measure the factor, and pages are then requested at width / factor
    /// DIPs, nudged by a DIP until the width comes out exact where possible.
    /// </summary>
    public static async Task RenderAsync(string path, SlideRender sink, CancellationToken ct)
    {
        var doc = await Load(path).WaitAsync(ct);
        sink.SetTotal((int)doc.PageCount);
        double? factor = null;
        var dipsFor = new Dictionary<int, uint>(); // pixel width -> the DIP width that gave it
        for (uint i = 0; i < doc.PageCount; i++)
        {
            ct.ThrowIfCancellationRequested();
            using var page = doc.GetPage(i);
            var target = SlideImage.Contain(sink.Size, page.Size.Width / page.Size.Height);
            factor ??= await MeasureFactor(page, target.Width, ct);
            SlideImage.Bitmap bitmap;
            if (dipsFor.TryGetValue(target.Width, out var known))
                bitmap = await RenderAt(page, known, ct);
            else
            {
                (bitmap, var dips) = await RenderExactWidth(page, target.Width, factor.Value, ct);
                dipsFor[target.Width] = dips;
            }
            await sink.QueueAsync(bitmap, ct);
        }
        sink.Note($"pdf: {factor:0.###} px per DIP");
    }

    private static async Task<double> MeasureFactor(PdfPage page, int width, CancellationToken ct)
    {
        var probe = await RenderAt(page, (uint)width, ct);
        return probe.Width / (double)width;
    }

    private static async Task<(SlideImage.Bitmap, uint)> RenderExactWidth(PdfPage page, int width, double factor,
        CancellationToken ct)
    {
        uint dips = (uint)Math.Max(1, Math.Round(width / factor));
        SlideImage.Bitmap? best = null;
        uint bestDips = dips;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            var bmp = await RenderAt(page, dips, ct);
            if (best is null || Math.Abs(bmp.Width - width) < Math.Abs(best.Width - width))
            {
                best = bmp;
                bestDips = dips;
            }
            if (bmp.Width == width) break;
            dips = bmp.Width < width ? dips + 1 : dips - 1;
        }
        return (best!, bestDips);
    }

    private static async Task<SlideImage.Bitmap> RenderAt(PdfPage page, uint dipWidth, CancellationToken ct)
    {
        using var stream = new InMemoryRandomAccessStream();
        // Only the width: the renderer keeps the page's shape. The default
        // encoder (BMP) is cheaper than PNG for an image decoded right away.
        await page.RenderToStreamAsync(stream, new PdfPageRenderOptions { DestinationWidth = dipWidth }).AsTask(ct);
        stream.Seek(0);
        return await SlideImage.DecodeAsync(stream, ct);
    }
}
