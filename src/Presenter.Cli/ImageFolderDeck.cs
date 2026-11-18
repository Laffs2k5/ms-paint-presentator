using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Presenter.Core;

namespace Presenter.Cli;

/// <summary>
/// A folder of slide images (PNG or JPEG, in name order) as a deck: what
/// `presenter render` writes, or any tool's image export. Images of another
/// size are fitted, centred on black, never stretched.
/// </summary>
internal sealed class ImageFolderDeck
{
    public IReadOnlyList<string> Files { get; }
    public double Aspect { get; }

    public ImageFolderDeck(string folder)
    {
        Files = Directory.EnumerateFiles(folder)
            .Where(f => f.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                        || f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase).ToList();
        if (Files.Count == 0) throw new ArgumentException($"no PNG or JPEG images in {folder}");
        using var first = Image.FromFile(Files[0]);
        Aspect = (double)first.Width / first.Height;
    }

    public static bool Is(string path) => Directory.Exists(path)
        && Directory.EnumerateFiles(path, "*.png").Concat(Directory.EnumerateFiles(path, "*.jpg")).Any();

    /// <summary>Slide i (1-based) as PNG bytes of exactly <paramref name="size"/>.</summary>
    public byte[] Png(int slide, PixelSize size)
    {
        string file = Files[slide - 1];
        using var img = Image.FromFile(file);
        if (img.Width == size.Width && img.Height == size.Height && file.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            return File.ReadAllBytes(file);
        using var bmp = new Bitmap(size.Width, size.Height, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Black);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            double k = Math.Min((double)size.Width / img.Width, (double)size.Height / img.Height);
            int w = (int)Math.Round(img.Width * k), h = (int)Math.Round(img.Height * k);
            g.DrawImage(img, (size.Width - w) / 2, (size.Height - h) / 2, w, h);
        }
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }
}
