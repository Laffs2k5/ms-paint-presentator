namespace Presenter.Slides;

/// <summary>The tool a deck was made in, which decides how it is rendered.</summary>
public enum DeckKind
{
    /// <summary>A PowerPoint file (.pptx, .ppt and their variants), rendered by PowerPoint.</summary>
    PowerPoint,
    /// <summary>A PDF (from any tool), rendered by Windows' own PDF renderer.</summary>
    Pdf,
    /// <summary>Marp Markdown (<c>marp: true</c>), rendered by marp-cli.</summary>
    Marp,
    /// <summary>A Slidev project, rendered by the project's own Slidev.</summary>
    Slidev,
}

/// <summary>A recognised deck.</summary>
/// <param name="Path">The deck's main file: the .pptx, .pdf, Marp .md or Slidev entry (slides.md).</param>
/// <param name="Kind">Its tool.</param>
/// <param name="Aspect">The slide shape, width / height.</param>
/// <param name="SlideCount">Slides the show presents (hidden PowerPoint slides
/// not counted), or null when that is costly to know before rendering.</param>
public sealed record DeckInfo(string Path, DeckKind Kind, double Aspect, int? SlideCount);

/// <summary>Recognises decks made in other tools (research 05 §6).</summary>
public static class Decks
{
    /// <summary>
    /// Recognises the deck at <paramref name="path"/> and reads its shape and,
    /// where cheap, its slide count:
    /// <list type="bullet">
    /// <item>.pptx and variants: from the file's XML, without PowerPoint;
    /// a binary .ppt opens PowerPoint briefly (hidden).</item>
    /// <item>.pdf: Windows.Data.Pdf, page 1's shape and the page count.</item>
    /// <item>.md with <c>marp: true</c>: Marp; the theme's design size.</item>
    /// <item>A Slidev project: a folder with slides.md, a slides.md (or a
    /// Markdown file with Slidev-only headmatter), or a package.json that
    /// depends on @slidev/cli; <c>canvasWidth</c> and <c>aspectRatio</c>.</item>
    /// </list>
    /// </summary>
    /// <exception cref="FileNotFoundException">Nothing at the path.</exception>
    /// <exception cref="NotSupportedException">Not a deck of a known tool.</exception>
    public static DeckInfo Inspect(string path)
    {
        var full = System.IO.Path.GetFullPath(path);
        if (!File.Exists(full) && !Directory.Exists(full)) throw new FileNotFoundException("No deck at this path.", full);
        var ext = System.IO.Path.GetExtension(full).ToLowerInvariant();

        if (File.Exists(full) && PowerPointDeck.IsOpenXml(full))
        {
            var (aspect, visible) = PowerPointDeck.InspectOpenXml(full);
            return new DeckInfo(full, DeckKind.PowerPoint, aspect, visible);
        }
        if (File.Exists(full) && PowerPointDeck.BinaryExtensions.Contains(ext))
            return InspectWithPowerPoint(full);
        if (ext == ".pdf" && File.Exists(full))
        {
            var (aspect, pages) = PdfDeck.Inspect(full);
            return new DeckInfo(full, DeckKind.Pdf, aspect, pages);
        }
        if (ext == ".md" && File.Exists(full) && MarpDeck.IsMarp(FrontMatter.SplitLines(File.ReadAllText(full))))
        {
            var marp = MarpDeck.Read(full);
            return new DeckInfo(full, DeckKind.Marp, (double)marp.Design.Width / marp.Design.Height, marp.SlideCount);
        }
        if (SlidevDeck.FindEntry(full) is { } entry)
        {
            var slidev = SlidevDeck.Read(entry);
            return new DeckInfo(slidev.Entry, DeckKind.Slidev, (double)slidev.CanvasWidth / slidev.CanvasHeight,
                slidev.SlideCount);
        }
        throw new NotSupportedException(
            $"Not a deck the presenter knows: {System.IO.Path.GetFileName(full)}. Supported: PowerPoint (.pptx, .ppt), " +
            "PDF, Marp Markdown (marp: true in the front matter) and Slidev projects (slides.md).");
    }

    private static DeckInfo InspectWithPowerPoint(string path)
    {
        var work = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PaintPresentator", "inspect-" + Guid.NewGuid().ToString("N"));
        try
        {
            return PowerPointSession.RunAsync(session =>
            {
                using var pres = session.Open(path, work);
                return new DeckInfo(path, DeckKind.PowerPoint, pres.Aspect, pres.VisibleSlides().Count);
            }, CancellationToken.None).GetAwaiter().GetResult();
        }
        finally
        {
            try
            {
                Directory.Delete(work, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
