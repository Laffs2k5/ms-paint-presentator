// E18: display changes on demand, without pulling a cable.
//
// The presenter must notice a display going away mid-show, bring the
// screen picker back, and resume when the speaker picks. Pulling a cable is
// the real test; this switches displays off and on through Windows' own
// display configuration API (QueryDisplayConfig / SetDisplayConfig), which
// is what Windows does when a display disappears: the desktop shrinks, the
// windows on it move, WM_DISPLAYCHANGE goes out.
//
//   E18Displays list                    the active displays, numbered
//   E18Displays detach <N> --for <S>    display N off for S seconds, then back
//   E18Displays internal --for <S>      only the laptop's own panel for S seconds, then back
//   E18Displays restore                 put back the layout saved by the last detach/internal
//
// The layout as it was is saved to %TEMP%\E18Displays-layout.bin before any
// change and put back afterwards, also when the wait is cut short (Ctrl+C).
// If anything goes wrong: Win+P, then Extend, brings the displays back.
using System.ComponentModel;
using System.Runtime.InteropServices;

static class Program
{
    const uint QDC_ONLY_ACTIVE_PATHS = 0x2;
    const uint SDC_APPLY = 0x80, SDC_USE_SUPPLIED_DISPLAY_CONFIG = 0x20, SDC_ALLOW_CHANGES = 0x400,
               SDC_TOPOLOGY_INTERNAL = 0x1;
    const int PathSize = 72, ModeSize = 64;

    static readonly string LayoutFile = Path.Combine(Path.GetTempPath(), "E18Displays-layout.bin");

    [DllImport("user32.dll")] static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);
    [DllImport("user32.dll")] static extern int QueryDisplayConfig(uint flags, ref uint np, [Out] byte[] paths, ref uint nm, [Out] byte[] modes, nint topology);
    [DllImport("user32.dll")] static extern int SetDisplayConfig(uint np, byte[]? paths, uint nm, byte[]? modes, uint flags);
    [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(ref SOURCE_NAME info);
    [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(ref TARGET_NAME info);

    [StructLayout(LayoutKind.Sequential)] struct LUID { public uint low; public int high; }
    [StructLayout(LayoutKind.Sequential)] struct HEADER { public int type, size; public LUID adapterId; public uint id; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SOURCE_NAME { public HEADER header; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string gdi; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct TARGET_NAME
    {
        public HEADER header; public uint flags; public int technology; public ushort mfr, product; public uint connector;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string friendly;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string path;
    }

    record Layout(uint Paths, byte[] PathBytes, uint Modes, byte[] ModeBytes);

    static Layout Query()
    {
        Check(GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out uint np, out uint nm), "GetDisplayConfigBufferSizes");
        var paths = new byte[np * PathSize];
        var modes = new byte[Math.Max(1, nm) * ModeSize];
        Check(QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref np, paths, ref nm, modes, 0), "QueryDisplayConfig");
        return new Layout(np, paths[..(int)(np * PathSize)], nm, modes[..(int)(nm * ModeSize)]);
    }

    static (string Gdi, string Name, bool BuiltIn) Describe(byte[] paths, int i)
    {
        var p = paths.AsSpan(i * PathSize, PathSize);
        var srcAdapter = MemoryMarshal.Read<LUID>(p[0..8]);
        uint srcId = BitConverter.ToUInt32(p[8..12]);
        var tgtAdapter = MemoryMarshal.Read<LUID>(p[20..28]);
        uint tgtId = BitConverter.ToUInt32(p[28..32]);
        var src = new SOURCE_NAME { header = new() { type = 1, size = Marshal.SizeOf<SOURCE_NAME>(), adapterId = srcAdapter, id = srcId } };
        var tgt = new TARGET_NAME { header = new() { type = 2, size = Marshal.SizeOf<TARGET_NAME>(), adapterId = tgtAdapter, id = tgtId } };
        DisplayConfigGetDeviceInfo(ref src);
        DisplayConfigGetDeviceInfo(ref tgt);
        bool builtIn = tgt.technology == unchecked((int)0x80000000);
        string name = !string.IsNullOrWhiteSpace(tgt.friendly) ? tgt.friendly : builtIn ? "Built-in screen" : "Display";
        return (src.gdi, name, builtIn);
    }

    static void Check(int rc, string what)
    {
        if (rc != 0) throw new Win32Exception(rc, $"{what} failed ({rc})");
    }

    static void Save(Layout l)
    {
        using var w = new BinaryWriter(File.Create(LayoutFile));
        w.Write(l.Paths); w.Write(l.PathBytes.Length); w.Write(l.PathBytes);
        w.Write(l.Modes); w.Write(l.ModeBytes.Length); w.Write(l.ModeBytes);
    }

    static Layout Load()
    {
        using var r = new BinaryReader(File.OpenRead(LayoutFile));
        uint np = r.ReadUInt32(); var pb = r.ReadBytes(r.ReadInt32());
        uint nm = r.ReadUInt32(); var mb = r.ReadBytes(r.ReadInt32());
        return new Layout(np, pb, nm, mb);
    }

    static void Apply(Layout l) =>
        Check(SetDisplayConfig(l.Paths, l.PathBytes, l.Modes, l.ModeBytes,
                               SDC_APPLY | SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_ALLOW_CHANGES), "SetDisplayConfig");

    static void List(Layout l)
    {
        for (int i = 0; i < l.Paths; i++)
        {
            var (gdi, name, builtIn) = Describe(l.PathBytes, i);
            Console.WriteLine($"{i + 1}. {gdi}  {name}{(builtIn ? " (built-in)" : "")}");
        }
    }

    /// <summary>Change, wait, and put the saved layout back whatever happens.</summary>
    static int ChangeFor(Action change, double seconds)
    {
        var before = Query();
        Save(before);
        using var cut = new ManualResetEventSlim();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cut.Set(); };
        try
        {
            change();
            Console.WriteLine($"changed at {DateTime.Now:HH:mm:ss}; back in {seconds:F0} s (Ctrl+C: back now)");
            cut.Wait(TimeSpan.FromSeconds(seconds));
        }
        finally
        {
            Apply(before);
            Console.WriteLine($"back at {DateTime.Now:HH:mm:ss}");
        }
        return 0;
    }

    static int Main(string[] args)
    {
        double For() => args.SkipWhile(a => a != "--for").Skip(1).Select(s => double.Parse(s, System.Globalization.CultureInfo.InvariantCulture)).DefaultIfEmpty(20).First();
        switch (args.FirstOrDefault())
        {
            case "list":
                List(Query());
                return 0;
            case "detach" when args.Length >= 2 && int.TryParse(args[1], out int n):
            {
                var l = Query();
                if (n < 1 || n > l.Paths) throw new ArgumentException($"no display {n}; there are {l.Paths}");
                if (l.Paths < 2) throw new InvalidOperationException("only one display: nothing to detach");
                var (gdi, name, _) = Describe(l.PathBytes, n - 1);
                Console.WriteLine($"detaching {n}. {gdi} {name}");
                // the same layout without that display's path
                var kept = l.PathBytes.Where((_, b) => b / PathSize != n - 1).ToArray();
                return ChangeFor(() => Check(SetDisplayConfig(l.Paths - 1, kept, l.Modes, l.ModeBytes,
                    SDC_APPLY | SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_ALLOW_CHANGES), "SetDisplayConfig"), For());
            }
            case "internal":
                Console.WriteLine("only the built-in screen");
                return ChangeFor(() => Check(SetDisplayConfig(0, null, 0, null, SDC_APPLY | SDC_TOPOLOGY_INTERNAL), "SetDisplayConfig"), For());
            case "restore":
                Apply(Load());
                Console.WriteLine("layout put back");
                return 0;
            default:
                Console.Error.WriteLine("usage: E18Displays list | detach <N> [--for S] | internal [--for S] | restore");
                return 2;
        }
    }
}
