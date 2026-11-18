using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Xml;
using Microsoft.Win32;

namespace Presenter.Slides;

/// <summary>
/// PowerPoint decks. A <c>.pptx</c> (and its macro and show variants) is
/// inspected from its XML without starting PowerPoint; a binary <c>.ppt</c>
/// needs PowerPoint itself.
/// </summary>
internal static class PowerPointDeck
{
    public static readonly string[] OpenXmlExtensions = [".pptx", ".pptm", ".ppsx", ".ppsm", ".potx", ".potm"];
    public static readonly string[] BinaryExtensions = [".ppt", ".pps", ".pot"];

    public static bool IsOpenXml(string path) =>
        OpenXmlExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>Slide shape and the number of slides PowerPoint's show would
    /// present (hidden ones skipped), from <c>ppt/presentation.xml</c>.</summary>
    public static (double Aspect, int Visible) InspectOpenXml(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        const string p = "http://schemas.openxmlformats.org/presentationml/2006/main";
        const string r = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        var pres = Load(zip, "ppt/presentation.xml");
        var ns = new XmlNamespaceManager(pres.NameTable);
        ns.AddNamespace("p", p);
        var sz = (XmlElement?)pres.SelectSingleNode("/p:presentation/p:sldSz", ns)
                 ?? throw new InvalidDataException("no slide size in ppt/presentation.xml");
        double cx = double.Parse(sz.GetAttribute("cx"), System.Globalization.CultureInfo.InvariantCulture);
        double cy = double.Parse(sz.GetAttribute("cy"), System.Globalization.CultureInfo.InvariantCulture);

        var rels = Load(zip, "ppt/_rels/presentation.xml.rels");
        var targets = new Dictionary<string, string>();
        foreach (XmlElement rel in rels.DocumentElement!.ChildNodes.OfType<XmlElement>())
            targets[rel.GetAttribute("Id")] = rel.GetAttribute("Target");

        int visible = 0;
        foreach (XmlElement id in pres.SelectNodes("/p:presentation/p:sldIdLst/p:sldId", ns)!.OfType<XmlElement>())
        {
            if (!targets.TryGetValue(id.GetAttribute("id", r), out var target)) continue;
            var part = target.StartsWith('/') ? target[1..] : "ppt/" + target;
            if (!SlideHidden(zip, part)) visible++;
        }
        return (cx / cy, visible);
    }

    private static XmlDocument Load(ZipArchive zip, string part)
    {
        var entry = zip.GetEntry(part) ?? throw new InvalidDataException($"{part} missing: not a PowerPoint file?");
        var doc = new XmlDocument { XmlResolver = null };
        using var s = entry.Open();
        using var reader = XmlReader.Create(s, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
        doc.Load(reader);
        return doc;
    }

    /// <summary>A slide's <c>show="0"</c>: hidden in the show.</summary>
    private static bool SlideHidden(ZipArchive zip, string part)
    {
        var entry = zip.GetEntry(part);
        if (entry is null) return false;
        using var s = entry.Open();
        using var reader = XmlReader.Create(s, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
        reader.MoveToContent();
        var show = reader.GetAttribute("show");
        return show is "0" or "false";
    }

    /// <summary>The registered PowerPoint version (e.g. "16"), for the cache key; no start needed.</summary>
    public static string? RegisteredVersion()
    {
        using var key = Registry.ClassesRoot.OpenSubKey(@"PowerPoint.Application\CurVer");
        return (key?.GetValue(null) as string)?.Replace("PowerPoint.Application.", "", StringComparison.Ordinal);
    }
}

/// <summary>
/// PowerPoint through late-bound COM (no interop assembly), always without a
/// window. PID-safe: if PowerPoint was already running, the session uses that
/// instance and never quits it; it quits only an instance it started, and only
/// if nobody else has opened anything in it or made it visible meanwhile. It
/// never kills a PowerPoint process. All calls run on one STA thread of its
/// own.
/// </summary>
internal sealed class PowerPointSession : IDisposable
{
    private const int MsoTrue = -1, MsoFalse = 0, PpAlertsNone = 1;
    private readonly dynamic _app;
    private readonly bool _started;
    private readonly int? _ourPid;
    private readonly object? _oldAlerts;

    private PowerPointSession(dynamic app, bool started, int? ourPid)
    {
        _app = app;
        _started = started;
        _ourPid = ourPid;
        try
        {
            _oldAlerts = app.DisplayAlerts;
            app.DisplayAlerts = PpAlertsNone; // no dialog may wait for a click
        }
        catch (COMException)
        {
            _oldAlerts = null;
        }
    }

    /// <summary>True when this session started PowerPoint (and so will quit it).</summary>
    public bool StartedPowerPoint => _started;

    /// <summary>The process id of the PowerPoint this session started, if any.</summary>
    internal int? StartedPid => _ourPid;

    /// <summary>The application object (tests build their decks with it).</summary>
    internal dynamic App => _app;

    private static HashSet<int> PowerPointPids()
    {
        var pids = new HashSet<int>();
        foreach (var p in Process.GetProcessesByName("POWERPNT"))
        {
            pids.Add(p.Id);
            p.Dispose();
        }
        return pids;
    }

    /// <summary>One session at a time in this process: PowerPoint is a single
    /// instance, and a session that attached to another's instance (and so
    /// would not quit it) while that one decided it was still in use (and so
    /// did not quit it either) would leave a hidden PowerPoint running.</summary>
    private static readonly SemaphoreSlim OneAtATime = new(1, 1);

    /// <summary>
    /// Runs <paramref name="work"/> with a session on a dedicated STA thread.
    /// The task completes after the session has ended (PowerPoint closed, or
    /// quit if the session started it), so the next session never finds it
    /// still on its way out.
    /// </summary>
    public static Task<T> RunAsync<T>(Func<PowerPointSession, T> work, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                OneAtATime.Wait(ct);
                try
                {
                    T result;
                    using (var session = Open()) result = work(session);
                    tcs.TrySetResult(result);
                }
                finally
                {
                    OneAtATime.Release();
                }
            }
            catch (OperationCanceledException oce)
            {
                tcs.TrySetCanceled(oce.CancellationToken);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "PowerPoint",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }

    private static PowerPointSession Open()
    {
        var type = Type.GetTypeFromProgID("PowerPoint.Application")
                   ?? throw new InvalidOperationException("PowerPoint is not installed (no PowerPoint.Application COM class).");
        // Ours only if a new PowerPoint process appeared for this call. Not
        // "none was running before": an instance still exiting (PowerPoint can
        // take seconds after Quit) has a process, yet COM starts a new one.
        // A user starting PowerPoint at this very moment is still safe: the
        // quit check finds their window or presentation and leaves it.
        var before = PowerPointPids();
        dynamic app = Activator.CreateInstance(type)
                      ?? throw new InvalidOperationException("PowerPoint did not start.");
        var appeared = PowerPointPids();
        appeared.ExceptWith(before);
        int? ours = appeared.Count == 1 ? appeared.First() : null;
        return new PowerPointSession(app, ours is not null, ours);
    }

    /// <summary>
    /// Opens a presentation read-only and without a window. The deck is
    /// opened from a private copy, so closing it can never close a window
    /// the user has open on the same file.
    /// </summary>
    public Presentation Open(string deckPath, string workDir)
    {
        Directory.CreateDirectory(workDir);
        var copy = Path.Combine(workDir, "deck" + Path.GetExtension(deckPath));
        File.Copy(deckPath, copy, overwrite: true);
        dynamic presentations = _app.Presentations;
        try
        {
            // FileName, ReadOnly, Untitled, WithWindow
            dynamic pres = presentations.Open(copy, MsoTrue, MsoFalse, MsoFalse);
            return new Presentation(pres);
        }
        finally
        {
            Release(presentations);
        }
    }

    internal static void Release(object? o)
    {
        if (o is not null && Marshal.IsComObject(o)) Marshal.ReleaseComObject(o);
    }

    /// <summary>Why the last session quit PowerPoint or left it running (diagnostics).</summary>
    internal static string LastDecision { get; private set; } = "";

    private string Decision
    {
        get => LastDecision;
        set => LastDecision = value;
    }

    public void Dispose()
    {
        bool quit = false;
        try
        {
            if (_oldAlerts is not null) _app.DisplayAlerts = _oldAlerts;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException
                                       or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            // restoring a setting must never keep us from quitting below
        }
        try
        {
            if (!_started)
            {
                Decision = "left running: it was running before";
            }
            else
            {
                // quit only if nobody has started using our instance meanwhile
                dynamic presentations = _app.Presentations;
                int open = Convert.ToInt32((object)presentations.Count);
                Release(presentations);
                bool visible = Convert.ToInt32((object)_app.Visible) == MsoTrue;
                Decision = $"started by us; {open} presentations open, visible {visible}";
                if (open == 0 && !visible)
                {
                    _app.Quit();
                    quit = true;
                    Decision += ": quit";
                }
            }
        }
        catch (COMException ex)
        {
            Decision = $"PowerPoint did not answer ({ex.HResult:X8})"; // it went away by itself
        }
        finally
        {
            Release(_app);
        }
        if (quit)
        {
            // PowerPoint exits only once every reference to it is released:
            // collect any the work left behind (on this STA thread, which
            // pumps while it waits for the finalizers)
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            if (_ourPid is { } pid) ExitedWithin(pid, 10_000); // wait briefly; never kill it
        }
    }

    /// <summary>True if the process has exited (or exits within the time).</summary>
    internal static bool ExitedWithin(int pid, int ms)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.WaitForExit(ms);
        }
        catch (ArgumentException)
        {
            return true; // already gone
        }
    }

    /// <summary>An open presentation.</summary>
    public sealed class Presentation : IDisposable
    {
        private readonly dynamic _pres;

        public Presentation(dynamic pres) => _pres = pres;

        public double Aspect
        {
            get
            {
                dynamic setup = _pres.PageSetup;
                try
                {
                    return Convert.ToDouble((object)setup.SlideWidth) / Convert.ToDouble((object)setup.SlideHeight);
                }
                finally
                {
                    Release(setup);
                }
            }
        }

        /// <summary>1-based indices of the slides the show presents (not hidden).</summary>
        public List<int> VisibleSlides()
        {
            var list = new List<int>();
            dynamic slides = _pres.Slides;
            try
            {
                int n = Convert.ToInt32((object)slides.Count);
                for (int i = 1; i <= n; i++)
                {
                    dynamic slide = slides.Item(i);
                    dynamic transition = slide.SlideShowTransition;
                    try
                    {
                        if (Convert.ToInt32((object)transition.Hidden) != MsoTrue) list.Add(i);
                    }
                    finally
                    {
                        Release(transition);
                        Release(slide);
                    }
                }
            }
            finally
            {
                Release(slides);
            }
            return list;
        }

        /// <summary><c>Slide.Export</c>: scales the slide to exactly width x height.</summary>
        public void Export(int index, string file, int width, int height)
        {
            dynamic slides = _pres.Slides;
            dynamic slide = slides.Item(index);
            try
            {
                slide.Export(file, "PNG", width, height);
            }
            finally
            {
                Release(slide);
                Release(slides);
            }
        }

        public void Dispose()
        {
            try
            {
                _pres.Saved = MsoTrue; // close without saving, without asking
                _pres.Close();
            }
            catch (COMException)
            {
            }
            finally
            {
                Release(_pres);
            }
        }
    }
}
