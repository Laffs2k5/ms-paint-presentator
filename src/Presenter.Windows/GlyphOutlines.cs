using System.Runtime.InteropServices;

namespace Presenter.Windows;

/// <summary>
/// A text's outlines as polylines, from an installed font at run time (a GDI
/// path, flattened): the loading painting traces its title from them, and
/// nothing of the font ships with the repo.
/// </summary>
public static class GlyphOutlines
{
    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint hdc);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern nint CreateFontW(int height, int width, int escapement, int orientation, int weight, uint italic,
                                           uint underline, uint strikeOut, uint charSet, uint outPrecision, uint clipPrecision,
                                           uint quality, uint pitchAndFamily, string face);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint hdc, nint obj);
    [DllImport("gdi32.dll")] private static extern int SetBkMode(nint hdc, int mode);
    [DllImport("gdi32.dll")] private static extern bool BeginPath(nint hdc);
    [DllImport("gdi32.dll")] private static extern bool EndPath(nint hdc);
    [DllImport("gdi32.dll")] private static extern bool FlattenPath(nint hdc);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern bool TextOutW(nint hdc, int x, int y, string text, int length);
    [DllImport("gdi32.dll")] private static extern int GetPath(nint hdc, [Out] POINT[]? points, [Out] byte[]? types, int count);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint hdc);

    private const byte PT_CLOSEFIGURE = 1, PT_MOVETO = 6;

    /// <summary>The outlines of <paramref name="text"/> in bold <paramref name="font"/>
    /// at <paramref name="px"/> pixels, each contour closed.</summary>
    public static List<List<(double X, double Y)>> Of(string text, string font = "Comic Sans MS", int px = 200)
    {
        nint dc = CreateCompatibleDC(0);
        nint hf = CreateFontW(-px, 0, 0, 0, 700, 0, 0, 0, 1, 0, 0, 4, 0, font);
        nint old = SelectObject(dc, hf);
        try
        {
            SetBkMode(dc, 1);  // TRANSPARENT
            BeginPath(dc);
            TextOutW(dc, 0, 0, text, text.Length);
            EndPath(dc);
            FlattenPath(dc);
            int n = GetPath(dc, null, null, 0);
            if (n <= 0) return [];
            var pts = new POINT[n];
            var types = new byte[n];
            GetPath(dc, pts, types, n);
            var contours = new List<List<(double, double)>>();
            List<(double, double)> cur = [];
            for (int i = 0; i < n; i++)
            {
                if ((types[i] & ~PT_CLOSEFIGURE) == PT_MOVETO)
                {
                    if (cur.Count > 1) contours.Add(cur);
                    cur = [(pts[i].X, pts[i].Y)];
                }
                else cur.Add((pts[i].X, pts[i].Y));
                if ((types[i] & PT_CLOSEFIGURE) != 0 && cur.Count > 0) cur.Add(cur[0]);
            }
            if (cur.Count > 1) contours.Add(cur);
            return contours;
        }
        finally
        {
            SelectObject(dc, old);
            DeleteObject(hf);
            DeleteDC(dc);
        }
    }
}
