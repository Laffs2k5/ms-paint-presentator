using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Text.Json;
using Microsoft.Win32;
using Presenter.Core;
using Presenter.Windows;

namespace Presenter.Cli;

/// <summary>
/// `presenter doctor` (research 05 §7.7): reruns the POC's checks on this
/// machine and prints one report. The first thing to run on a new laptop, or
/// after a Paint update.
/// </summary>
internal static class DoctorCommand
{
    private enum Level { Pass, Warn, Fail }

    private sealed record Check(string Name, Level Level, string Detail);

    public static async Task<int> Run(string[] args)
    {
        string? monitorArg = null;
        bool json = false;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--monitor" when i + 1 < args.Length: monitorArg = args[++i]; break;
                case "--json": json = true; break;
                default: throw new UsageException($"unknown option {args[i]}");
            }
        }
        var checks = new List<Check>();
        void Add(string name, Level level, string detail)
        {
            checks.Add(new Check(name, level, detail));
            if (!json) Console.WriteLine($"{level switch { Level.Pass => "ok  ", Level.Warn => "warn", _ => "FAIL" }}  {name}: {detail}");
        }

        // --- the machine -------------------------------------------------------------------------
        var monitors = Monitors.All();
        Add("displays", Level.Pass, string.Join("; ", monitors.Select((m, i) => $"{i + 1}. {m}")));
        var monitor = Monitors.Find(monitorArg);
        Add("power", Power.OnMains() ? Level.Pass : Level.Warn,
            Power.OnMains() ? "on mains power" : "on battery: Paint is slower and less steady (E6)");
        Add("do not disturb", Level.Warn, "cannot be read by any API (E19): switch it on before a show");
        if (Which("mspaint.exe") is null) Add("Paint", Level.Fail, "mspaint.exe not found (Windows 11's Paint from the Store is needed)");

        // --- renderers' prerequisites (research 05 §6) ---------------------------------------------
        Add("Node", Which("node.exe") is { } node ? Level.Pass : Level.Warn,
            Which("node.exe") ?? "not on PATH: Marp and Slidev decks need it");
        string edge = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Microsoft\Edge\Application\msedge.exe");
        Add("Edge", File.Exists(edge) ? Level.Pass : Level.Warn, File.Exists(edge) ? edge : "not found: Marp and Slidev render through it");
        bool ppt = Registry.ClassesRoot.OpenSubKey(@"PowerPoint.Application\CLSID") is not null;
        Add("PowerPoint", ppt ? Level.Pass : Level.Warn, ppt ? "registered for automation" : "not installed: .pptx decks cannot be rendered");

        // --- Paint itself ------------------------------------------------------------------------------
        var predicted = SlideGeometry.PredictedViewport(monitor.WorkArea.Size, monitor.Dpi);
        var size = SlideGeometry.SlideSize(predicted, 16.0 / 9);
        using var clipboard = new ClipboardHost();
        using var stage = new PaintStage(clipboard, json ? null : m => Console.WriteLine($"      {m}"));
        KeyHook? hook = null;
        try
        {
            var paint = await stage.OpenPaint(monitor, size);
            Add("Paint", Level.Pass, PaintPackage(paint.ExecutablePath) ?? paint.ExecutablePath ?? "running");
            var vp = await stage.Run(p => p!.Viewport());
            Add("viewport", vp?.Size == predicted ? Level.Pass : Level.Warn,
                vp is null ? "not found" : $"{vp.Value.Size}, predicted {predicted} (E11){(vp.Value.Size == predicted ? "" : ": Paint will open twice")}");
            var missing = await stage.Run(p => MissingElements(p!));
            Add("UI Automation", missing.Count == 0 ? Level.Pass : Level.Fail,
                missing.Count == 0 ? "every element the presenter uses is there (E4)" : $"missing: {string.Join(", ", missing)} (a Paint update or a localised Paint?)");

            var (ox, oy, dx, dy) = await stage.Run(p => { p!.Focus(); Keyboard.Tap('S'); Thread.Sleep(300); return p.MeasureOrigin(); });
            Add("canvas origin", Math.Abs(dx) <= 2 && Math.Abs(dy) <= 2 ? Level.Pass : Level.Warn, $"UIA's rectangle is off by ({dx}, {dy}) px");

            // a calibration paste, read back pixel by pixel (E11)
            byte[] a = Pattern(size, 1), b = Pattern(size, 2);
            stage.SetSlides(i => i == 1 ? a : b);
            await stage.ShowAsync(new Frame.Slide(1), CancellationToken.None);
            var (wrong, lastRow) = await stage.Run(p =>
            {
                Thread.Sleep(200);
                var c = p!.CanvasOnScreen()!.Value;
                using var shot = ScreenCapture.Grab(PixelRect.FromSize(c.Left + dx, c.Top + dy, size));
                using var want = new Bitmap(new MemoryStream(a));
                if (Environment.GetEnvironmentVariable("PRESENTER_VERIFY_DIR") is { Length: > 0 } dir)
                {
                    Directory.CreateDirectory(dir);
                    shot.Save(Path.Combine(dir, "doctor-seen.png"), ImageFormat.Png);
                    want.Save(Path.Combine(dir, "doctor-want.png"), ImageFormat.Png);
                    using var full = ScreenCapture.Grab(p.Monitor.Bounds);
                    full.Save(Path.Combine(dir, "doctor-screen.png"), ImageFormat.Png);
                }
                // Paint draws a light line over the canvas's last row on screen: the
                // rows above it must match exactly, the last one is reported apart
                return (Differences(shot, want, 0, size.Height - 1), Differences(shot, want, size.Height - 1, size.Height));
            });
            Add("calibration paste", wrong == 0 ? Level.Pass : Level.Fail,
                (wrong == 0 ? $"{size} pasted pixel-exact" : $"{wrong} of {size.Area} pixels differ: zoom, scaling or the canvas position is off")
                + (lastRow > 0 ? $" (the last row, {lastRow} px, is under Paint's canvas edge)" : ""));

            // paste timing (E6): key-less, stage only
            var times = new List<double>();
            for (int i = 0; i < 10; i++)
            {
                long t0 = Stopwatch.GetTimestamp();
                long? seen = await stage.ShowAsync(new Frame.Slide(i % 2 == 0 ? 2 : 1), CancellationToken.None);
                if (seen is { } s) times.Add((s - t0) * 1000.0 / Stopwatch.Frequency);
            }
            times.Sort();
            Add("paste speed", times.Count == 10 && times[5] < 300 ? Level.Pass : Level.Warn,
                times.Count == 0 ? "no paste was seen" : $"median {times[times.Count / 2]:F0} ms, max {times[^1]:F0} ms at {size} ({times.Count}/10 seen)");

            // the hook (E10)
            hook = new KeyHook(paint.Window);
            bool alive = hook.Alive();
            await stage.Run(p => p!.Focus());
            Keyboard.Tap(0x27, tagged: false);  // Right, as the speaker would
            bool taken = await Task.Run(async () =>
            {
                using var cts = new CancellationTokenSource(1000);
                try { return (await hook.Keys.ReadAsync(cts.Token)).Action == ShowAction.Next; }
                catch (OperationCanceledException) { return false; }
            });
            Add("keyboard hook", alive && taken ? Level.Pass : Level.Fail,
                $"heartbeat {(alive ? "answered" : "MISSED")}; a Right while Paint is in front was {(taken ? "taken" : "NOT taken")}");

            bool restored = await stage.Run(p =>
            {
                using var pen = new SyntheticPen();
                return new Toolbox(p!, pen).RestoreTextSettings(ox + 200, oy + 200);
            });
            Add("text settings", restored ? Level.Warn : Level.Pass,
                restored ? "an interrupted show had left Paint's Text font changed: put back" : "nothing left over");
        }
        catch (Exception e) when (e is PaintException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            Add("Paint", Level.Fail, e.Message);
        }
        finally
        {
            hook?.Dispose();
            await stage.Run(p => { p?.Close(discard: true); });
            clipboard.Clear();
        }

        if (json) Console.WriteLine(JsonSerializer.Serialize(checks.Select(c => new { c.Name, Level = c.Level.ToString().ToLowerInvariant(), c.Detail }),
                                                             new JsonSerializerOptions { WriteIndented = true }));
        return checks.Any(c => c.Level == Level.Fail) ? 1 : 0;
    }

    private static List<string> MissingElements(PaintSession p)
    {
        var missing = new List<string>();
        foreach (var id in new[] { "scrollViewer", "image", "ZoomSliderControl", "CropButton", "BrushesSplitButton" })
            if (p.ById(id) is null) missing.Add(id);
        foreach (var name in new[] { "Fit to window", "Pencil" })
            if (p.ByName(name) is null) missing.Add($"\"{name}\"");
        foreach (var colour in Toolbox.Palette)
            if (p.ByName(colour, FlaUI.Core.Definitions.ControlType.ListItem) is null) missing.Add($"colour \"{colour}\"");
        return missing;
    }

    /// <summary>Paint's package folder names its version and architecture,
    /// e.g. Microsoft.Paint_11.2609.401.0_arm64__8wekyb3d8bbwe.</summary>
    private static string? PaintPackage(string? exe) =>
        exe?.Split(Path.DirectorySeparatorChar).FirstOrDefault(d => d.StartsWith("Microsoft.Paint_", StringComparison.OrdinalIgnoreCase)) is { } pkg
            ? string.Join(' ', pkg.Split('_').Skip(1).Take(2)) : null;

    private static string? Which(string exe) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)
        .Select(d => Path.Combine(d.Trim(), exe)).FirstOrDefault(File.Exists);

    /// <summary>A test pattern: coloured blocks and one-pixel lines, so a
    /// shift or a resample by a single pixel shows.</summary>
    private static byte[] Pattern(PixelSize size, int seed)
    {
        var rng = new Random(seed);
        using var bmp = new Bitmap(size.Width, size.Height, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.White);
            for (int i = 0; i < 60; i++)
                using (var br = new SolidBrush(Color.FromArgb(rng.Next(256), rng.Next(256), rng.Next(256))))
                    g.FillRectangle(br, rng.Next(size.Width), rng.Next(size.Height), 40 + rng.Next(300), 40 + rng.Next(200));
            for (int x = 0; x < size.Width; x += 17) bmp.SetPixel(x, (x * 7) % size.Height, Color.Black);
        }
        return ScreenCapture.Png(bmp);
    }

    private static unsafe int Differences(Bitmap a, Bitmap b, int fromRow, int toRow)
    {
        if (a.Size != b.Size) return a.Width * a.Height;
        var r = new Rectangle(0, 0, a.Width, a.Height);
        var da = a.LockBits(r, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var db = b.LockBits(r, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int wrong = 0;
            for (int y = fromRow; y < toRow; y++)
            {
                uint* pa = (uint*)((byte*)da.Scan0 + y * da.Stride), pb = (uint*)((byte*)db.Scan0 + y * db.Stride);
                for (int x = 0; x < a.Width; x++)
                    if ((pa[x] & 0xFFFFFF) != (pb[x] & 0xFFFFFF)) wrong++;
            }
            return wrong;
        }
        finally
        {
            a.UnlockBits(da);
            b.UnlockBits(db);
        }
    }
}
