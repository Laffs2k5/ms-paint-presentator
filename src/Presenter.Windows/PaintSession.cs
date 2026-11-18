using System.Diagnostics;
using System.Drawing;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;
using Presenter.Core;
using static Presenter.Windows.Native;

namespace Presenter.Windows;

public sealed class PaintException(string message) : Exception(message);

/// <summary>
/// One Paint window, opened on a blank canvas of an exact size (paintctl, E11):
/// launched, maximised on a monitor at 100% zoom, measured through UI
/// Automation (E4). Element lookups go through this class only; the UIA
/// objects it caches belong to the thread that created the session (the
/// stage).
/// </summary>
public sealed class PaintSession : IDisposable
{
    private readonly UIA3Automation _uia = new();
    private readonly Action<string>? _log;
    private AutomationElement? _window, _crop;

    public nint Window { get; }
    public string? ExecutablePath => ProcessImage(Window);

    /// <summary>The window is still there (Paint was not closed and did not crash).</summary>
    public bool Alive => IsWindow(Window) && IsWindowVisible(Window);

    /// <summary>Still maximised, as the slide size assumes.</summary>
    public bool Maximised => IsZoomed(Window);
    public PixelSize Canvas { get; }
    public Monitor Monitor { get; private set; }

    private PaintSession(nint hwnd, PixelSize canvas, Monitor monitor, Action<string>? log)
    {
        Window = hwnd;
        Canvas = canvas;
        Monitor = monitor;
        _log = log;
    }

    // --- launching ------------------------------------------------------------------------------
    /// <summary>Open a new Paint window on a blank canvas of <paramref name="canvas"/>
    /// pixels, maximised on <paramref name="monitor"/> at 100% zoom.</summary>
    public static PaintSession Launch(Monitor monitor, PixelSize canvas, Action<string>? log = null)
    {
        string file = BlankCanvasFile(canvas);
        var before = PaintWindows().Select(w => w.Hwnd).ToHashSet();
        Process.Start(new ProcessStartInfo("mspaint.exe") { ArgumentList = { file }, UseShellExecute = false })?.Dispose();
        string stem = Path.GetFileNameWithoutExtension(file);
        var deadline = Stopwatch.StartNew();
        nint hwnd = 0;
        while (deadline.Elapsed < TimeSpan.FromSeconds(20) && hwnd == 0)
        {
            hwnd = PaintWindows().FirstOrDefault(w => !before.Contains(w.Hwnd) && w.Title.StartsWith(stem)).Hwnd;
            if (hwnd == 0) Thread.Sleep(200);
        }
        if (hwnd == 0) throw new PaintException("Paint's window did not appear within 20 s");
        Thread.Sleep(800);  // let the canvas finish initialising
        var s = new PaintSession(hwnd, canvas, monitor, log);
        s.PlaceOn(monitor);
        return s;
    }

    /// <summary>Maximise on the monitor at 100% zoom, the canvas centred.</summary>
    public void PlaceOn(Monitor monitor)
    {
        Monitor = monitor;
        if (IsZoomed(Window)) { ShowWindow(Window, SW_RESTORE); Thread.Sleep(300); }
        // A small restored rectangle well inside the monitor: crossing a DPI
        // boundary makes Paint rescale itself (paintctl's notes).
        SetWindowPos(Window, 0, monitor.WorkArea.Left + 40, monitor.WorkArea.Top + 40, 900, 700, SWP_NOZORDER | SWP_NOACTIVATE);
        Thread.Sleep(300);
        ShowWindow(Window, SW_MAXIMIZE);
        Thread.Sleep(300);
        ZoomFit();  // fit, then 100%, re-centres a scrolled canvas (E11)
        Zoom(100);
        Thread.Sleep(300);
    }

    /// <summary>A blank white PNG of the size, in our temp folder. If someone
    /// saved a picture over it in Paint (File > Save writes back to it), it is
    /// kept under another name and a blank one written.</summary>
    public static string BlankCanvasFile(PixelSize size)
    {
        string dir = Path.Combine(Path.GetTempPath(), "PaintPresentator");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, $"canvas-{size}.png");
        if (File.Exists(path))
        {
            bool blank;
            try
            {
                using var bmp = new Bitmap(path);
                blank = bmp.Width == size.Width && bmp.Height == size.Height && ScreenCapture.IsSolid(bmp, Color.White);
            }
            catch (Exception e) when (e is ArgumentException or IOException) { blank = false; }
            if (!blank) File.Move(path, Path.Combine(dir, $"canvas-{size}-saved-{DateTime.Now:yyyyMMdd-HHmmss}.png"));
        }
        if (!File.Exists(path)) File.WriteAllBytes(path, ScreenCapture.SolidPng(size, Color.White));
        return path;
    }

    /// <summary>Visible Paint document windows ("… - Paint").</summary>
    public static List<(nint Hwnd, string Title)> PaintWindows()
    {
        var found = new List<(nint, string)>();
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            string title = WindowText(h);
            if (title.EndsWith("Paint") && Path.GetFileName(ProcessImage(h) ?? "").Equals("mspaint.exe", StringComparison.OrdinalIgnoreCase))
                found.Add((h, title));
            return true;
        }, 0);
        return found;
    }

    // --- UI Automation ----------------------------------------------------------------------------
    private AutomationElement Win => _window ??= _uia.FromHandle(Window);

    public AutomationElement? ById(string id) => Win.FindFirstDescendant(cf => cf.ByAutomationId(id));
    public AutomationElement? ByName(string name) => Win.FindFirstDescendant(cf => cf.ByName(name));
    public AutomationElement? ByName(string name, FlaUI.Core.Definitions.ControlType type) =>
        Win.FindFirstDescendant(cf => cf.ByName(name).And(cf.ByControlType(type)));
    public AutomationElement? Find(Func<FlaUI.Core.Conditions.ConditionFactory, FlaUI.Core.Conditions.ConditionBase> condition) =>
        Win.FindFirstDescendant(condition);

    // A search of Paint's element tree takes tens of milliseconds, and the
    // painter asks for the same swatches and buttons again and again.
    private readonly Dictionary<string, AutomationElement> _cache = [];

    /// <summary>An element found once and kept; <see cref="Forget"/> it when it
    /// turns out stale, and the next call searches again.</summary>
    public AutomationElement? Cached(string key, Func<AutomationElement?> find)
    {
        if (_cache.TryGetValue(key, out var e)) return e;
        var found = find();
        if (found is not null) _cache[key] = found;
        return found;
    }

    public void Forget(string key) => _cache.Remove(key);

    private static PixelRect? RectOf(AutomationElement? e)
    {
        if (e is null) return null;
        var r = e.BoundingRectangle;
        return r.IsEmpty ? null : new PixelRect(r.Left, r.Top, r.Right, r.Bottom);
    }

    /// <summary>Where the canvas can show (UIA "scrollViewer").</summary>
    public PixelRect? Viewport() => RectOf(ById("scrollViewer"));

    /// <summary>The canvas on screen (UIA "image"), clipped to the viewport.</summary>
    public PixelRect? CanvasOnScreen() => RectOf(ById("image"));

    public void Zoom(int percent)
    {
        var slider = ById("ZoomSliderControl")?.Patterns.RangeValue.PatternOrDefault
                     ?? throw new PaintException("Paint's zoom slider was not found");
        // Setting the value it already has is a no-op, and after Fit to window it
        // can read 100.0 at a real 99.8%: step to a neighbour first (paintctl).
        slider.SetValue(percent < slider.Maximum.Value ? percent + 1 : percent - 1);
        Thread.Sleep(150);
        slider.SetValue(percent);
        Thread.Sleep(300);
    }

    public void ZoomFit()
    {
        var button = ByName("Fit to window") ?? throw new PaintException("Paint's Fit to window button was not found (a localised Paint?)");
        button.Patterns.Invoke.Pattern.Invoke();
        Thread.Sleep(300);
    }

    /// <summary>The Crop button is enabled exactly while a selection floats on
    /// the canvas: a pasted slide before its commit (E21).</summary>
    public bool SelectionFloats()
    {
        _crop ??= ById("CropButton") ?? throw new PaintException("Paint's Crop button was not found");
        try
        {
            return _crop.IsEnabled;
        }
        catch (Exception e) when (e is FlaUI.Core.Exceptions.FlaUIException or System.Runtime.InteropServices.COMException)
        {
            _crop = null;  // stale: Paint closed or rebuilt its toolbar
            throw new PaintException($"Paint's Crop button cannot be read ({e.GetType().Name}): is Paint still there?");
        }
    }

    // --- the background route (E2) -------------------------------------------------------------------
    private nint _inputSite;

    /// <summary>Paste without the focus: MFC's Edit > Paste command (ID_EDIT_PASTE)
    /// posted to Paint's main window. Paint reads the clipboard within ~8 ms (E2).</summary>
    public void PostPaste() => PostMessageW(Window, WM_COMMAND, 0xE125, 0);

    /// <summary>A key posted to Paint's WinUI input window, the only window that
    /// takes keys posted from outside (E2): `S` commits a paste without the focus.</summary>
    public void PostKey(int vk)
    {
        nint site = InputSite();
        nint scan = (nint)MapVirtualKeyW((uint)vk, 0) << 16;
        PostMessageW(site, WM_KEYDOWN, (nuint)vk, 1 | scan);
        PostMessageW(site, WM_KEYUP, (nuint)vk, 1 | scan | (1 << 30) | unchecked((nint)(1u << 31)));
    }

    private nint InputSite()
    {
        if (_inputSite != 0 && IsWindow(_inputSite)) return _inputSite;
        _inputSite = 0;
        EnumChildWindows(Window, (h, _) =>
        {
            var cls = new System.Text.StringBuilder(64);
            GetClassNameW(h, cls, cls.Capacity);
            if (cls.ToString() != "InputSiteWindowClass") return true;
            _inputSite = h;
            return false;
        }, 0);
        return _inputSite != 0 ? _inputSite : throw new PaintException("Paint's input window was not found");
    }

    /// <summary>
    /// A key meant for Paint and nothing else. Synthetic keys go to whatever
    /// window is in front, so a key sent while another window has the focus
    /// lands in that app (a "b" was found typed into another program after a
    /// test night). Sent, tagged, only while Paint is in front; otherwise
    /// posted to Paint's own input window (E2), which needs no focus.
    /// </summary>
    public void Key(int vk)
    {
        if (InFront()) Keyboard.Tap(vk);
        else PostKey(vk);
    }

    /// <summary>Text for Paint's open text box only: typed while Paint is in
    /// front, else posted to its input window as characters. (Posted tool
    /// keys switch Paint's tools without the focus; posted characters were only
    /// seen working with Paint active: a pen tap on the canvas, as the painter
    /// makes before typing, brings Paint to the front.)</summary>
    public void TypeText(string text)
    {
        if (InFront())
        {
            Keyboard.Type(text);
            return;
        }
        nint site = InputSite();
        foreach (char c in text) PostMessageW(site, WM_CHAR, c, 1);
    }

    // --- focus, closing, dialogs ------------------------------------------------------------------
    public bool InFront() => GetAncestor(GetForegroundWindow(), GA_ROOTOWNER) == Window;

    public bool Focus(int timeoutMs = 3000)
    {
        if (IsIconic(Window)) ShowWindow(Window, SW_RESTORE);
        var sw = Stopwatch.StartNew();
        bool altTapped = false;
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            SetForegroundWindow(Window);
            if (InFront()) return true;
            if (!altTapped)
            {
                // the foreground lock: one Alt tap lets SetForegroundWindow through
                altTapped = true;
                keybd_event(VK_MENU, 0, 0, Keyboard.OwnTag);
                keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, Keyboard.OwnTag);
                continue;
            }
            Thread.Sleep(100);
        }
        return InFront();
    }

    /// <summary>Paint's "Can't complete operation" dialog blocks every paste
    /// until closed; its OK is closed through UIA, no keys sent (paintctl).</summary>
    public bool DismissClipboardError()
    {
        try
        {
            var dialog = Win.FindFirstChild(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.Window));
            if (dialog is null || !(dialog.Name ?? "").Contains("complete operation", StringComparison.OrdinalIgnoreCase))
                return false;
            dialog.FindFirstDescendant(cf => cf.ByAutomationId("CloseButton"))?.Patterns.Invoke.Pattern.Invoke();
            _log?.Invoke("closed Paint's clipboard-error dialog");
            return true;
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Close the window; with <paramref name="discard"/>, answer Don't
    /// save (the dialog's language-independent SecondaryButton).</summary>
    public bool Close(bool discard, int timeoutMs = 6000)
    {
        bool Gone() => !(IsWindow(Window) && IsWindowVisible(Window));
        PostMessageW(Window, WM_CLOSE, 0, 0);
        var sw = Stopwatch.StartNew();
        bool again = false;
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            Thread.Sleep(300);
            if (Gone()) return true;
            if (!again && sw.ElapsedMilliseconds > timeoutMs / 2)
            {
                // a close that went unanswered (seen once, after a long soak): ask again
                again = true;
                PostMessageW(Window, WM_CLOSE, 0, 0);
            }
            if (!discard) continue;
            try { ById("SecondaryButton")?.Patterns.Invoke.Pattern.Invoke(); }
            catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidOperationException) { }
        }
        return Gone();
    }

    // --- the canvas on screen -------------------------------------------------------------------------
    /// <summary>Wait until the canvas sits centred in the viewport; right after
    /// launch Paint can leave it scrolled and centre it only later (E13). If it
    /// stays off centre, fit and 100% again.</summary>
    public PixelRect CentredCanvas(int tries = 3)
    {
        PixelRect? c = null;
        for (int attempt = 0; attempt < tries; attempt++)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 3000)
            {
                c = CanvasOnScreen();
                var vp = Viewport();
                if (c is { } cr && vp is { } v)
                {
                    var want = SlideGeometry.CentredIn(v, cr.Size);
                    if (Math.Abs(cr.Left - want.Left) <= 2 && Math.Abs(cr.Top - want.Top) <= 2) return cr;
                }
                Thread.Sleep(200);
            }
            _log?.Invoke($"canvas not centred ({c}): re-centring");
            ZoomFit();
            Zoom(100);
        }
        return c ?? throw new PaintException("the canvas was not found");
    }

    /// <summary>The blank canvas's top-left pixel on screen, from its white
    /// area (as E17), and its offset from UIA's rectangle, which can be a pixel
    /// off (E11): later, when the canvas is no longer blank, the origin is
    /// UIA's rectangle plus this offset.</summary>
    public (int X, int Y, int Dx, int Dy) MeasureOrigin()
    {
        var c = CentredCanvas();
        const int pad = 6;
        var box = new PixelRect(c.Left - pad, c.Top - pad, c.Left + c.Width + pad, c.Top + c.Height + pad);
        using var bmp = ScreenCapture.Grab(box);
        int left = LongestWhiteRun(bmp, horizontal: true, at: bmp.Height / 4);
        int top = LongestWhiteRun(bmp, horizontal: false, at: bmp.Width / 4);
        int x = box.Left + left, y = box.Top + top;
        return (x, y, x - c.Left, y - c.Top);
    }

    private static int LongestWhiteRun(Bitmap bmp, bool horizontal, int at)
    {
        int n = horizontal ? bmp.Width : bmp.Height, bestStart = 0, bestLen = 0, start = -1;
        for (int i = 0; i <= n; i++)
        {
            bool white = i < n && (horizontal ? bmp.GetPixel(i, at) : bmp.GetPixel(at, i)).ToArgb() == Color.White.ToArgb();
            if (white && start < 0) start = i;
            if (!white && start >= 0)
            {
                if (i - start > bestLen) (bestStart, bestLen) = (start, i - start);
                start = -1;
            }
        }
        return bestStart;
    }

    public void Dispose() => _uia.Dispose();
}
