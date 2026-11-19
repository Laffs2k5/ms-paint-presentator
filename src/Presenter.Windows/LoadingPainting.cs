using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Presenter.Core;

namespace Presenter.Windows;

/// <summary>How far the slides are: <paramref name="Done"/> of <paramref name="Total"/>
/// ready (Total 0 while unknown).</summary>
public readonly record struct PaintingProgress(int Done, int Total);

/// <summary>
/// The loading painting, "The sky leaks" (docs/loading-painting.md), painted
/// live in Paint with the synthetic pen and Paint's own tools, ported from
/// the POC's loading_painting.py. The pen writes the title, draws a sun, a
/// cloud left open and a frame, fills the frame as slides become ready, adds
/// birds, then the bucket in the cloud leaks into the whole sky, colours in a
/// few letters, and the date is signed with a typo. It stops at once when
/// <c>stop</c> fires (the speaker's first key).
/// Runs on the stage thread.
/// </summary>
public sealed class LoadingPainting
{
    private static readonly string[] TitleColours = ["Indigo", "Red", "Purple", "Dark red", "Black"];
    // for the letters: the palette without White, Black, the greys and the sky
    private static readonly string[] LetterColours =
        ["Dark red", "Red", "Rose", "Orange", "Gold", "Yellow", "Green", "Lime", "Turquoise", "Indigo", "Blue-gray", "Purple", "Lavender", "Brown"];

    private readonly PaintSession _paint;
    private readonly Toolbox _tools;
    private readonly SyntheticPen _pen;
    private readonly (int X, int Y) _origin;
    private readonly PixelSize _size;
    private readonly string _title;
    private readonly Func<PaintingProgress> _progress;
    private readonly CancellationToken _stop;
    private readonly Action<string>? _log;
    private readonly Random _rng;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly double W = 1280, H, s, u;  // design space: 1280 wide, the canvas's shape
    private readonly bool _right;
    private readonly (double X, double Y) _sunC, _cloudC;
    private readonly double _sunR;
    private readonly (double X0, double Y0, double X1, double Y1) _frame;
    private (string Tool, string? Colour, int? Size)? _tool;
    private int _filled;
    private string _titleColour = "Black";
    private List<List<(double X, double Y)>> _titleShapes = [];
    private double _titleEm;

    public List<string> Steps { get; } = [];

    private readonly string? _tip;

    /// <param name="tip">A line for the bottom left, by a small light bulb,
    /// after the signature (demo mode: how to begin); none when null.</param>
    public LoadingPainting(PaintSession paint, Toolbox tools, SyntheticPen pen, (int X, int Y) origin, PixelSize size, string title,
                           Func<PaintingProgress> progress, CancellationToken stop, int? seed = null, Action<string>? log = null,
                           string? tip = null)
    {
        (_paint, _tools, _pen, _origin, _size, _title, _progress, _stop, _log) = (paint, tools, pen, origin, size, title, progress, stop, log);
        _tip = tip;
        _rng = seed is { } sd ? new Random(sd) : new Random();
        H = 1280.0 * size.Height / size.Width;
        s = size.Width / 1280.0;
        u = H / 720.0;
        _right = _rng.NextDouble() < 0.5;  // sun on the right, cloud on the left
        _sunC = ((_right ? 0.87 : 0.13) * W, 0.17 * H);
        _sunR = 0.075 * H;
        _cloudC = ((_right ? 0.22 : 0.78) * W, 0.14 * H);
        _frame = (0.12 * W, 0.75 * H, 0.88 * W, 0.86 * H);
    }

    /// <summary>Paint it all, or until stopped. Returns whether it was interrupted.</summary>
    public bool Run()
    {
        try
        {
            // Always the whole painting, however soon the slides are ready (the
            // user's call): the first key jumps to slide 1 anyway.
            Title();
            Sun();
            Cloud();
            FrameLines();
            CatchUp();  // the fill starts once the frame is there
            Birds();
            CatchUp();
            while (true)  // wait at the frame, one bite per slide that becomes ready
            {
                var p = _progress();
                if (p.Done > _filled) FillTo(p.Done, p.Total);
                if (p.Total > 0 && _filled >= p.Total) break;
                Check();
                Thread.Sleep(50);
            }
            Finale();
            Letters();
            Signature();
            if (_tip is not null) Tip(_tip);
            _tools.Key('S');  // the Select tool: no brush panel over the canvas
            if (_pen.Recoveries > 0) Note($"the pen was refused {_pen.Recoveries} time(s) and went down again");
            return false;
        }
        catch (OperationCanceledException)
        {
            _tools.Key('S');
            Note("stopped (a key, Ctrl+C, or the deck failed to render)");
            return true;
        }
        catch (Exception e)
        {
            // The painting is decoration: whatever goes wrong in it (the pen
            // refused, a tool not found), it stops and the show goes on.
            Note($"stopped by an error, the show goes on: {e.GetType().Name}: {e.Message}");
            try { _tools.Key('S'); } catch (Exception) { }
            return true;
        }
    }

    // --- drawing primitives -----------------------------------------------------------------------
    private void Check() => _stop.ThrowIfCancellationRequested();

    private void Note(string what)
    {
        string line = $"{_clock.Elapsed.TotalSeconds,5:F1}s {what}";
        Steps.Add(line);
        _log?.Invoke($"painting: {what}");
    }

    private (int X, int Y) At(double x, double y) =>
        ((int)Math.Round(_origin.X + x * s, MidpointRounding.AwayFromZero), (int)Math.Round(_origin.Y + y * s, MidpointRounding.AwayFromZero));

    /// <summary>Points every <paramref name="step"/> canvas px: Paint draws
    /// straight segments, and short ones keep curves round.</summary>
    private List<(double X, double Y)> Dense(IReadOnlyList<(double X, double Y)> pts, double step = 2.0)
    {
        var o = new List<(double, double)> { pts[0] };
        for (int i = 1; i < pts.Count; i++)
        {
            var (x0, y0) = pts[i - 1];
            var (x1, y1) = pts[i];
            int n = Math.Max(1, (int)Math.Ceiling(Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0)) * s / step));
            for (int j = 1; j <= n; j++) o.Add((x0 + (x1 - x0) * j / n, y0 + (y1 - y0) * j / n));
        }
        return o;
    }

    /// <summary>A shaky hand: two slow waves per axis, random phases.</summary>
    private List<(double X, double Y)> Wob(IReadOnlyList<(double X, double Y)> pts, double amp, double fq = 1.0)
    {
        double a = _rng.NextDouble() * 6, b = _rng.NextDouble() * 6;
        return pts.Select((p, i) => (p.X + amp * (Math.Sin(i * 0.11 * fq + a) + 0.5 * Math.Sin(i * 0.37 * fq + b)),
                                     p.Y + amp * (Math.Cos(i * 0.13 * fq + b) + 0.5 * Math.Sin(i * 0.29 * fq + a)))).ToList();
    }

    /// <summary>Draw design-space paths within <paramref name="budget"/> seconds.
    /// The pen needs at least 1 ms between points (E15): long paths are thinned.</summary>
    private void Strokes(IEnumerable<List<(double X, double Y)>> paths, double budget)
    {
        var screen = paths.Where(p => p.Count > 1).Select(p => p.Select(q => At(q.X, q.Y)).ToList()).ToList();
        int n = screen.Sum(p => p.Count);
        if (n == 0) return;
        double ms = budget * 1000 / n;
        int keep = 1;
        while (ms < 1.0)
        {
            keep++;
            ms = budget * 1000 / ((double)n / keep);
        }
        foreach (var p in screen)
        {
            Check();
            var q = p.Where((_, i) => i % keep == 0).ToList();
            if (q[^1] != p[^1]) q.Add(p[^1]);
            _pen.Stroke(q, ms, _stop);
        }
    }

    /// <summary>The Brush in a colour, <paramref name="width"/> design px wide (Brush width = size + 1, E27).</summary>
    private void Use(string colour, double width = 5.0)
    {
        int size = Math.Max(0, (int)Math.Round(width * u * s, MidpointRounding.AwayFromZero) - 1);
        Check();
        if (_tool is not { Tool: "Brush" })
        {
            _tools.Brush("Brush");
            _tool = ("Brush", null, null);
        }
        if (_tool.Value.Colour != colour) _tools.Colour(colour);
        if (_tool.Value.Size != size) _tools.Size(size);
        _tool = ("Brush", colour, size);
    }

    // --- the elements ------------------------------------------------------------------------------
    private void Title()
    {
        _titleColour = TitleColours[_rng.Next(TitleColours.Length)];
        Use(_titleColour);
        var box = (X0: 0.08 * W, Y0: 0.22 * H, X1: 0.92 * W, Y1: 0.58 * H);
        var chains = Wrap(_title, box);
        double minX = chains.Min(c => c.Min(p => p.X)), maxX = chains.Max(c => c.Max(p => p.X));
        double minY = chains.Min(c => c.Min(p => p.Y)), maxY = chains.Max(c => c.Max(p => p.Y));
        double bw = maxX - minX, bh = maxY - minY;
        double k = Math.Min((box.X1 - box.X0) / bw, (box.Y1 - box.Y0) / bh);
        double ox = box.X0 + ((box.X1 - box.X0) - bw * k) / 2 - minX * k;
        double oy = box.Y0 + ((box.Y1 - box.Y0) - bh * k) / 2 - minY * k;
        // the letters' true outlines, before the shake, for the bucket later
        _titleShapes = chains.Select(c => c.Select(p => (ox + p.X * k, oy + p.Y * k)).ToList()).ToList();
        _titleEm = 200 * k * s;  // the font's 200 px, in canvas px
        var paths = new List<List<(double X, double Y)>>();
        (double X, double Y)? penAt = null;
        foreach (var c in chains)
        {
            double dx = (_rng.NextDouble() - 0.5) * 2 * u, dy = (_rng.NextDouble() - 0.5) * 2 * u;
            var shaken = Wob(c.Where((_, i) => i % 2 == 0).Select(p => (ox + p.X * k + dx, oy + p.Y * k + dy)).ToList(), 1.5 * u, 1.3);
            var p = Dense([.. shaken, shaken[0], shaken[1]]);  // shaken first, then closed with an overshoot
            if (penAt is { } at && _rng.NextDouble() < 0.12 && Dist(at, p[0]) < 0.12 * W)
                paths[^1].AddRange(Dense([at, p[0]]).Concat(p));  // the pen forgot to lift
            else
                paths.Add(p);
            penAt = p[^1];
        }
        Note($"title in {_titleColour}");
        Strokes(paths, 3.0);
    }

    private static double Dist((double X, double Y) a, (double X, double Y) b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    /// <summary>The title's outlines (bold Comic Sans), wrapped into as many
    /// lines (up to 4) as makes the letters biggest in the box; each line
    /// centred on x = 0, lines 260 font px apart.</summary>
    private static List<List<(double X, double Y)>> Wrap(string text, (double X0, double Y0, double X1, double Y1) box)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) words = ["…"];
        double bw = box.X1 - box.X0, bh = box.Y1 - box.Y0;
        (double Scale, List<List<List<(double X, double Y)>>> Outs, List<(double A, double B)> Spans)? best = null;
        for (int lines = 1; lines <= Math.Min(4, words.Length); lines++)
        {
            int per = (int)Math.Ceiling((double)words.Length / lines);
            var texts = Enumerable.Range(0, (words.Length + per - 1) / per).Select(i => string.Join(' ', words.Skip(i * per).Take(per))).ToList();
            var outs = texts.Select(t => GlyphOutlines.Of(t, px: 200)).ToList();
            if (outs.Any(o => o.Count == 0)) continue;
            var spans = outs.Select(o => (A: o.Min(c => c.Min(p => p.X)), B: o.Max(c => c.Max(p => p.X)))).ToList();
            double scale = Math.Min(bw / spans.Max(sp => sp.B - sp.A), bh / (texts.Count * 260.0));
            if (best is null || scale > best.Value.Scale) best = (scale, outs, spans);
        }
        if (best is null) throw new InvalidOperationException("no outlines for the title (is Comic Sans MS installed?)");
        var (_, o2, sp2) = best.Value;
        return o2.SelectMany((o, i) => o.Select(c => c.Select(p => (p.X - (sp2[i].A + sp2[i].B) / 2, p.Y + i * 260)).ToList())).ToList();
    }

    private void Sun()
    {
        Use("Orange");
        var (sx, sy) = _sunC;
        double sr = _sunR;
        var ring = new List<(double, double)>();
        for (double a = 0; a <= 2 * Math.PI * 1.12; a += 0.25)
        {
            double rr = sr * (1 + (_rng.NextDouble() - 0.5) * 0.12);
            ring.Add((sx + Math.Cos(a) * rr, sy + Math.Sin(a) * rr));
        }
        var scribble = new List<(double, double)>();
        for (int i = 0; i < 16; i++)
        {
            double a = _rng.NextDouble() * 2 * Math.PI, rr = sr * (0.55 + _rng.NextDouble() * 0.4);
            scribble.Add((sx + Math.Cos(a) * rr, sy + Math.Sin(a) * rr));
        }
        var rays = new List<List<(double X, double Y)>>();
        int n = 7 + (int)(_rng.NextDouble() * 3);
        for (int i = 0; i < n; i++)
        {
            double a = i * 2 * Math.PI / n + (_rng.NextDouble() - 0.5) * 0.5;
            double r1 = sr * (1.2 + _rng.NextDouble() * 0.3), r2 = sr * (1.6 + _rng.NextDouble() * 0.7);
            double b = a + (_rng.NextDouble() - 0.5) * 0.3;
            List<(double X, double Y)> ray = [(sx + Math.Cos(a) * r1, sy + Math.Sin(a) * r1), (sx + Math.Cos(b) * r2, sy + Math.Sin(b) * r2)];
            rays.Add(Dense(ray));
            if (_rng.NextDouble() < 0.3) rays.Add(Dense(ray.Select(p => (p.X + 3 * u, p.Y + 2 * u)).ToList()));  // drawn twice
        }
        Note("sun");
        Strokes(new[] { Dense(ring), Dense(scribble) }.Concat(rays), 1.8);
    }

    private void Cloud()
    {
        Use("Black");
        var (cx, cy) = _cloudC;
        double cw = 0.14 * W, ch = 0.065 * H, gap = 16 * u;
        int bumps = 3 + (int)(_rng.NextDouble() * 3);
        var pts = new List<(double X, double Y)>
        {
            (cx - cw / 2 + gap, cy + ch / 2 + (_rng.NextDouble() - 0.5) * 4 * u),
            (cx + cw / 2, cy + ch / 2),
        };
        for (int i = 0; i <= 50; i++)
        {
            double t = i / 50.0;
            pts.Add((cx + cw / 2 - t * cw, cy + ch / 2 - ch * Math.Sin(Math.PI * t) * 0.9 - Math.Abs(Math.Sin(bumps * Math.PI * t)) * ch * 0.6));
        }
        Note("cloud, left open");
        Strokes([Dense(Wob(pts, 2 * u, 2))], 0.9);
    }

    private void FrameLines()
    {
        Use("Black");
        var (x0, y0, x1, y1) = _frame;
        double J() => (_rng.NextDouble() - 0.5) * 8 * u;
        List<(double X, double Y)> corners =
        [
            (x0 - 8 * u, y0 + J()), (x1 + 10 * u, y0 + J()), (x1 + J(), y0 - 6 * u), (x1 + J(), y1 + 8 * u),
            (x1 + 6 * u, y1 + J()), (x0 + J(), y1 + J()), (x0 + J(), y1 + 6 * u), (x0 + J(), y0 - 10 * u),
        ];
        Note("frame");
        Strokes([Dense(Wob(Dense(corners, 12), 1.5 * u))], 0.6);
    }

    /// <summary>Lime zigzag up to slide k of total: one bite of 1/total per slide.</summary>
    private void FillTo(int k, int total)
    {
        if (k <= _filled || total <= 0) return;
        Use("Lime", 8.0);
        var (x0, y0, x1, y1) = _frame;
        double iw = x1 - x0 - 12 * u;
        double xa = x0 + 6 * u + iw * _filled / total, xb = x0 + 6 * u + iw * k / total;
        var pts = new List<(double X, double Y)>();
        bool up = true;
        for (double x = xa; x <= xb; x += (3 + _rng.NextDouble() * 6) * u)
        {
            pts.Add((x, up ? y0 + (6 - _rng.NextDouble() * 12) * u : y1 - (6 - _rng.NextDouble() * 12) * u));
            up = !up;
        }
        if (pts.Count < 2) pts.Add((xb, y1 - 6 * u));
        Note($"fill: slides {_filled + 1}-{k} of {total}");
        Strokes([Dense(pts)], Math.Max(0.12, 1.4 * (k - _filled) / total));
        _filled = k;
    }

    private void CatchUp()
    {
        var p = _progress();
        if (p.Done > _filled) FillTo(p.Done, p.Total);
    }

    private void Birds()
    {
        Use("Black", 4.0);
        var paths = new List<List<(double X, double Y)>>();
        foreach (var (fx, fy) in new[] { (0.5, 0.09), (0.57, 0.14), (0.53, 0.2) })
        {
            if (_rng.NextDouble() < 0.3) continue;
            double x = (_right ? fx : 1 - fx) * W, y = fy * H, k = (0.7 + _rng.NextDouble() * 0.7) * u;
            paths.Add(Dense([(x - 16 * k, y - 4 * k), (x - 7 * k, y - 8 * k), (x, y + 2 * k), (x + 8 * k, y - 9 * k), (x + 17 * k, y - 2 * k)]));
        }
        Note("birds");
        Strokes(paths, 0.5);
    }

    /// <summary>The bucket in the cloud: it leaks through the gap and floods the sky.</summary>
    private void Finale()
    {
        Check();
        _tools.Key('B');  // Fill with color (tagged: B is the show's black-screen key)
        _tools.Colour("Light turquoise");
        _tool = null;
        var (sx, sy) = At(_cloudC.X, _cloudC.Y + 0.005 * H);
        Note("bucket: ...hmm");
        _pen.Hover(sx, sy);
        Thread.Sleep(800);
        Check();
        _pen.Tap(sx, sy);
        Note("leaks!");
        Thread.Sleep(500);
    }

    /// <summary>With the bucket still in hand: fill 1 to 4 of the title's
    /// letters, chosen at random, each in its own random colour.</summary>
    private void Letters()
    {
        Check();
        if (_titleShapes.Count == 0) return;
        int n = _rng.Next(1, 5);
        var colours = LetterColours.Where(c => c != _titleColour).OrderBy(_ => _rng.Next()).Take(n).ToList();
        Note($"letters in {string.Join(", ", colours)}");
        var picked = new List<(int X, int Y)>();
        foreach (var spot in LetterSpots())
        {
            if (picked.Count == n) break;
            if (picked.Any(p => Dist(p, spot) < 0.45 * _titleEm)) continue;  // likely the same letter
            if (!IsWhite(spot)) continue;  // filled already: a letter that touches one just filled
            Check();
            _tools.Colour(colours[picked.Count]);
            _pen.Tap(spot.X, spot.Y);
            picked.Add(spot);
            Thread.Sleep(300);
        }
    }

    /// <summary>
    /// Screen points well inside a letter's body (not in its holes) that are
    /// still white after the flood, in random order: one screen read of the
    /// title's area, compared at half resolution with the letters' true
    /// outlines (even-odd, so holes are out), shrunk by the shake and half
    /// the stroke.
    /// </summary>
    private List<(int X, int Y)> LetterSpots()
    {
        var pts = _titleShapes.SelectMany(c => c.Select(p => At(p.X, p.Y))).ToList();
        int x0 = pts.Min(p => p.X) - 4, y0 = pts.Min(p => p.Y) - 4, x1 = pts.Max(p => p.X) + 4, y1 = pts.Max(p => p.Y) + 4;
        using var shot = ScreenCapture.Grab(new PixelRect(x0, y0, x1, y1));
        int hw = shot.Width / 2, hh = shot.Height / 2;
        var white = new bool[hw, hh];
        var px = Pixels(shot);
        for (int y = 0; y < hh; y++)
            for (int x = 0; x < hw; x++)
                white[x, y] = Luma(px[2 * y * shot.Width + 2 * x]) >= 245;
        var body = new bool[hw, hh];
        using (var mask = new Bitmap(hw, hh, PixelFormat.Format32bppArgb))
        {
            using (var g = Graphics.FromImage(mask))
            using (var path = new GraphicsPath(FillMode.Alternate))  // even-odd: a hole cancels its letter
            {
                g.Clear(Color.White);
                foreach (var c in _titleShapes)
                    path.AddPolygon(c.Select(p => At(p.X, p.Y)).Select(q => new PointF((q.X - x0) / 2f, (q.Y - y0) / 2f)).ToArray());
                g.FillPath(Brushes.Black, path);
            }
            var mp = Pixels(mask);
            for (int y = 0; y < hh; y++)
                for (int x = 0; x < hw; x++)
                    body[x, y] = Luma(mp[y * hw + x]) < 128;
        }
        body = Erode(body, (int)Math.Ceiling(5 * u * s / 2));  // the shake + half the stroke
        white = Erode(white, 2);  // and 4 px of white around the spot
        var spots = new List<(int X, int Y)>();
        for (int y = 0; y < hh; y += 3)
            for (int x = 0; x < hw; x += 3)
                if (body[x, y] && white[x, y]) spots.Add((x0 + 2 * x, y0 + 2 * y));
        return spots.OrderBy(_ => _rng.Next()).ToList();
    }

    private static bool IsWhite((int X, int Y) spot)
    {
        using var b = ScreenCapture.Grab(new PixelRect(spot.X - 2, spot.Y - 2, spot.X + 3, spot.Y + 3));
        return Pixels(b).All(p => Luma(p) >= 245);
    }

    private static int Luma(int argb) => (((argb >> 16) & 0xFF) * 299 + ((argb >> 8) & 0xFF) * 587 + (argb & 0xFF) * 114) / 1000;

    private static int[] Pixels(Bitmap b)
    {
        var data = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var o = new int[b.Width * b.Height];
            for (int y = 0; y < b.Height; y++)
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, o, y * b.Width, b.Width);
            return o;
        }
        finally { b.UnlockBits(data); }
    }

    /// <summary>3 x 3 erosion, <paramref name="times"/> over.</summary>
    private static bool[,] Erode(bool[,] m, int times)
    {
        int w = m.GetLength(0), h = m.GetLength(1);
        for (int t = 0; t < times; t++)
        {
            var o = new bool[w, h];
            for (int y = 1; y < h - 1; y++)
                for (int x = 1; x < w - 1; x++)
                    o[x, y] = m[x - 1, y - 1] && m[x, y - 1] && m[x + 1, y - 1] && m[x - 1, y] && m[x, y] && m[x + 1, y]
                              && m[x - 1, y + 1] && m[x, y + 1] && m[x + 1, y + 1];
            m = o;
        }
        return m;
    }

    /// <summary>Today's date in Comic Sans, in an opaque white box, with a
    /// typo (the last two digits swapped) put right with Backspace.</summary>
    private void Signature()
    {
        Check();
        var d = DateTime.Today;
        string dd = d.Day.ToString("00"), mm = d.Month.ToString("00"), yy = (d.Year % 100).ToString("00");
        // about 6% of the canvas height (larger than the concept's 3.3%, the
        // user's call); the box is drawn to fit "dd.mm.yy" in the bottom right
        double em = 0.06 * _size.Height;
        var (bx0, by0) = At(0.985 * W - 4.2 * em / s, 0.99 * H - 1.6 * em / s);
        var (bx1, by1) = At(0.985 * W, 0.99 * H);
        _tools.TextBox(bx0, by0, bx1, by1);
        _tools.RememberTextSettings();
        _tools.Font("Comic Sans MS");
        string? size = _tools.FontSize((int)Math.Round(em * 0.75));  // points = px * 0.75 at 100% zoom
        Note($"{_tools.CurrentFont()} {size}");
        _tools.BackgroundFill(true);
        _tools.Colour("White", slot: 2);  // the box's colour
        _tools.Colour("Black", slot: 1);
        _pen.Tap(bx0 + 6, by0 + 6);  // back into the box
        Thread.Sleep(300);
        Note("signature with a typo");
        _tools.Type($"{dd}.{mm}.{yy[1]}{yy[0]}");
        Thread.Sleep(700);
        _tools.Press(0x08);  // Backspace twice (tagged: Backspace is a show key)
        _tools.Press(0x08);
        _tools.Type(yy);
        Thread.Sleep(600);
        var (cx, cy) = At(0.40 * W, 0.94 * H);
        _tools.CommitText(cx, cy);
        _tool = null;
    }

    /// <summary>
    /// A small, crappy light bulb in the bottom left corner, coloured in with
    /// yellow scribbles, a few orange rays, and the tip beside it in Comic
    /// Sans. After the flood, so the sky does not wash it away; scribbles
    /// rather than the bucket, so a bulb that does not quite close cannot
    /// leak yellow into the sky.
    /// </summary>
    private void Tip(string tip)
    {
        double r = 0.026 * H, bx = 0.045 * W, by = 0.912 * H;  // the glass: radius and centre
        (double X, double Y) On(double deg, double k = 1) => (bx + Math.Cos(deg * Math.PI / 180) * r * k, by + Math.Sin(deg * Math.PI / 180) * r * k);

        // yellow first, a zigzag inside the glass
        Use("Yellow", 6.0);
        var glow = new List<(double X, double Y)>();
        for (int i = 0; i < 9; i++)
            glow.Add(On(_rng.NextDouble() * 360, 0.25 + _rng.NextDouble() * 0.5));
        Note("tip: a bulb");
        Strokes([Dense(glow)], 0.5);

        // then the outline: the glass, open at the bottom for the neck, and a screw base
        Use("Black", 3.5);
        var glass = new List<(double X, double Y)>();
        for (double a = 118; a <= 422; a += 8) glass.Add(On(a, 1 + (_rng.NextDouble() - 0.5) * 0.12));
        var neckL = new List<(double X, double Y)> { On(118), (bx - 0.45 * r, by + 1.55 * r) };
        var neckR = new List<(double X, double Y)> { On(62), (bx + 0.45 * r, by + 1.55 * r) };
        var threads = new List<List<(double X, double Y)>>();
        foreach (double k in new[] { 1.05, 1.3, 1.55 })
            threads.Add(Dense([(bx - 0.5 * r, by + k * r + (_rng.NextDouble() - 0.5) * 0.1 * r), (bx + 0.5 * r, by + (k - 0.08) * r)]));
        threads.Add(Dense([(bx - 0.2 * r, by + 1.8 * r), (bx + 0.2 * r, by + 1.8 * r)]));
        Strokes(new[] { Dense(Wob(glass, 0.6 * u, 3)), Dense(neckL), Dense(neckR) }.Concat(threads), 1.0);

        Use("Orange", 3.5);
        var rays = new List<List<(double X, double Y)>>();
        foreach (double a in new[] { -155.0, -115, -65, -30 })
        {
            double b = a + (_rng.NextDouble() - 0.5) * 12;
            rays.Add(Dense([On(b, 1.4), On(b, 1.9 + _rng.NextDouble() * 0.4)]));
        }
        Strokes(rays, 0.5);

        // the tip, typed: one line beside the bulb, with no box (the sky shows through)
        Check();
        double em = 0.038 * _size.Height;
        var (tx0, ty0) = At(bx + 2.2 * r, by - 0.9 * em / s);
        var (tx1, ty1) = At(bx + 2.2 * r + 24 * em / s, by + 0.9 * em / s);
        _tools.TextBox(tx0, ty0, tx1, ty1);
        _tools.Font("Comic Sans MS");
        _tools.FontSize((int)Math.Round(em * 0.75));
        _tools.BackgroundFill(false);
        _tools.Colour("Black", slot: 1);
        _pen.Tap(tx0 + 6, ty0 + 6);  // back into the box
        Thread.Sleep(300);
        Note($"tip: \"{tip}\"");
        _tools.Type(tip);
        Thread.Sleep(400);
        var (cx, cy) = At(0.72 * W, 0.94 * H);  // between the tip and the signature
        _tools.CommitText(cx, cy);
        _tool = null;
    }
}
