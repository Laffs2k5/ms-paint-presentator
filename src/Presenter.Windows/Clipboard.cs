using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Windows.Forms;
using ComIDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;
using static Presenter.Windows.Native;

namespace Presenter.Windows;

/// <summary>
/// A live clipboard object holding one slide (E1, E20): the PNG, which Paint
/// reads within milliseconds of Ctrl+V, plus
/// ExcludeClipboardContentFromMonitorProcessing so the slide stays out of
/// clipboard history (E8). A new object per slide: Paint pastes a cached
/// picture if the same object is offered again (E7).
/// </summary>
[ComVisible(true)]
internal sealed class SlideDataObject : ComIDataObject
{
    private readonly (short Cf, byte[] Data)[] _formats;

    public SlideDataObject((short, byte[])[] formats) => _formats = formats;

    private static nint ToHGlobal(byte[] data)
    {
        nint h = GlobalAlloc(GMEM_MOVEABLE, (nuint)data.Length);
        nint p = GlobalLock(h);
        Marshal.Copy(data, 0, p, data.Length);
        GlobalUnlock(h);
        return h;
    }

    public void GetData(ref FORMATETC format, out STGMEDIUM medium)
    {
        foreach (var (cf, data) in _formats)
        {
            if (cf == format.cfFormat && (format.tymed & TYMED.TYMED_HGLOBAL) != 0)
            {
                medium = new STGMEDIUM { tymed = TYMED.TYMED_HGLOBAL, unionmember = ToHGlobal(data) };
                return;
            }
        }
        throw new COMException("format not offered", DV_E_FORMATETC);
    }

    public void GetDataHere(ref FORMATETC format, ref STGMEDIUM medium) =>
        throw new COMException("not implemented", E_NOTIMPL);

    public int QueryGetData(ref FORMATETC format)
    {
        foreach (var (cf, _) in _formats)
            if (cf == format.cfFormat && (format.tymed & TYMED.TYMED_HGLOBAL) != 0)
                return S_OK;
        return DV_E_FORMATETC;
    }

    public int GetCanonicalFormatEtc(ref FORMATETC formatIn, out FORMATETC formatOut)
    {
        formatOut = default;
        return E_NOTIMPL;
    }

    public void SetData(ref FORMATETC formatIn, ref STGMEDIUM medium, bool release) =>
        throw new COMException("not implemented", E_NOTIMPL);

    public IEnumFORMATETC EnumFormatEtc(DATADIR direction)
    {
        if (direction != DATADIR.DATADIR_GET) throw new COMException("not implemented", E_NOTIMPL);
        return new FormatEnum(_formats.Select(f => new FORMATETC
        {
            cfFormat = f.Cf, dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = -1, tymed = TYMED.TYMED_HGLOBAL,
        }).ToArray());
    }

    public int DAdvise(ref FORMATETC pFormatetc, ADVF advf, IAdviseSink adviseSink, out int connection)
    {
        connection = 0;
        return OLE_E_ADVISENOTSUPPORTED;  // nothing calls it (E5)
    }

    public void DUnadvise(int connection) => throw new COMException("not supported", OLE_E_ADVISENOTSUPPORTED);

    public int EnumDAdvise(out IEnumSTATDATA enumAdvise)
    {
        enumAdvise = null!;
        return OLE_E_ADVISENOTSUPPORTED;
    }

    [ComVisible(true)]
    private sealed class FormatEnum(FORMATETC[] items) : IEnumFORMATETC
    {
        private int _pos;

        public int Next(int celt, FORMATETC[] rgelt, int[]? pceltFetched)
        {
            int n = 0;
            while (n < celt && _pos < items.Length) rgelt[n++] = items[_pos++];
            if (pceltFetched is { Length: > 0 }) pceltFetched[0] = n;
            return n == celt ? S_OK : S_FALSE;
        }

        public int Skip(int celt)
        {
            _pos = Math.Min(items.Length, _pos + celt);
            return _pos < items.Length ? S_OK : S_FALSE;
        }

        public int Reset() { _pos = 0; return S_OK; }
        public void Clone(out IEnumFORMATETC newEnum) => newEnum = new FormatEnum(items) { _pos = _pos };
    }
}

/// <summary>
/// The clipboard thread (research 05 §2): an STA thread that always pumps
/// messages, because Paint's GetData calls arrive on it. Offering a slide is
/// marshalled onto it; nothing ever blocks it waiting for Paint.
/// </summary>
public sealed class ClipboardHost : IDisposable
{
    private readonly Thread _thread;
    private readonly Form _form;
    private object? _current;  // keeps the offered object alive
    private readonly short _cfPng, _cfExclude;
    private static readonly byte[] ExcludeValue = new byte[4];

    public ClipboardHost()
    {
        _cfPng = (short)RegisterClipboardFormatW("PNG");
        _cfExclude = (short)RegisterClipboardFormatW("ExcludeClipboardContentFromMonitorProcessing");
        Form? form = null;
        using var ready = new ManualResetEventSlim();
        _thread = new Thread(() =>
        {
            Application.OleRequired();  // OleInitialize on this STA thread
            form = new Form { ShowInTaskbar = false, WindowState = FormWindowState.Minimized, Opacity = 0 };
            form.Load += (_, _) => form.Hide();
            form.HandleCreated += (_, _) => ready.Set();
            Application.Run(form);
        }) { IsBackground = true, Name = "clipboard" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        if (!ready.Wait(5000)) throw new InvalidOperationException("the clipboard thread did not start");
        _form = form!;
    }

    /// <summary>Put a slide (PNG bytes) on the clipboard as a new live object.</summary>
    public void Offer(byte[] png) => Run(() =>
    {
        var obj = new SlideDataObject([(_cfPng, png), (_cfExclude, ExcludeValue)]);
        int hr = 0;
        for (int i = 0; i < 20; i++)  // another program may hold the clipboard for a moment
        {
            hr = OleSetClipboard(obj);
            if (hr == 0) break;
            Thread.Sleep(25);
        }
        if (hr != 0) throw new COMException("OleSetClipboard failed", hr);
        _current = obj;
    });

    /// <summary>Empty the clipboard if it still holds our object.</summary>
    public void Clear() => Run(() =>
    {
        if (_current is null) return;
        OleSetClipboard(null);
        _current = null;
    });

    private void Run(Action action)
    {
        Exception? error = null;
        _form.Invoke(() =>
        {
            try { action(); }
            catch (Exception e) { error = e; }
        });
        if (error is not null) throw error;
    }

    public void Dispose()
    {
        try
        {
            Clear();
            _form.Invoke(() => _form.Close());
        }
        catch (InvalidOperationException) { }  // the thread is gone already
        _thread.Join(2000);
    }
}
