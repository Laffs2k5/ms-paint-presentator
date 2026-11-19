using System.Drawing;
using System.Drawing.Imaging;
using Presenter.Core;
using static Presenter.Windows.Native;

namespace Presenter.Windows;

/// <summary>A display as Windows reports it, in physical pixels.</summary>
public sealed record Monitor(string Device, PixelRect Bounds, PixelRect WorkArea, int Dpi, bool Primary, nint Handle)
{
    public int ScalePercent => Dpi * 100 / 96;
    public override string ToString() => $"{Device} {Bounds.Size} at {ScalePercent}%{(Primary ? " (primary)" : "")}";
}

public static class Monitors
{
    /// <summary>The active displays. The process must be per-monitor DPI aware
    /// (presenter.exe's manifest) for these to be physical pixels.</summary>
    public static IReadOnlyList<Monitor> All()
    {
        var list = new List<Monitor>();
        EnumDisplayMonitors(0, 0, (hmon, _, _, _) =>
        {
            var info = new MONITORINFOEXW { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFOEXW>() };
            if (GetMonitorInfoW(hmon, ref info))
            {
                GetDpiForMonitor(hmon, MDT_EFFECTIVE_DPI, out uint dpi, out _);
                list.Add(new Monitor(info.szDevice, Rect(info.rcMonitor), Rect(info.rcWork), (int)dpi,
                                     (info.dwFlags & MONITORINFOF_PRIMARY) != 0, hmon));
            }
            return true;
        }, 0);
        return list;
    }

    public static Monitor Primary() => All().First(m => m.Primary);

    /// <summary>A monitor by GDI name (\\.\DISPLAY2) or 1-based number, or the primary.</summary>
    public static Monitor Find(string? which)
    {
        var all = All();
        if (string.IsNullOrEmpty(which)) return all.First(m => m.Primary);
        if (int.TryParse(which, out int n) && n >= 1 && n <= all.Count) return all[n - 1];
        return all.FirstOrDefault(m => string.Equals(m.Device, which, StringComparison.OrdinalIgnoreCase))
               ?? throw new ArgumentException($"no display {which}; there are: {string.Join("; ", all.Select((m, i) => $"{i + 1}. {m}"))}");
    }
}

/// <summary>Screen reads. Each one waits for a display refresh (E6), so the
/// show itself never reads the screen per slide.</summary>
public static class ScreenCapture
{
    public static Bitmap Grab(PixelRect r)
    {
        var bmp = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.CopyFromScreen(r.Left, r.Top, 0, 0, new Size(r.Width, r.Height), CopyPixelOperation.SourceCopy);
        return bmp;
    }

    public static byte[] Png(Bitmap bmp)
    {
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    /// <summary>A solid PNG of the size (the blank screens, the blank canvas).</summary>
    public static byte[] SolidPng(PixelSize size, Color colour)
    {
        using var bmp = new Bitmap(size.Width, size.Height, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(bmp)) g.Clear(colour);
        return Png(bmp);
    }

    /// <summary>Every pixel of the bitmap has this colour.</summary>
    public static unsafe bool IsSolid(Bitmap bmp, Color colour)
    {
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            uint want = (uint)colour.ToArgb() | 0xFF000000;
            for (int y = 0; y < bmp.Height; y++)
            {
                uint* row = (uint*)((byte*)data.Scan0 + y * data.Stride);
                for (int x = 0; x < bmp.Width; x++)
                    if ((row[x] | 0xFF000000) != want) return false;
            }
            return true;
        }
        finally { bmp.UnlockBits(data); }
    }
}

/// <summary>Keeps the system and display awake for the show (E19).</summary>
public static class Power
{
    public static void KeepAwake(bool on) =>
        SetThreadExecutionState(on ? ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED : ES_CONTINUOUS);

    public static bool OnMains() => GetSystemPowerStatus(out var s) && s.ACLineStatus == 1;
}
