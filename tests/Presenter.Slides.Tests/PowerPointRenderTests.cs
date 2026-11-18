using System.Diagnostics;
using Presenter.Core;
using Xunit.Abstractions;

namespace Presenter.Slides.Tests;

/// <summary>
/// PowerPoint through COM, always without a window, with decks the test
/// builds itself (as poc/experiments/e25_legacy_ppt.py does). Few, short
/// PowerPoint runs. Skipped without PowerPoint.
/// </summary>
[Collection("renders")]
public class PowerPointRenderTests(ITestOutputHelper output)
{
    private static readonly PixelSize Viewport = new(2784, 1499);

    [RequiresFact(Tool.PowerPoint)]
    public async Task PowerPoint_decks_render_at_exact_sizes_with_hidden_slides_skipped()
    {
        using var dir = new TempDir();
        var clock = Stopwatch.StartNew();
        int? builder = null;
        var (wide, old, oldPpt) = await PowerPointSession.RunAsync(s =>
        {
            builder = s.StartedPid;
            return Build(s, dir.Path);
        }, default);
        output.WriteLine($"decks built in {clock.Elapsed.TotalSeconds:0.0} s (PowerPoint pid {builder}; {PowerPointSession.LastDecision})");
        // the session quit the PowerPoint it started; PowerPoint can take more
        // than the session's 10 s wait to exit, but it must go by itself
        Assert.Contains(": quit", PowerPointSession.LastDecision, StringComparison.Ordinal);
        if (builder is { } pid) Assert.True(PowerPointSession.ExitedWithin(pid, 30_000), "the builder's PowerPoint did not exit");

        // inspection: .pptx from its XML, .ppt through PowerPoint
        var wideDeck = Decks.Inspect(wide);
        Assert.Equal((DeckKind.PowerPoint, 4), (wideDeck.Kind, wideDeck.SlideCount)); // 5 slides, 1 hidden
        Assert.Equal(16.0 / 9, wideDeck.Aspect, 3);
        var oldDeck = Decks.Inspect(old);
        Assert.Equal(4.0 / 3, oldDeck.Aspect, 3);
        Assert.Equal(3, oldDeck.SlideCount);
        clock.Restart();
        var pptDeck = Decks.Inspect(oldPpt);
        output.WriteLine($".ppt inspected through PowerPoint in {clock.Elapsed.TotalSeconds:0.00} s");
        Assert.Equal((DeckKind.PowerPoint, 3), (pptDeck.Kind, pptDeck.SlideCount));
        Assert.Equal(4.0 / 3, pptDeck.Aspect, 3);

        var options = new RenderOptions(Path.Combine(dir.Path, "cache"));
        foreach (var size in new[] { new PixelSize(1920, 1080), SlideGeometry.SlideSize(Viewport, wideDeck.Aspect) })
        {
            var run = await RenderChecks.RunAsync(wideDeck, size, options);
            output.WriteLine($"pptx 16:9 {size}: {run}");
            Assert.Equal(4, run.TotalAtStart);
            Assert.Equal(4, run.Done);
            var again = await RenderChecks.RunAsync(wideDeck, size, options);
            output.WriteLine($"pptx 16:9 {size} again: {again}");
            Assert.True(again.FromCache);
        }

        // the old .ppt (4:3) at the size the presenter would ask for on a 16:9 screen
        var fitted = SlideGeometry.SlideSize(Viewport, pptDeck.Aspect);
        var ppt = await RenderChecks.RunAsync(pptDeck, fitted, options);
        output.WriteLine($"ppt 4:3 {fitted}: {ppt}");
        Assert.Equal(3, ppt.Done);
        AssertNoPowerPointLeft();
    }

    [RequiresFact(Tool.PowerPoint)]
    public async Task A_powerpoint_already_running_is_never_quit()
    {
        using var dir = new TempDir();
        var (_, old, _) = await PowerPointSession.RunAsync(s => Build(s, dir.Path), default);
        var deck = Decks.Inspect(old);
        // a PowerPoint held by someone else (standing in for the user's) is
        // running while the render runs: the render must leave it running
        RenderRun run;
        int userPid;
        using (var user = new StandInPowerPoint())
        {
            Assert.Null(user.Error);
            userPid = user.Pid;
            run = await RenderChecks.RunAsync(deck, new PixelSize(1920, 1080), new RenderOptions(Path.Combine(dir.Path, "cache")));
            output.WriteLine($"4:3 pptx at 1920x1080 while another PowerPoint runs: {run}; {PowerPointSession.LastDecision}");
            if (userPid != 0) Assert.True(IsRunning(userPid), "the render quit a PowerPoint it did not start");
        }
        Assert.Equal(3, run.Done); // padded left and right, never stretched
        Assert.Contains(run.Notes, n => n.Contains("already running", StringComparison.Ordinal));
        if (userPid != 0) Assert.True(PowerPointSession.ExitedWithin(userPid, 15_000), "the stand-in's PowerPoint did not quit");
        AssertNoPowerPointLeft();
    }

    /// <summary>PowerPoint started and held by plain COM on a thread of its
    /// own, as the user's own instance would be; quit on dispose.</summary>
    private sealed class StandInPowerPoint : IDisposable
    {
        private readonly ManualResetEventSlim _started = new(), _stop = new();
        private readonly Thread _thread;

        /// <summary>The stand-in's process, or 0 if a PowerPoint was already running.</summary>
        public int Pid { get; private set; }

        public Exception? Error { get; private set; }

        public StandInPowerPoint()
        {
            _thread = new Thread(() =>
            {
                try
                {
                    var before = Process.GetProcessesByName("POWERPNT").Select(p => p.Id).ToHashSet();
                    dynamic app = Activator.CreateInstance(Type.GetTypeFromProgID("PowerPoint.Application")!)!;
                    Pid = Process.GetProcessesByName("POWERPNT").Select(p => p.Id).Except(before).FirstOrDefault();
                    _started.Set();
                    _stop.Wait();
                    if (Pid != 0) app.Quit(); // never quit one that was there before the test
                    System.Runtime.InteropServices.Marshal.FinalReleaseComObject(app);
                }
                catch (Exception ex)
                {
                    Error = ex;
                    _started.Set();
                }
            });
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            _started.Wait(TimeSpan.FromSeconds(30));
        }

        public void Dispose()
        {
            _stop.Set();
            _thread.Join(TimeSpan.FromSeconds(30));
        }
    }

    private static bool IsRunning(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Only checks when no PowerPoint ran before the tests (then any
    /// left is one of ours that failed to quit).</summary>
    private void AssertNoPowerPointLeft()
    {
        var left = Process.GetProcessesByName("POWERPNT");
        output.WriteLine($"POWERPNT processes after the test: {left.Length}");
        foreach (var p in left) p.Dispose();
        if (!PowerPointWasRunning) Assert.Empty(left);
    }

    /// <summary>Whether a PowerPoint (the user's) ran before these tests, taken
    /// when the class is first used (the static constructor makes that eager).</summary>
    private static readonly bool PowerPointWasRunning = Process.GetProcessesByName("POWERPNT").Length > 0;

    static PowerPointRenderTests()
    {
    }

    /// <summary>
    /// A 16:9 deck of 5 slides (slide 3 hidden; slide 2 with an entrance
    /// animation), and a 4:3 deck of 4 slides (slide 2 hidden) saved as
    /// .pptx and as .ppt (PowerPoint 97-2003). Text made up for the test.
    /// </summary>
    internal static (string Wide, string Old, string OldPpt) Build(PowerPointSession session, string dir)
    {
        var wide = Path.Combine(dir, "wide.pptx");
        var old = Path.Combine(dir, "old.pptx");
        var oldPpt = Path.Combine(dir, "old.ppt");
        dynamic app = session.App;
        MakeDeck(app, 960, 540, 5, 3, ("wide", wide, 24));
        MakeDeck(app, 720, 540, 4, 2, ("old", old, 24), ("old", oldPpt, 1));
        return (wide, old, oldPpt);
    }

    private static void MakeDeck(dynamic app, float w, float h, int slides, int hidden, params (string Name, string Path, int Format)[] saves)
    {
        dynamic presentations = app.Presentations;
        dynamic pres = presentations.Add(0); // msoFalse: no window
        try
        {
            dynamic setup = pres.PageSetup;
            setup.SlideWidth = w;
            setup.SlideHeight = h;
            dynamic layouts = pres.SlideMaster.CustomLayouts;
            dynamic list = pres.Slides;
            for (int i = 1; i <= slides; i++)
            {
                dynamic s = list.AddSlide(i, layouts.Item(i == 1 ? 1 : 2));
                s.Shapes.Title.TextFrame.TextRange.Text = $"Test slide {i}";
                if (i == 2)
                {
                    dynamic box = s.Shapes.AddShape(1, w * 0.6f, h * 0.6f, w * 0.2f, h * 0.2f);
                    box.Fill.ForeColor.RGB = 0x2020D0;
                    s.TimeLine.MainSequence.AddEffect(box, 1, 0, 1); // Appear, on click: exported in place
                }
                if (i == hidden) s.SlideShowTransition.Hidden = -1;
            }
            foreach (var (_, path, format) in saves) pres.SaveAs(path, format);
        }
        finally
        {
            pres.Close();
        }
    }
}
