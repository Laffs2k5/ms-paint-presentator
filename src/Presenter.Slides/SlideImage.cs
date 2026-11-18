using Presenter.Core;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Presenter.Slides;

/// <summary>
/// Brings a rendered slide to exactly the slide size, losslessly: a renderer
/// works from the deck's design canvas at a scale, and rounding leaves a pixel
/// too many or too few (E12). The image is centred; missing rows and columns
/// are padded with the slide's own bottom-left pixel colour and extra ones are
/// cropped. It is never resampled or stretched. Uses Windows' own codecs
/// (Windows.Graphics.Imaging), so no image library ships.
/// </summary>
internal static class SlideImage
{
    /// <summary>A decoded image: BGRA, 8 bits a channel, straight alpha, rows top down.</summary>
    internal sealed record Bitmap(int Width, int Height, byte[] Bgra);

    /// <summary>The pixel size of an image file, from its header.</summary>
    public static async Task<PixelSize> SizeAsync(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var decoder = await BitmapDecoder.CreateAsync(fs.AsRandomAccessStream());
        return new PixelSize((int)decoder.PixelWidth, (int)decoder.PixelHeight);
    }

    /// <summary>
    /// Writes <paramref name="source"/> (any format Windows decodes) to
    /// <paramref name="target"/> as a PNG of exactly <paramref name="size"/>.
    /// A PNG that already has the size is moved, not re-encoded. The target
    /// appears atomically (written aside, then renamed).
    /// </summary>
    public static async Task FitFileAsync(string source, string target, PixelSize size, bool moveIfExact,
        CancellationToken ct)
    {
        Bitmap bitmap;
        using (var fs = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var decoder = await BitmapDecoder.CreateAsync(fs.AsRandomAccessStream()).AsTask(ct);
            if (decoder.PixelWidth == size.Width && decoder.PixelHeight == size.Height
                && decoder.DecoderInformation.CodecId == BitmapDecoder.PngDecoderId && moveIfExact)
            {
                fs.Dispose();
                File.Move(source, target, overwrite: true);
                return;
            }
            bitmap = await DecodeAsync(decoder, ct);
        }
        await WritePngAsync(Fit(bitmap, size), target, ct);
    }

    /// <summary>Decodes an image held in a stream (e.g. a rendered PDF page).</summary>
    public static async Task<Bitmap> DecodeAsync(IRandomAccessStream stream, CancellationToken ct)
    {
        var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(ct);
        return await DecodeAsync(decoder, ct);
    }

    private static async Task<Bitmap> DecodeAsync(BitmapDecoder decoder, CancellationToken ct)
    {
        var data = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight,
            new BitmapTransform(), ExifOrientationMode.IgnoreExifOrientation,
            ColorManagementMode.DoNotColorManage).AsTask(ct);
        return new Bitmap((int)decoder.PixelWidth, (int)decoder.PixelHeight, data.DetachPixelData());
    }

    /// <summary>Encodes as PNG (opaque images without an alpha channel) and
    /// writes the file atomically.</summary>
    public static async Task WritePngAsync(Bitmap bitmap, string target, CancellationToken ct)
    {
        bool opaque = true;
        for (int i = 3; i < bitmap.Bgra.Length; i += 4)
        {
            if (bitmap.Bgra[i] != 255)
            {
                opaque = false;
                break;
            }
        }
        using var mem = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, mem).AsTask(ct);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, opaque ? BitmapAlphaMode.Ignore : BitmapAlphaMode.Straight,
            (uint)bitmap.Width, (uint)bitmap.Height, 96, 96, bitmap.Bgra);
        await encoder.FlushAsync().AsTask(ct);
        mem.Seek(0);
        var tmp = target + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            await mem.AsStreamForRead().CopyToAsync(fs, ct);
        File.Move(tmp, target, overwrite: true);
    }

    /// <summary>
    /// The image centred on a canvas of <paramref name="size"/>: padded with
    /// its bottom-left pixel's colour, cropped where larger. With a difference
    /// of one pixel the extra or missing row is the last one (as E13 did).
    /// </summary>
    public static Bitmap Fit(Bitmap src, PixelSize size)
    {
        if (src.Width == size.Width && src.Height == size.Height) return src;
        int tw = size.Width, th = size.Height;
        var dst = new byte[tw * th * 4];
        // the pad colour: the source's bottom-left pixel
        int pad = (src.Height - 1) * src.Width * 4;
        if (src.Width < tw || src.Height < th)
        {
            for (int i = 0; i < dst.Length; i += 4)
            {
                dst[i] = src.Bgra[pad];
                dst[i + 1] = src.Bgra[pad + 1];
                dst[i + 2] = src.Bgra[pad + 2];
                dst[i + 3] = src.Bgra[pad + 3];
            }
        }
        // offsets: where the source lands (dx, dy >= 0) or where it is cut (sx, sy >= 0)
        int dx = Math.Max(0, (tw - src.Width) / 2), dy = Math.Max(0, (th - src.Height) / 2);
        int sx = Math.Max(0, (src.Width - tw) / 2), sy = Math.Max(0, (src.Height - th) / 2);
        int w = Math.Min(src.Width - sx, tw - dx), h = Math.Min(src.Height - sy, th - dy);
        for (int y = 0; y < h; y++)
            System.Buffer.BlockCopy(src.Bgra, ((sy + y) * src.Width + sx) * 4, dst, ((dy + y) * tw + dx) * 4, w * 4);
        return new Bitmap(tw, th, dst);
    }

    /// <summary>The largest size of the exact shape <paramref name="aspect"/>
    /// (width / height) that fits in <paramref name="size"/>, to whole pixels.</summary>
    public static PixelSize Contain(PixelSize size, double aspect)
    {
        if (size.Aspect > aspect)
            return new PixelSize(Math.Max(1, (int)Math.Round(size.Height * aspect, MidpointRounding.AwayFromZero)),
                size.Height);
        return new PixelSize(size.Width,
            Math.Max(1, (int)Math.Round(size.Width / aspect, MidpointRounding.AwayFromZero)));
    }

    /// <summary>True when a PNG file is complete: it ends with the IEND chunk
    /// and no other process has it open for writing.</summary>
    public static bool IsCompletePng(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (fs.Length < 20) return false;
            fs.Seek(-12, SeekOrigin.End);
            Span<byte> tail = stackalloc byte[12];
            fs.ReadExactly(tail);
            return tail[4] == 'I' && tail[5] == 'E' && tail[6] == 'N' && tail[7] == 'D';
        }
        catch (IOException)
        {
            return false; // still open for writing
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
