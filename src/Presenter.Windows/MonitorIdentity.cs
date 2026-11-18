using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace Presenter.Windows;

/// <summary>Who a display is, beyond its GDI name (E26).</summary>
/// <param name="Identity">A stable key: the EDID's manufacturer, product,
/// serial number and serial text, hashed; the model plus the device path
/// (the port) when the EDID has no serial.</param>
public sealed record DisplayIdentity(string Device, string FriendlyName, string Connection, bool Internal, string Identity);

/// <summary>
/// The EDID identity Windows keeps in the registry for every monitor, through
/// QueryDisplayConfig (E26): it belongs to the monitor, not to the port or
/// to Windows' numbering, and told apart even two monitors of one model on
/// this laptop. The connection type marks the laptop's own panel.
/// </summary>
public static class MonitorIdentity
{
    public static IReadOnlyDictionary<string, DisplayIdentity> Active()
    {
        var found = new Dictionary<string, DisplayIdentity>(StringComparer.OrdinalIgnoreCase);
        if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out uint np, out uint nm) != 0) return found;
        var paths = new PATH[np];
        var modes = new byte[Math.Max(1, nm) * 64];
        if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref np, paths, ref nm, modes, 0) != 0) return found;
        for (int i = 0; i < np; i++)
        {
            var p = paths[i];
            var src = new SOURCE_NAME { header = new() { type = 1, size = Marshal.SizeOf<SOURCE_NAME>(), adapterId = p.sourceAdapter, id = p.sourceId } };
            var tgt = new TARGET_NAME { header = new() { type = 2, size = Marshal.SizeOf<TARGET_NAME>(), adapterId = p.targetAdapter, id = p.targetId } };
            if (DisplayConfigGetDeviceInfo(ref src) != 0 || DisplayConfigGetDeviceInfo(ref tgt) != 0) continue;
            string? instance = InstanceFromPath(tgt.monitorDevicePath);
            var edid = instance is null ? null : ReadEdid(instance);
            string key = edid is { } e && (e.Serial != 0 || e.SerialText.Length > 0)
                ? $"{e.Model}|{e.Serial}|{e.SerialText}"
                : $"{edid?.Model ?? tgt.edidProductCodeId.ToString("X4")}|{tgt.monitorDevicePath}";
            bool builtIn = tgt.outputTechnology == unchecked((int)0x80000000);
            string name = !string.IsNullOrWhiteSpace(tgt.monitorFriendlyDeviceName) ? tgt.monitorFriendlyDeviceName
                          : edid?.Name is { Length: > 0 } n ? n : builtIn ? "Built-in screen" : "Display";
            found[src.viewGdiDeviceName] = new DisplayIdentity(src.viewGdiDeviceName, name, Technology(tgt.outputTechnology), builtIn, Hash(key));
        }
        return found;
    }

    private static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..16];

    private sealed record Edid(string Model, string Name, uint Serial, string SerialText);

    /// <summary>\\?\DISPLAY#MODEL#INSTANCE#{guid} to MODEL\INSTANCE.</summary>
    private static string? InstanceFromPath(string path)
    {
        var parts = path.Split('#');
        return parts.Length >= 3 ? $"{parts[1]}\\{parts[2]}" : null;
    }

    private static Edid? ReadEdid(string instance)
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
        return new Edid($"{mfr}{BitConverter.ToUInt16(b, 10):X4}", name, BitConverter.ToUInt32(b, 12), serialText);
    }

    private static string Technology(int t) => t switch
    {
        unchecked((int)0x80000000) => "built-in", 0 => "VGA", 4 => "DVI", 5 => "HDMI", 6 => "LVDS",
        10 => "DisplayPort", 11 => "built-in DisplayPort", 15 => "Miracast", 16 => "wired (indirect)",
        17 => "virtual", 18 => "USB-C", _ => "other",
    };

    // --- QueryDisplayConfig ---------------------------------------------------------------------
    private const uint QDC_ONLY_ACTIVE_PATHS = 2;

    [StructLayout(LayoutKind.Sequential)] private struct LUID { public uint low; public int high; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PATH  // DISPLAYCONFIG_PATH_INFO, 72 bytes
    {
        public LUID sourceAdapter; public uint sourceId, sourceModeIdx, sourceStatus;
        public LUID targetAdapter; public uint targetId, targetModeIdx;
        public int outputTechnology; public uint rotation, scaling, refreshNum, refreshDen, scanline;
        public int targetAvailable; public uint targetStatus;
        public uint flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HEADER { public int type, size; public LUID adapterId; public uint id; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SOURCE_NAME
    {
        public HEADER header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string viewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TARGET_NAME
    {
        public HEADER header;
        public uint flags;
        public int outputTechnology;
        public ushort edidManufactureId, edidProductCodeId;
        public uint connectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string monitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string monitorDevicePath;
    }

    [DllImport("user32.dll")] private static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);
    [DllImport("user32.dll")] private static extern int QueryDisplayConfig(uint flags, ref uint np, [Out] PATH[] paths, ref uint nm, [Out] byte[] modes, nint topology);
    [DllImport("user32.dll")] private static extern int DisplayConfigGetDeviceInfo(ref SOURCE_NAME info);
    [DllImport("user32.dll")] private static extern int DisplayConfigGetDeviceInfo(ref TARGET_NAME info);
}
