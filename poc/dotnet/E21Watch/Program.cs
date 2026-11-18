// E21: signals that a paste has landed in Paint, without reading the screen.
//
// A paste lands as a floating selection with its own toolbar (a popup
// window), and Paint enables its Crop button while a selection exists; the
// `S` commit removes both. Three candidate signals, watched at once:
//
//   uia       UIA property-changed events (IsEnabled) anywhere in Paint's window
//   poll      Crop button's IsEnabled read every 2 ms over UIA (cost per read)
//   winevent  WinEvents from Paint's process: windows shown or hidden, and
//             MSAA state changes (SetWinEventHook, out of context)
//
//   E21Watch.exe <paint hwnd, decimal>
//
// Prints "ready" once everything is subscribed, then one line per signal:
//   <source> <detail> <seconds>
// Seconds come from Stopwatch (QueryPerformanceCounter), the same clock as
// Python's time.perf_counter. Ends when stdin closes. Uses FlaUI (UIA3, MIT).
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;

CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
var hwnd = new IntPtr(long.Parse(args[0]));
object gate = new();
double Now() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
void Emit(string source, string detail)
{
    double t = Now();
    lock (gate) { Console.WriteLine($"{source} {detail} {t:F6}"); Console.Out.Flush(); }
}

using var automation = new UIA3Automation();
var window = automation.FromHandle(hwnd);
var crop = window.FindFirstDescendant(cf => cf.ByAutomationId("CropButton"))
           ?? throw new InvalidOperationException("no CropButton");

// 1. UIA: IsEnabled changes anywhere under the window
using var uiaHandler = window.RegisterPropertyChangedEvent(
    TreeScope.Subtree,
    (element, property, value) =>
    {
        string id = "?";
        try { id = element.Properties.AutomationId.ValueOrDefault ?? element.Properties.Name.ValueOrDefault ?? "?"; }
        catch { }
        Emit("uia", $"{id.Replace(' ', '_')}={value}");
    },
    automation.PropertyLibrary.Element.IsEnabled);

// 2. Poll the Crop button's IsEnabled
var stop = false;
var pollThread = new Thread(() =>
{
    using var a2 = new UIA3Automation();
    var c2 = a2.FromHandle(hwnd).FindFirstDescendant(cf => cf.ByAutomationId("CropButton"))!;
    bool last = c2.IsEnabled;
    long reads = 0;
    double spent = 0;
    while (!Volatile.Read(ref stop))
    {
        double t0 = Now();
        bool v = c2.IsEnabled;
        spent += Now() - t0;
        reads++;
        if (v != last) { Emit("poll", v.ToString()); last = v; }
        if (reads % 500 == 0) Emit("pollcost", $"{spent / reads * 1000:F3}ms");
        Thread.Sleep(2);
    }
}) { IsBackground = true };
pollThread.Start();

// 3. WinEvents from Paint's process
Native.GetWindowThreadProcessId(hwnd, out uint pid);
var winThread = new Thread(() =>
{
    Native.WinEventProc proc = (hook, ev, h, idObject, idChild, thread, time) =>
    {
        if (ev is 0x8002 or 0x8003 && idObject == 0)  // OBJECT_SHOW / OBJECT_HIDE, a window
        {
            var cls = new StringBuilder(128);
            Native.GetClassNameW(h, cls, cls.Capacity);
            Emit("winevent", $"{(ev == 0x8002 ? "show" : "hide")}:{cls}");
        }
        else if (ev == 0x800A)  // OBJECT_STATECHANGE
            Emit("winevent", $"state:{idObject}:{idChild}");
    };
    GC.KeepAlive(proc);
    var hook = Native.SetWinEventHook(0x8002, 0x800A, IntPtr.Zero, proc, pid, 0, 0);
    while (Native.GetMessageW(out _, IntPtr.Zero, 0, 0) > 0) { }
    Native.UnhookWinEvent(hook);
    GC.KeepAlive(proc);
}) { IsBackground = true };
winThread.Start();

Thread.Sleep(300);
Console.WriteLine($"ready {crop.IsEnabled}");
Console.Out.Flush();
while (Console.In.ReadLine() != null) { }
Volatile.Write(ref stop, true);
return 0;

static class Native
{
    public delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    [DllImport("user32.dll")]
    public static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr hmod, WinEventProc proc, uint pid, uint tid, uint flags);

    [DllImport("user32.dll")] public static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassNameW(IntPtr hwnd, StringBuilder name, int max);

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG { public IntPtr hwnd; public uint message; public UIntPtr wParam; public IntPtr lParam; public uint time; public int x, y; }

    [DllImport("user32.dll")] public static extern int GetMessageW(out MSG msg, IntPtr hwnd, uint min, uint max);
}
