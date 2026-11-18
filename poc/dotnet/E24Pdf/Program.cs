// E24: render PDF pages at an exact pixel width with Windows' own PDF
// renderer (Windows.Data.Pdf): the presenter's adapter for decks from any
// tool that exports PDF. Nothing to install: it is part of Windows.
//
//   E24Pdf.exe <deck.pdf> <width> <outdir>
//
// The destination size is in DIPs, which Windows turns into pixels by a
// display-dependent factor (1.8 on the test laptop, whatever the process's DPI
// awareness). So the factor is measured first, by rendering page 1 at <width>
// DIPs and reading the pixels that come out. Then each page is requested at
// <width> / factor DIPs, which lands on <width> pixels to within a pixel of
// rounding (the presenter crops or pads that, as for Marp and Slidev, E12).
// Pages are written as <outdir>/page-001.png ... Prints JSON with the factor,
// each page's pixel size and the time per page.
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Windows.Data.Pdf;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
if (args.Length != 3)
{
    Console.Error.WriteLine("usage: E24Pdf <deck.pdf> <width> <outdir>");
    return 2;
}
var pdfPath = Path.GetFullPath(args[0]);
uint width = uint.Parse(args[1]);
var outDir = Directory.CreateDirectory(Path.GetFullPath(args[2])).FullName;

var sw = Stopwatch.StartNew();
var file = await StorageFile.GetFileFromPathAsync(pdfPath);
var doc = await PdfDocument.LoadFromFileAsync(file);
double loadMs = sw.Elapsed.TotalMilliseconds;

async Task<(InMemoryRandomAccessStream Png, uint W, uint H)> Render(PdfPage page, double dipWidth)
{
    var size = page.Size;
    var stream = new InMemoryRandomAccessStream();
    await page.RenderToStreamAsync(stream, new PdfPageRenderOptions
    {
        DestinationWidth = (uint)Math.Round(dipWidth),
        DestinationHeight = (uint)Math.Round(dipWidth * size.Height / size.Width),
        BitmapEncoderId = BitmapEncoder.PngEncoderId,
    });
    stream.Seek(0);
    var decoder = await BitmapDecoder.CreateAsync(stream);
    stream.Seek(0);
    return (stream, decoder.PixelWidth, decoder.PixelHeight);
}

// measure DIPs-to-pixels once
sw.Restart();
double factor;
using (var first = doc.GetPage(0))
{
    var probe = await Render(first, width);
    factor = probe.W / (double)width;
    probe.Png.Dispose();
}
double calibrateMs = sw.Elapsed.TotalMilliseconds;

var pages = new List<object>();
for (uint i = 0; i < doc.PageCount; i++)
{
    sw.Restart();
    using var page = doc.GetPage(i);
    var (png, w, h) = await Render(page, width / factor);
    using (png)
    using (var fs = File.Create(Path.Combine(outDir, $"page-{i + 1:000}.png")))
        await png.AsStreamForRead().CopyToAsync(fs);
    pages.Add(new { page = i + 1, dips = new[] { page.Size.Width, page.Size.Height }, pixels = new[] { w, h },
                    ms = Math.Round(sw.Elapsed.TotalMilliseconds, 1) });
}
Console.WriteLine(JsonSerializer.Serialize(new
{
    pdf = Path.GetFileName(pdfPath), pages = doc.PageCount, load_ms = Math.Round(loadMs, 1),
    dips_to_pixels = Math.Round(factor, 4), calibrate_ms = Math.Round(calibrateMs, 1), page_results = pages,
}));
return 0;
