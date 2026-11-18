using Windows.Data.Pdf;
using Windows.Storage;

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
}
