using System.Runtime.InteropServices;
using System.Text;

namespace Presenter.Windows;

/// <summary>The Win32 calls the presenter needs, declared by hand.</summary>
internal static unsafe class Native
{
    // --- keyboard ---------------------------------------------------------------------------
    public const int WH_KEYBOARD_LL = 13;
    public const uint WM_KEYDOWN = 0x0100, WM_SYSKEYDOWN = 0x0104, WM_QUIT = 0x0012;
    public const uint KEYEVENTF_KEYUP = 0x0002, KEYEVENTF_UNICODE = 0x0004;
    public const uint INPUT_KEYBOARD = 1;

    [StructLayout(LayoutKind.Sequential)]
    public struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public nuint dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public nuint dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public nuint dwExtraInfo; }

    [StructLayout(LayoutKind.Explicit)]
    public struct INPUT
    {
        [FieldOffset(0)] public uint type;
        [FieldOffset(8)] public KEYBDINPUT ki;
        [FieldOffset(8)] public MOUSEINPUT mi;  // sizes the union as Windows does
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG { public nint hwnd; public uint message; public nuint wParam; public nint lParam; public uint time; public int x, y; }

    public delegate nint HookProc(int code, nuint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)] public static extern nint SetWindowsHookExW(int id, HookProc proc, nint hmod, uint tid);
    [DllImport("user32.dll")] public static extern nint CallNextHookEx(nint h, int code, nuint w, nint l);
    [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(nint h);
    [DllImport("user32.dll")] public static extern int GetMessageW(out MSG m, nint hwnd, uint min, uint max);
    [DllImport("user32.dll")] public static extern bool PostThreadMessageW(uint tid, uint msg, nuint w, nint l);
    [DllImport("user32.dll", SetLastError = true)] public static extern uint SendInput(uint n, INPUT* inputs, int size);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll")] public static extern nint GetModuleHandleW(nint name);

    // --- windows ----------------------------------------------------------------------------
    public const uint GA_ROOTOWNER = 3;
    public const int SW_RESTORE = 9, SW_MAXIMIZE = 3;
    public const uint SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;
    public const uint WM_CLOSE = 0x0010, WM_COMMAND = 0x0111, WM_KEYUP = 0x0101;
    public const byte VK_MENU = 0x12;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    public delegate bool EnumWindowsProc(nint hwnd, nint lParam);

    [DllImport("user32.dll")] public static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] public static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc proc, nint lParam);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] public static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] public static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] public static extern bool IsZoomed(nint hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextW(nint hwnd, StringBuilder text, int max);
    [DllImport("user32.dll")] public static extern int GetWindowTextLengthW(nint hwnd);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll")] public static extern bool ShowWindow(nint hwnd, int cmd);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int w, int h, uint flags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(nint hwnd, out RECT r);
    [DllImport("user32.dll")] public static extern bool PostMessageW(nint hwnd, uint msg, nuint w, nint l);
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, nuint extra);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] public static extern bool EnumChildWindows(nint parent, EnumWindowsProc proc, nint lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassNameW(nint hwnd, StringBuilder name, int max);
    [DllImport("user32.dll")] public static extern uint MapVirtualKeyW(uint code, uint mapType);

    // --- monitors ---------------------------------------------------------------------------
    public const uint MONITORINFOF_PRIMARY = 1;
    public const int MDT_EFFECTIVE_DPI = 0;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFOEXW
    {
        public int cbSize;
        public RECT rcMonitor, rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    public delegate bool MonitorEnumProc(nint hmon, nint hdc, nint rect, nint data);

    [DllImport("user32.dll")] public static extern bool EnumDisplayMonitors(nint hdc, nint clip, MonitorEnumProc proc, nint data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool GetMonitorInfoW(nint hmon, ref MONITORINFOEXW info);
    [DllImport("shcore.dll")] public static extern int GetDpiForMonitor(nint hmon, int type, out uint dpiX, out uint dpiY);

    // --- processes ----------------------------------------------------------------------------
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)] public static extern nint OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] public static extern bool CloseHandle(nint h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern bool QueryFullProcessImageNameW(nint h, uint flags, StringBuilder name, ref uint size);

    // --- power ---------------------------------------------------------------------------------
    public const uint ES_CONTINUOUS = 0x80000000, ES_SYSTEM_REQUIRED = 1, ES_DISPLAY_REQUIRED = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct SYSTEM_POWER_STATUS { public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag; public uint BatteryLifeTime, BatteryFullLifeTime; }

    [DllImport("kernel32.dll")] public static extern uint SetThreadExecutionState(uint flags);
    [DllImport("kernel32.dll")] public static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

    // --- clipboard -------------------------------------------------------------------------------
    public const uint GMEM_MOVEABLE = 0x0002;
    public const int DV_E_FORMATETC = unchecked((int)0x80040064);
    public const int E_NOTIMPL = unchecked((int)0x80004001);
    public const int OLE_E_ADVISENOTSUPPORTED = unchecked((int)0x80040003);
    public const int S_OK = 0, S_FALSE = 1;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern uint RegisterClipboardFormatW(string name);
    [DllImport("ole32.dll")] public static extern int OleSetClipboard(System.Runtime.InteropServices.ComTypes.IDataObject? obj);
    [DllImport("ole32.dll")] public static extern int OleFlushClipboard();
    [DllImport("kernel32.dll")] public static extern nint GlobalAlloc(uint flags, nuint bytes);
    [DllImport("kernel32.dll")] public static extern nint GlobalLock(nint h);
    [DllImport("kernel32.dll")] public static extern bool GlobalUnlock(nint h);

    public static string WindowText(nint hwnd)
    {
        int n = GetWindowTextLengthW(hwnd);
        var sb = new StringBuilder(n + 1);
        GetWindowTextW(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    public static string? ProcessImage(nint hwnd)
    {
        GetWindowThreadProcessId(hwnd, out uint pid);
        nint h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == 0) return null;
        try
        {
            var sb = new StringBuilder(1024);
            uint size = (uint)sb.Capacity;
            return QueryFullProcessImageNameW(h, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally { CloseHandle(h); }
    }

    public static Presenter.Core.PixelRect Rect(RECT r) => new(r.Left, r.Top, r.Right, r.Bottom);
}
