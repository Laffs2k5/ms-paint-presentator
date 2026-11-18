using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Presenter.Windows;

/// <summary>
/// A synthetic pen (E15): strokes land exactly where sent, live, with
/// pressure, and the mouse cursor stays where the speaker left it. Paint needs
/// at least about 1 ms between points, and drops the last segment of a stroke
/// (E27), so <see cref="Stroke"/> sends the end point twice.
/// </summary>
public sealed unsafe class SyntheticPen : IDisposable
{
    private const uint PT_PEN = 3, POINTER_FEEDBACK_NONE = 3, PEN_MASK_PRESSURE = 1;
    public const uint InRange = 0x2, InContact = 0x4, FirstButton = 0x10, Down = 0x10000, Update = 0x20000, Up = 0x40000;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINTER_INFO
    {
        public uint pointerType, pointerId, frameId, pointerFlags;
        public nint sourceDevice, hwndTarget;
        public POINT ptPixelLocation, ptHimetricLocation, ptPixelLocationRaw, ptHimetricLocationRaw;
        public uint dwTime, historyCount;
        public int InputData;
        public uint dwKeyStates;
        public ulong PerformanceCount;
        public int ButtonChangeType;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINTER_PEN_INFO
    {
        public POINTER_INFO pointerInfo;
        public uint penFlags, penMask, pressure, rotation;
        public int tiltX, tiltY;
    }

    // The union in POINTER_TYPE_INFO is as large as its touch member (144 bytes).
    [StructLayout(LayoutKind.Explicit, Size = 8 + 144)]
    private struct POINTER_TYPE_INFO
    {
        [FieldOffset(0)] public uint type;
        [FieldOffset(8)] public POINTER_PEN_INFO penInfo;
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern nint CreateSyntheticPointerDevice(uint type, uint maxCount, uint mode);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool InjectSyntheticPointerInput(nint device, POINTER_TYPE_INFO* info, uint count);
    [DllImport("user32.dll")] private static extern void DestroySyntheticPointerDevice(nint device);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);

    private readonly nint _device;
    private readonly int _vx, _vy;
    public uint Pressure { get; set; } = 512;

    public SyntheticPen()
    {
        _device = CreateSyntheticPointerDevice(PT_PEN, 1, POINTER_FEEDBACK_NONE);
        if (_device == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateSyntheticPointerDevice failed");
        _vx = GetSystemMetrics(76);  // SM_XVIRTUALSCREEN
        _vy = GetSystemMetrics(77);
    }

    public void Inject(int x, int y, uint flags, int buttonChange = 0)
    {
        var info = new POINTER_TYPE_INFO { type = PT_PEN };
        info.penInfo.pointerInfo.pointerType = PT_PEN;
        info.penInfo.pointerInfo.pointerFlags = flags;
        info.penInfo.pointerInfo.ptPixelLocation = new POINT { X = x - _vx, Y = y - _vy };
        info.penInfo.pointerInfo.ButtonChangeType = buttonChange;
        info.penInfo.penMask = PEN_MASK_PRESSURE;
        info.penInfo.pressure = (flags & InContact) != 0 ? Pressure : 0;
        if (!InjectSyntheticPointerInput(_device, &info, 1))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "InjectSyntheticPointerInput failed");
    }

    public void Hover(int x, int y) => Inject(x, y, InRange | Update);

    public void PenDown(int x, int y)
    {
        Inject(x, y, InRange | Update);  // hover in first
        Inject(x, y, InRange | InContact | FirstButton | Down, 1);
    }

    public void Move(int x, int y) => Inject(x, y, InRange | InContact | FirstButton | Update);

    public void PenUp(int x, int y)
    {
        Inject(x, y, InRange | Up, 2);
        Inject(x, y, Update);  // out of range
    }

    /// <summary>One stroke through the points, <paramref name="intervalMs"/>
    /// apart (at least 1 ms), the end point sent twice (E27).</summary>
    public void Stroke(IReadOnlyList<(int X, int Y)> points, double intervalMs, CancellationToken ct = default)
    {
        if (points.Count == 0) return;
        intervalMs = Math.Max(1.0, intervalMs);
        long t0 = Stopwatch.GetTimestamp();
        double tick = Stopwatch.Frequency / 1000.0;
        PenDown(points[0].X, points[0].Y);
        int n = points.Count + 1;  // the last point twice
        for (int i = 1; i < n; i++)
        {
            var p = points[Math.Min(i, points.Count - 1)];
            long due = t0 + (long)(i * intervalMs * tick);
            while (Stopwatch.GetTimestamp() < due)
            {
                if (due - Stopwatch.GetTimestamp() > 2 * tick) Thread.Sleep(1);
                else Thread.SpinWait(20);
            }
            Move(p.X, p.Y);
            if (ct.IsCancellationRequested) break;
        }
        var last = points[^1];
        PenUp(last.X, last.Y);
    }

    /// <summary>A tap: a click that leaves the mouse where it is.</summary>
    public void Tap(int x, int y) => Stroke([(x, y)], 2);

    public void Dispose() => DestroySyntheticPointerDevice(_device);
}
