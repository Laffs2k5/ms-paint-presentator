// E20: a C# live clipboard object for Paint, driven by the Python harness
// (poc/experiments/e20_csharp_clipboard.py).
//
// Commands on stdin, one per line:
//   offer winforms <png>   WinForms DataObject + Clipboard.SetDataObject(obj, copy: false)
//   offer own <png>        our own IDataObject + OleSetClipboard
//   clear                  empty the clipboard
//   quit
// Replies on stdout: "ok <seconds>" or "error <message>". Every call Paint (or
// anyone) makes on our data objects is logged as
//   call <variant> <method> <format> <seconds>
// Seconds come from Stopwatch (QueryPerformanceCounter), the same clock as
// Python's time.perf_counter, so the harness can line them up with its own.
//
// Each slide gets a new data object (E7: Paint pastes a cached PNG if the
// object is reused). Formats: PNG (registered), plus
// ExcludeClipboardContentFromMonitorProcessing (E8) to keep slides out of
// clipboard history.
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using ComIDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;

static class Native
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern uint RegisterClipboardFormatW(string name);

    [DllImport("ole32.dll")]
    public static extern int OleSetClipboard(ComIDataObject? obj);

    [DllImport("kernel32.dll")]
    public static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);

    [DllImport("kernel32.dll")]
    public static extern IntPtr GlobalLock(IntPtr h);

    [DllImport("kernel32.dll")]
    public static extern bool GlobalUnlock(IntPtr h);

    public const uint GMEM_MOVEABLE = 0x0002;
    public const int DV_E_FORMATETC = unchecked((int)0x80040064);
    public const int E_NOTIMPL = unchecked((int)0x80004001);
    public const int OLE_E_ADVISENOTSUPPORTED = unchecked((int)0x80040003);
    public const int S_OK = 0, S_FALSE = 1;

    public static double Now() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    public static IntPtr ToHGlobal(byte[] data)
    {
        var h = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)data.Length);
        var p = GlobalLock(h);
        Marshal.Copy(data, 0, p, data.Length);
        GlobalUnlock(h);
        return h;
    }
}

static class Log
{
    static readonly object Gate = new();

    public static void Line(string s)
    {
        lock (Gate) { Console.Out.WriteLine(s); Console.Out.Flush(); }
    }

    public static string Format(short cf)
    {
        if (cf == Program.CfPng) return "PNG";
        if (cf == Program.CfExclude) return "Exclude";
        return cf switch { 2 => "CF_BITMAP", 8 => "CF_DIB", 17 => "CF_DIBV5", _ => $"cf{(ushort)cf}" };
    }
}

/// WinForms' own DataObject, subclassed only to log the calls that reach it.
sealed class LoggingWinFormsData : DataObject
{
    public override object? GetData(string format, bool autoConvert)
    {
        Log.Line($"call winforms GetData {format} {Native.Now():F6}");
        return base.GetData(format, autoConvert);
    }

    public override bool GetDataPresent(string format, bool autoConvert)
    {
        Log.Line($"call winforms GetDataPresent {format} {Native.Now():F6}");
        return base.GetDataPresent(format, autoConvert);
    }
}

/// Our own IDataObject: HGLOBAL data per format, nothing else (as E1's Python object).
[ComVisible(true)]
sealed class OwnData : ComIDataObject
{
    readonly (short Cf, byte[] Data)[] _formats;

    public OwnData((short, byte[])[] formats) => _formats = formats;

    static void Note(string call, short cf) =>
        Log.Line($"call own {call} {Log.Format(cf)} {Native.Now():F6}");

    public void GetData(ref FORMATETC format, out STGMEDIUM medium)
    {
        Note("GetData", format.cfFormat);
        foreach (var (cf, data) in _formats)
        {
            if (cf == format.cfFormat && (format.tymed & TYMED.TYMED_HGLOBAL) != 0)
            {
                medium = new STGMEDIUM { tymed = TYMED.TYMED_HGLOBAL, unionmember = Native.ToHGlobal(data) };
                return;
            }
        }
        throw new COMException("format not offered", Native.DV_E_FORMATETC);
    }

    public void GetDataHere(ref FORMATETC format, ref STGMEDIUM medium)
    {
        Note("GetDataHere", format.cfFormat);
        throw new COMException("not implemented", Native.E_NOTIMPL);
    }

    public int QueryGetData(ref FORMATETC format)
    {
        Note("QueryGetData", format.cfFormat);
        foreach (var (cf, _) in _formats)
            if (cf == format.cfFormat && (format.tymed & TYMED.TYMED_HGLOBAL) != 0)
                return Native.S_OK;
        return Native.DV_E_FORMATETC;
    }

    public int GetCanonicalFormatEtc(ref FORMATETC formatIn, out FORMATETC formatOut)
    {
        formatOut = default;
        return Native.E_NOTIMPL;
    }

    public void SetData(ref FORMATETC formatIn, ref STGMEDIUM medium, bool release) =>
        throw new COMException("not implemented", Native.E_NOTIMPL);

    public IEnumFORMATETC EnumFormatEtc(DATADIR direction)
    {
        Log.Line($"call own EnumFormatEtc - {Native.Now():F6}");
        if (direction != DATADIR.DATADIR_GET)
            throw new COMException("not implemented", Native.E_NOTIMPL);
        return new FormatEnum(_formats.Select(f => new FORMATETC
        {
            cfFormat = f.Cf, dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = -1, tymed = TYMED.TYMED_HGLOBAL,
        }).ToArray());
    }

    public int DAdvise(ref FORMATETC pFormatetc, ADVF advf, IAdviseSink adviseSink, out int connection)
    {
        connection = 0;
        return Native.OLE_E_ADVISENOTSUPPORTED;
    }

    public void DUnadvise(int connection) =>
        throw new COMException("not supported", Native.OLE_E_ADVISENOTSUPPORTED);

    public int EnumDAdvise(out IEnumSTATDATA enumAdvise)
    {
        enumAdvise = null!;
        return Native.OLE_E_ADVISENOTSUPPORTED;
    }
}

[ComVisible(true)]
sealed class FormatEnum : IEnumFORMATETC
{
    readonly FORMATETC[] _items;
    int _pos;

    public FormatEnum(FORMATETC[] items) => _items = items;

    public int Next(int celt, FORMATETC[] rgelt, int[]? pceltFetched)
    {
        int n = 0;
        while (n < celt && _pos < _items.Length)
            rgelt[n++] = _items[_pos++];
        if (pceltFetched is { Length: > 0 }) pceltFetched[0] = n;
        return n == celt ? Native.S_OK : Native.S_FALSE;
    }

    public int Skip(int celt)
    {
        _pos = Math.Min(_items.Length, _pos + celt);
        return _pos < _items.Length ? Native.S_OK : Native.S_FALSE;
    }

    public int Reset() { _pos = 0; return Native.S_OK; }

    public void Clone(out IEnumFORMATETC newEnum) => newEnum = new FormatEnum(_items) { _pos = _pos };
}

static class Program
{
    public static short CfPng, CfExclude;
    static object? _current;  // keep the offered object alive

    [STAThread]
    static int Main()
    {
        // The machine's culture may write 0,5 for 0.5: keep the protocol invariant.
        System.Globalization.CultureInfo.DefaultThreadCurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        CfPng = (short)Native.RegisterClipboardFormatW("PNG");
        CfExclude = (short)Native.RegisterClipboardFormatW("ExcludeClipboardContentFromMonitorProcessing");
        Application.OleRequired();  // OleInitialize on this STA thread
        using var form = new Form { ShowInTaskbar = false, WindowState = FormWindowState.Minimized };
        form.Load += (_, _) => form.Hide();
        var ui = SynchronizationContext.Current;
        form.HandleCreated += (_, _) =>
        {
            var ctx = SynchronizationContext.Current!;
            new Thread(() => ReadCommands(ctx, form)) { IsBackground = true }.Start();
        };
        Application.Run(form);  // the message loop serves Paint's OLE calls
        return 0;
    }

    static void ReadCommands(SynchronizationContext ctx, Form form)
    {
        string? line;
        while ((line = Console.In.ReadLine()) != null)
        {
            var parts = line.Split(' ', 3);
            if (parts[0] == "quit") break;
            ctx.Send(_ => Run(parts), null);  // on the STA thread
        }
        ctx.Post(_ => { Native.OleSetClipboard(null); form.Close(); }, null);
    }

    static void Run(string[] parts)
    {
        try
        {
            var t0 = Native.Now();
            switch (parts[0])
            {
                case "offer" when parts.Length == 3:
                    var png = File.ReadAllBytes(parts[2]);
                    var exclude = new byte[4];
                    if (parts[1] == "winforms")
                    {
                        var d = new LoggingWinFormsData();
                        d.SetData("PNG", false, new MemoryStream(png));
                        d.SetData("ExcludeClipboardContentFromMonitorProcessing", false, new MemoryStream(exclude));
                        Clipboard.SetDataObject(d, false, 20, 50);
                        _current = d;
                    }
                    else
                    {
                        var d = new OwnData(new[] { (CfPng, png), (CfExclude, exclude) });
                        int hr = 0;
                        for (int i = 0; i < 20; i++)
                        {
                            hr = Native.OleSetClipboard(d);
                            if (hr == 0) break;
                            Thread.Sleep(50);
                        }
                        if (hr != 0) throw new COMException("OleSetClipboard failed", hr);
                        _current = d;
                    }
                    break;
                case "clear":
                    Native.OleSetClipboard(null);
                    _current = null;
                    break;
                default:
                    throw new ArgumentException($"unknown command: {string.Join(' ', parts)}");
            }
            Log.Line($"ok {Native.Now() - t0:F6}");
        }
        catch (Exception e)
        {
            Log.Line($"error {e.GetType().Name}: {e.Message.Replace('\n', ' ')}");
        }
    }
}
