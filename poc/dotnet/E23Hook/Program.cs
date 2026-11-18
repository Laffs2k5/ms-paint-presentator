// E23: a C# low-level keyboard hook while the process allocates and collects.
//
// The presenter's hook callback is managed code, so a garbage collection that
// suspends managed threads also holds up the callback, and Windows waits only
// about 300 ms for a hook before passing the key on and holding up the whole
// keyboard (E10). This measures how long the callback is held up.
//
// A probe key with an unassigned virtual-key code (0x97) is sent every 20 ms
// with SendInput, tagged with its sequence number in dwExtraInfo. The hook
// swallows it (nothing reaches any app) and records when it was called.
// Latency = callback time - send time, for four loads:
//   idle         nothing else running
//   churn        4 threads allocating short-lived objects and 1 MB arrays
//   forced       churn, a 400 MB live heap, and a blocking full GC every 2 s
//   sustained    churn and the 400 MB heap under GCLatencyMode.SustainedLowLatency
// Prints JSON: latency median / p99 / max, probes missed, GC pause times.
using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text.Json;

static unsafe class Program
{
    const int WH_KEYBOARD_LL = 13, VK_PROBE = 0x97;
    const long TAG = 0x5052_0000_0000;  // "PR" in the high bits, sequence below

    [StructLayout(LayoutKind.Sequential)]
    struct KBDLLHOOKSTRUCT { public uint vk, scan, flags, time; public nuint extra; }

    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT { public ushort vk, scan; public uint flags, time; public nuint extra; }

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    struct INPUT { [FieldOffset(0)] public uint type; [FieldOffset(8)] public KEYBDINPUT ki; }

    [StructLayout(LayoutKind.Sequential)]
    struct MSG { public nint hwnd; public uint message; public nuint wParam; public nint lParam; public uint time; public int x, y; }

    delegate nint HookProc(int code, nuint wParam, nint lParam);

    [DllImport("user32.dll")] static extern nint SetWindowsHookExW(int id, HookProc proc, nint hmod, uint tid);
    [DllImport("user32.dll")] static extern nint CallNextHookEx(nint h, int code, nuint w, nint l);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(nint h);
    [DllImport("user32.dll")] static extern int GetMessageW(out MSG m, nint hwnd, uint min, uint max);
    [DllImport("user32.dll")] static extern bool PostThreadMessageW(uint tid, uint msg, nuint w, nint l);
    [DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT* inputs, int size);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll")] static extern nint GetModuleHandleW(nint name);

    static readonly HookProc Proc = Callback;  // rooted: never collected
    static long[] _sent = new long[1], _seen = new long[1];

    static nint Callback(int code, nuint wParam, nint lParam)
    {
        if (code >= 0)
        {
            var kb = (KBDLLHOOKSTRUCT*)lParam;
            long extra = (long)kb->extra;
            if ((extra & ~0xFFFF_FFFFL) == TAG)
            {
                if (wParam == 0x100) // WM_KEYDOWN: record, no allocation
                {
                    int seq = (int)(extra & 0xFFFF_FFFF);
                    if (seq < _seen.Length) _seen[seq] = Stopwatch.GetTimestamp();
                }
                return 1; // swallow the probe
            }
        }
        return CallNextHookEx(0, code, wParam, lParam);
    }

    static void Send(int seq)
    {
        INPUT* inp = stackalloc INPUT[2];
        inp[0] = new INPUT { type = 1, ki = new KEYBDINPUT { vk = VK_PROBE, extra = (nuint)(TAG | (uint)seq) } };
        inp[1] = new INPUT { type = 1, ki = new KEYBDINPUT { vk = VK_PROBE, flags = 2, extra = (nuint)(TAG | (uint)seq) } };
        _sent[seq] = Stopwatch.GetTimestamp();
        SendInput(2, inp, sizeof(INPUT));
    }

    static List<object> _live = new();

    static void Churn(CancellationToken stop)
    {
        var rng = new Random();
        var keep = new object[256];
        while (!stop.IsCancellationRequested)
        {
            for (int i = 0; i < 1000; i++)
                keep[rng.Next(keep.Length)] = new byte[rng.Next(16, 4096)];
            keep[rng.Next(keep.Length)] = new byte[1 << 20]; // a large-object-heap array
        }
    }

    static Dictionary<string, object> RunScenario(string name, int seconds)
    {
        int n = seconds * 50;
        _sent = new long[n];
        _seen = new long[n];
        var stop = new CancellationTokenSource();
        var workers = new List<Thread>();
        var oldMode = GCSettings.LatencyMode;
        if (name != "idle")
        {
            if (name is "forced" or "sustained")
                for (int i = 0; i < 400; i++) _live.Add(new byte[1 << 20]); // 400 MB live
            if (name == "sustained") GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
            for (int i = 0; i < 4; i++)
            {
                var t = new Thread(() => Churn(stop.Token)) { IsBackground = true };
                t.Start();
                workers.Add(t);
            }
            if (name == "forced")
            {
                var t = new Thread(() =>
                {
                    while (!stop.IsCancellationRequested)
                    {
                        Thread.Sleep(2000);
                        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                    }
                }) { IsBackground = true };
                t.Start();
                workers.Add(t);
            }
        }
        var pauseBefore = GC.GetTotalPauseDuration();
        int gen0 = GC.CollectionCount(0), gen2 = GC.CollectionCount(2);
        Thread.Sleep(300);
        // The probes come from a separate, idle process: a sender in this
        // process would be suspended by the same collections it should time.
        var sender = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, $"send {n} 20")
        { RedirectStandardOutput = true, UseShellExecute = false })!;
        var sentJson = sender.StandardOutput.ReadToEnd();
        sender.WaitForExit();
        _sent = JsonSerializer.Deserialize<long[]>(sentJson)!;
        Thread.Sleep(500);
        stop.Cancel();
        var maxBlocking = GC.GetGCMemoryInfo(GCKind.FullBlocking).PauseDurations.ToArray()
            .Select(p => p.TotalMilliseconds).DefaultIfEmpty(0).Max();
        foreach (var t in workers) t.Join();
        GCSettings.LatencyMode = oldMode;
        _live.Clear();
        double f = Stopwatch.Frequency / 1000.0;
        var lat = new List<double>();
        int missed = 0;
        for (int i = 0; i < n; i++)
        {
            if (_seen[i] == 0) missed++;
            else lat.Add((_seen[i] - _sent[i]) / f);
        }
        lat.Sort();
        var info = GC.GetGCMemoryInfo(GCKind.Any);
        return new()
        {
            ["probes"] = n,
            ["missed"] = missed,
            ["latency_ms_median"] = Math.Round(lat[lat.Count / 2], 3),
            ["latency_ms_p99"] = Math.Round(lat[(int)(lat.Count * 0.99)], 3),
            ["latency_ms_max"] = Math.Round(lat[^1], 3),
            ["gc_gen0"] = GC.CollectionCount(0) - gen0,
            ["gc_gen2"] = GC.CollectionCount(2) - gen2,
            ["gc_total_pause_ms"] = Math.Round((GC.GetTotalPauseDuration() - pauseBefore).TotalMilliseconds, 1),
            ["last_gc_pause_ms"] = info.PauseDurations.ToArray().Select(p => Math.Round(p.TotalMilliseconds, 2)).ToArray(),
            ["last_full_blocking_gc_pause_ms"] = Math.Round(maxBlocking, 2),
            ["latency_mode"] = name == "sustained" ? "SustainedLowLatency" : oldMode.ToString(),
        };
    }

    static int Main(string[] args)
    {
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        if (args.Length == 3 && args[0] == "send")  // the probe sender (a child process)
        {
            int count = int.Parse(args[1]), every = int.Parse(args[2]);
            _sent = new long[count];
            for (int seq = 0; seq < count; seq++)
            {
                Send(seq);
                Thread.Sleep(every);
            }
            Console.WriteLine(JsonSerializer.Serialize(_sent));
            return 0;
        }
        int seconds = args.Length > 0 ? int.Parse(args[0]) : 10;
        uint tid = 0;
        var ready = new ManualResetEventSlim();
        var hookThread = new Thread(() =>
        {
            tid = GetCurrentThreadId();
            nint h = SetWindowsHookExW(WH_KEYBOARD_LL, Proc, GetModuleHandleW(0), 0);
            ready.Set();
            while (GetMessageW(out _, 0, 0, 0) > 0) { }
            UnhookWindowsHookEx(h);
        }) { IsBackground = true, Priority = ThreadPriority.Highest };
        hookThread.Start();
        ready.Wait();
        var results = new Dictionary<string, object>
        {
            ["gc"] = new { server = GCSettings.IsServerGC, mode = GCSettings.LatencyMode.ToString() },
        };
        foreach (var s in new[] { "idle", "churn", "forced", "sustained" })
        {
            results[s] = RunScenario(s, seconds);
            Console.Error.WriteLine($"{s}: {JsonSerializer.Serialize(results[s])}");
        }
        PostThreadMessageW(tid, 0x12, 0, 0); // WM_QUIT
        hookThread.Join(2000);
        Console.WriteLine(JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }
}
