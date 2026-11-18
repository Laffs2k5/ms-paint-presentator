// E26: what identifies a monitor across a pulled cable?
//
// The screen picker must recognise "the same display is back" (the user's
// display-change decision) and remember the speaker's choice per display.
// Candidates, read for every active display:
//   gdi         the GDI device name (\\.\DISPLAY1): what the POC used
//   path        the monitor device path from QueryDisplayConfig (PnP
//               instance: model + the port it hangs on)
//   edid        manufacturer, product code and serial number from the EDID
//               Windows keeps in the registry for every monitor seen
//   technology  internal panel, HDMI, DisplayPort...: the picker's default
//               is the screen that is not the laptop's own
// Also lists every monitor Windows remembers (registry Enum\DISPLAY), to see
// whether monitors of the same model can be told apart by their EDID serial.
//
//   E26MonitorId.exe [--raw]
//
// Prints JSON. Without --raw, serial numbers and paths are replaced by short
// hashes: enough to compare runs (before and after a replug), without
// recording the identifiers themselves.
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
bool raw = args.Contains("--raw");
Native.SetProcessDpiAwarenessContext(-4);
string Id(string s) => raw ? s : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..8];

// --- QueryDisplayConfig: active paths ----------------------------------------------
Native.GetDisplayConfigBufferSizes(2, out uint np, out uint nm);  // QDC_ONLY_ACTIVE_PATHS
var paths = new Native.PATH[np];
var modes = new byte[Math.Max(1, nm) * 64];
int rc = Native.QueryDisplayConfig(2, ref np, paths, ref nm, modes, IntPtr.Zero);
if (rc != 0) throw new Exception($"QueryDisplayConfig {rc}");

// --- EnumDisplayMonitors: GDI name, bounds, primary, DPI -----------------------------
var monitors = new Dictionary<string, object>();
Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (h, _, _, _) =>
{
    var mi = new Native.MONITORINFOEX { cbSize = Marshal.SizeOf<Native.MONITORINFOEX>() };
    Native.GetMonitorInfoW(h, ref mi);
    Native.GetDpiForMonitor(h, 0, out uint dx, out _);
    monitors[mi.szDevice] = new
    {
        bounds = new[] { mi.rcMonitor.left, mi.rcMonitor.top, mi.rcMonitor.right - mi.rcMonitor.left, mi.rcMonitor.bottom - mi.rcMonitor.top },
        work = new[] { mi.rcWork.right - mi.rcWork.left, mi.rcWork.bottom - mi.rcWork.top },
        primary = (mi.dwFlags & 1) != 0,
        dpi = dx,
    };
    return true;
}, IntPtr.Zero);

var active = new List<object>();
var activeInstances = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
for (int i = 0; i < np; i++)
{
    var p = paths[i];
    var src = new Native.SOURCE_NAME { header = new() { type = 1, size = Marshal.SizeOf<Native.SOURCE_NAME>(), adapterId = p.sourceAdapter, id = p.sourceId } };
    Native.DisplayConfigGetDeviceInfo(ref src);
    var tgt = new Native.TARGET_NAME { header = new() { type = 2, size = Marshal.SizeOf<Native.TARGET_NAME>(), adapterId = p.targetAdapter, id = p.targetId } };
    Native.DisplayConfigGetDeviceInfo(ref tgt);
    var instance = Edid.InstanceFromPath(tgt.monitorDevicePath);
    if (instance != null) activeInstances.Add(instance);
    var edid = instance != null ? Edid.Read(instance) : null;
    active.Add(new
    {
        gdi = src.viewGdiDeviceName,
        monitor = monitors.GetValueOrDefault(src.viewGdiDeviceName),
        technology = Native.Technology(tgt.outputTechnology),
        friendly_name = tgt.monitorFriendlyDeviceName,
        edid_ids_from_config = $"{Edid.Pnp(tgt.edidManufactureId)}{tgt.edidProductCodeId:X4}",
        connector_instance = tgt.connectorInstance,
        path = Id(tgt.monitorDevicePath),
        adapter_luid = Id($"{p.targetAdapter.high:X8}{p.targetAdapter.low:X8}"),
        target_id = p.targetId,
        edid = edid == null ? null : new
        {
            model = edid.Model,
            name = edid.Name,
            serial_number_present = edid.SerialNumber != 0,
            serial_text_present = !string.IsNullOrEmpty(edid.SerialText),
            identity = Id($"{edid.Model}|{edid.SerialNumber}|{edid.SerialText}"),
        },
    });
}

// --- every monitor Windows remembers --------------------------------------------------
var known = new List<Edid>();
using (var display = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\DISPLAY"))
{
    foreach (var model in display?.GetSubKeyNames() ?? [])
        foreach (var inst in display!.OpenSubKey(model)!.GetSubKeyNames())
            if (Edid.Read($"{model}\\{inst}") is { } e) known.Add(e);
}
var byModel = known.GroupBy(e => e.Model).Select(g => new
{
    model = g.Key,
    name = g.First().Name,
    instances = g.Count(),
    active_now = g.Count(e => activeInstances.Contains(e.Instance)),
    distinct_identities = g.Select(e => $"{e.SerialNumber}|{e.SerialText}").Distinct().Count(),
    serial_number_present = g.All(e => e.SerialNumber != 0),
    serial_text_present = g.All(e => !string.IsNullOrEmpty(e.SerialText)),
}).ToList();

Console.WriteLine(JsonSerializer.Serialize(new { active, remembered = byModel },
    new JsonSerializerOptions { WriteIndented = true }));
return 0;

/// One EDID from the registry, parsed.
sealed class Edid
{
    public required string Instance, Model, Name, SerialText;
    public uint SerialNumber;

    public static string Pnp(ushort id)  // three 5-bit letters, big-endian in the EDID
    {
        ushort v = (ushort)((id >> 8) | (id << 8));
        return $"{(char)('A' - 1 + ((v >> 10) & 31))}{(char)('A' - 1 + ((v >> 5) & 31))}{(char)('A' - 1 + (v & 31))}";
    }

    /// \\?\DISPLAY#MODEL#INSTANCE#{guid}  ->  MODEL\INSTANCE
    public static string? InstanceFromPath(string path)
    {
        var parts = path.Split('#');
        return parts.Length >= 3 ? $"{parts[1]}\\{parts[2]}" : null;
    }

    public static Edid? Read(string instance)
    {
        using var k = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\DISPLAY\{instance}\Device Parameters");
        if (k?.GetValue("EDID") is not byte[] b || b.Length < 128) return null;
        string mfr = $"{(char)('A' - 1 + ((b[8] >> 2) & 31))}{(char)('A' - 1 + (((b[8] & 3) << 3) | (b[9] >> 5)))}{(char)('A' - 1 + (b[9] & 31))}";
        string name = "", serialText = "";
        for (int d = 54; d <= 108; d += 18)
        {
            if (b[d] != 0 || b[d + 1] != 0 || b[d + 2] != 0) continue;
            string text = Encoding.ASCII.GetString(b, d + 5, 13).Split('\n')[0].Trim();
            if (b[d + 3] == 0xFC) name = text;
            if (b[d + 3] == 0xFF) serialText = text;
        }
        return new Edid
        {
            Instance = instance, Model = $"{mfr}{BitConverter.ToUInt16(b, 10):X4}", Name = name,
            SerialNumber = BitConverter.ToUInt32(b, 12), SerialText = serialText,
        };
    }
}

static class Native
{
    [StructLayout(LayoutKind.Sequential)] public struct LUID { public uint low; public int high; }

    [StructLayout(LayoutKind.Sequential)]
    public struct PATH  // DISPLAYCONFIG_PATH_INFO, 72 bytes
    {
        public LUID sourceAdapter; public uint sourceId, sourceModeIdx, sourceStatus;
        public LUID targetAdapter; public uint targetId, targetModeIdx;
        public int outputTechnology; public uint rotation, scaling, refreshNum, refreshDen, scanline;
        public int targetAvailable; public uint targetStatus;
        public uint flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HEADER { public int type, size; public LUID adapterId; public uint id; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct SOURCE_NAME
    {
        public HEADER header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string viewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct TARGET_NAME
    {
        public HEADER header;
        public uint flags;
        public int outputTechnology;
        public ushort edidManufactureId, edidProductCodeId;
        public uint connectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string monitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string monitorDevicePath;
    }

    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int left, top, right, bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFOEX
    {
        public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    public delegate bool MonitorEnum(IntPtr hmon, IntPtr hdc, IntPtr rect, IntPtr data);

    [DllImport("user32.dll")] public static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);
    [DllImport("user32.dll")] public static extern int QueryDisplayConfig(uint flags, ref uint np, [Out] PATH[] paths, ref uint nm, [Out] byte[] modes, IntPtr topology);
    [DllImport("user32.dll")] public static extern int DisplayConfigGetDeviceInfo(ref SOURCE_NAME info);
    [DllImport("user32.dll")] public static extern int DisplayConfigGetDeviceInfo(ref TARGET_NAME info);
    [DllImport("user32.dll")] public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnum cb, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool GetMonitorInfoW(IntPtr hmon, ref MONITORINFOEX mi);
    [DllImport("shcore.dll")] public static extern int GetDpiForMonitor(IntPtr hmon, int type, out uint x, out uint y);
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(nint value);

    public static string Technology(int t) => t switch
    {
        unchecked((int)0x80000000) => "internal", 0 => "VGA", 4 => "DVI", 5 => "HDMI", 6 => "LVDS",
        10 => "DisplayPort", 11 => "embedded DisplayPort", 15 => "Miracast", 16 => "indirect (wired)",
        17 => "indirect (virtual)", 18 => "DisplayPort over USB-C", _ => $"other ({t})",
    };
}
