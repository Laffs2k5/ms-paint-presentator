using Presenter.Core;

namespace Presenter.Slides.Tests;

public class SlideImageTests
{
    private static SlideImage.Bitmap Solid(int w, int h, byte b, byte g, byte r)
    {
        var px = new byte[w * h * 4];
        for (int i = 0; i < px.Length; i += 4)
        {
            px[i] = b;
            px[i + 1] = g;
            px[i + 2] = r;
            px[i + 3] = 255;
        }
        return new SlideImage.Bitmap(w, h, px);
    }

    private static (byte B, byte G, byte R) At(SlideImage.Bitmap bmp, int x, int y)
    {
        int i = (y * bmp.Width + x) * 4;
        return (bmp.Bgra[i], bmp.Bgra[i + 1], bmp.Bgra[i + 2]);
    }

    [Fact]
    public void One_row_short_is_padded_at_the_bottom_with_the_bottom_left_colour()
    {
        var src = Solid(4, 3, 10, 20, 30);
        src.Bgra[(2 * 4 + 0) * 4] = 99; // bottom-left pixel differs
        var fit = SlideImage.Fit(src, new PixelSize(4, 4));
        Assert.Equal((4, 4), (fit.Width, fit.Height));
        Assert.Equal((10, 20, 30), At(fit, 1, 0)); // the image starts at the top
        Assert.Equal((99, 20, 30), At(fit, 0, 3)); // padding: the bottom-left colour
        Assert.Equal((99, 20, 30), At(fit, 3, 3));
    }

    [Fact]
    public void One_row_too_many_is_cropped_at_the_bottom()
    {
        var src = Solid(3, 4, 1, 1, 1);
        for (int x = 0; x < 3; x++) src.Bgra[(3 * 3 + x) * 4] = 200; // last row marked
        var fit = SlideImage.Fit(src, new PixelSize(3, 3));
        for (int y = 0; y < 3; y++) Assert.Equal((1, 1, 1), At(fit, 1, y));
    }

    [Fact]
    public void A_narrower_slide_is_centred_never_stretched()
    {
        var src = Solid(4, 3, 5, 5, 5);
        var fit = SlideImage.Fit(src, new PixelSize(8, 3));
        Assert.Equal((5, 5, 5), At(fit, 2, 1));
        Assert.Equal((5, 5, 5), At(fit, 5, 1));
        Assert.Equal(8 * 3 * 4, fit.Bgra.Length);
    }

    [Theory]
    [InlineData(1920, 1080, 16.0 / 9, 1920, 1080)]
    [InlineData(1910, 1433, 4.0 / 3, 1910, 1433)] // 1433 * 4/3 = 1910.67: width-limited
    [InlineData(1920, 1080, 4.0 / 3, 1440, 1080)]
    [InlineData(2546, 1433, 16.0 / 9, 2546, 1432)]
    public void Contain_keeps_the_exact_shape(int w, int h, double aspect, int ew, int eh) =>
        Assert.Equal(new PixelSize(ew, eh), SlideImage.Contain(new PixelSize(w, h), aspect));

    [Fact]
    public async Task Png_round_trip_through_windows_codecs()
    {
        using var dir = new TempDir();
        var a = Path.Combine(dir.Path, "a.png");
        await SlideImage.WritePngAsync(Solid(10, 7, 0, 128, 255), a, default);
        Assert.Equal(new PixelSize(10, 7), await SlideImage.SizeAsync(a));
        Assert.True(SlideImage.IsCompletePng(a));
        var b = Path.Combine(dir.Path, "b.png");
        await SlideImage.FitFileAsync(a, b, new PixelSize(12, 6), moveIfExact: true, default);
        Assert.Equal(new PixelSize(12, 6), await SlideImage.SizeAsync(b));
        Assert.True(File.Exists(a)); // re-encoded, so the source stays
        var c = Path.Combine(dir.Path, "c.png");
        await SlideImage.FitFileAsync(b, c, new PixelSize(12, 6), moveIfExact: true, default);
        Assert.False(File.Exists(b)); // the exact size is moved, not re-encoded
    }
}

public class CacheTests
{
    [Fact]
    public void Key_depends_on_content_size_and_renderer()
    {
        using var dir = new TempDir();
        var f = dir.File("deck.md", "one");
        var k1 = SlideCache.Key(DeckKind.Marp, "marp-cli 4.5.1", new PixelSize(1920, 1080), [f]);
        Assert.Equal(k1, SlideCache.Key(DeckKind.Marp, "marp-cli 4.5.1", new PixelSize(1920, 1080), [f]));
        Assert.NotEqual(k1, SlideCache.Key(DeckKind.Marp, "marp-cli 4.5.1", new PixelSize(1920, 1081), [f]));
        Assert.NotEqual(k1, SlideCache.Key(DeckKind.Marp, "marp-cli 4.6.0", new PixelSize(1920, 1080), [f]));
        File.WriteAllText(f, "two");
        Assert.NotEqual(k1, SlideCache.Key(DeckKind.Marp, "marp-cli 4.5.1", new PixelSize(1920, 1080), [f]));
        Assert.Matches("^[0-9a-f]{32}$", k1);
    }

    [Fact]
    public void Eviction_keeps_the_most_recently_used_and_never_touches_other_folders()
    {
        using var dir = new TempDir();
        var entries = new List<string>();
        for (int i = 0; i < 5; i++)
        {
            var e = Path.Combine(dir.Path, $"{i:x32}");
            Directory.CreateDirectory(e);
            File.WriteAllText(SlideCache.SlideFile(e, 1), "x");
            SlideCache.MarkComplete(e, new SlideCache.MarkerData(1, "Marp", "1x1", "test"));
            File.SetLastWriteTimeUtc(Path.Combine(e, SlideCache.Marker), DateTime.UtcNow.AddMinutes(-10 + i));
            entries.Add(e);
        }
        var foreign = Path.Combine(dir.Path, "my-documents");
        Directory.CreateDirectory(foreign);
        Assert.Equal(1, SlideCache.TryHit(entries[0])); // a hit makes entry 0 the most recent
        SlideCache.Evict(dir.Path, keep: 2);
        Assert.True(Directory.Exists(entries[0]));
        Assert.True(Directory.Exists(entries[4]));
        Assert.False(Directory.Exists(entries[1]));
        Assert.False(Directory.Exists(entries[3]));
        Assert.True(Directory.Exists(foreign));
    }

    [Fact]
    public void An_entry_missing_a_slide_is_not_a_hit()
    {
        using var dir = new TempDir();
        var e = Path.Combine(dir.Path, new string('a', 32));
        Directory.CreateDirectory(e);
        SlideCache.MarkComplete(e, new SlideCache.MarkerData(2, "Marp", "1x1", "test"));
        File.WriteAllText(SlideCache.SlideFile(e, 1), "x");
        Assert.Null(SlideCache.TryHit(e));
    }
}

public class MiscTests
{
    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("with space", "\"with space\"")]
    [InlineData(@"C:\a b\", "\"C:\\a b\\\\\"")]
    [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
    [InlineData("", "\"\"")]
    public void Arguments_are_quoted_for_the_c_runtime(string arg, string expected) =>
        Assert.Equal(expected, ChildProcess.Quote(arg));

    [Theory]
    [InlineData("deck.001.png", 1)]
    [InlineData("deck.012.png", 12)]
    [InlineData("my.deck.1000.png", 1000)]
    [InlineData("deck.png", null)]
    public void Marp_image_names(string name, int? expected) => Assert.Equal(expected, MarpRenderer.SlideNumber(name));

    [Theory]
    [InlineData("7.png", 7)]
    [InlineData("07.png", 7)]
    [InlineData("07-2.png", null)]
    public void Slidev_image_names(string name, int? expected) => Assert.Equal(expected, SlidevRenderer.SlideNumber(name));

    [Fact]
    public async Task A_job_object_ends_what_it_started()
    {
        using var dir = new TempDir();
        using var job = new JobObject();
        var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        // cmd starts a child (ping) that would outlive it; the job ends both
        using var p = ChildProcess.Start(job, cmd, ["/d", "/c", "ping -n 30 127.0.0.1 >nul"], dir.Path,
            new Dictionary<string, string?>(), Path.Combine(dir.Path, "log.txt"));
        await Task.Delay(300);
        Assert.True(job.ActiveProcesses >= 2);
        job.Terminate();
        await p.Exited.WaitAsync(TimeSpan.FromSeconds(10));
        for (int i = 0; i < 50 && job.ActiveProcesses > 0; i++) await Task.Delay(50);
        Assert.Equal(0, job.ActiveProcesses);
    }

    [Fact]
    public async Task A_missing_deck_faults_completion_instead_of_throwing()
    {
        using var dir = new TempDir();
        var deck = new DeckInfo(Path.Combine(dir.Path, "gone.pdf"), DeckKind.Pdf, 4.0 / 3, 3);
        await using var render = SlideRender.Start(deck, new PixelSize(800, 600), new RenderOptions(dir.Path));
        var ex = await Assert.ThrowsAsync<SlideRenderException>(() => render.Completion);
        Assert.Contains("gone.pdf", ex.Message);
    }

    [Fact]
    public async Task A_missing_node_tool_says_how_to_install_it()
    {
        using var dir = new TempDir();
        var path = dir.File("deck.md", SampleDecks.Marp(2));
        var options = new RenderOptions(Path.Combine(dir.Path, "cache"), NodeToolsDir: Path.Combine(dir.Path, "no-tools"));
        await using var render = SlideRender.Start(Decks.Inspect(path), new PixelSize(640, 360), options);
        Assert.Equal(2, render.Total); // known before anything runs
        var ex = await Assert.ThrowsAsync<SlideRenderException>(() => render.Completion);
        Assert.Contains("npm install --prefix", ex.Message);
        Assert.Contains("@marp-team/marp-cli", ex.Message);
    }
}
