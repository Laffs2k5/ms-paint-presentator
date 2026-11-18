using System.Text.Json;
using Presenter.Core;
using Presenter.Ui;
using Presenter.Windows;

namespace Presenter.Cli;

/// <summary>The displays as the screen picker offers them, and the speaker's
/// last choice, remembered by monitor identity (E26).</summary>
internal static class ScreenOptions
{
    public static IReadOnlyList<ScreenOption> Now()
    {
        var ids = MonitorIdentity.Active();
        return Monitors.All().Select(m =>
        {
            ids.TryGetValue(m.Device, out var id);
            return new ScreenOption(m.Device, id?.FriendlyName ?? m.Device, id?.Connection ?? "display", id?.Internal ?? false,
                                    m.Primary, m.Bounds.Size, m.ScalePercent, SlideGeometry.PredictedViewport(m.WorkArea.Size, m.Dpi),
                                    id?.Identity ?? m.Device, m.Bounds);
        }).ToList();
    }

    private static string SettingsFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                                       "PaintPresentator", "settings.json");

    private sealed record Settings(string? LastScreen);

    public static string? Remembered()
    {
        try { return File.Exists(SettingsFile) ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsFile))?.LastScreen : null; }
        catch (JsonException) { return null; }
    }

    public static void Remember(ScreenOption choice)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
        File.WriteAllText(SettingsFile, JsonSerializer.Serialize(new Settings(choice.Identity)));
    }

    /// <summary>The picker, then the chosen display as a <see cref="Presenter.Windows.Monitor"/>.</summary>
    public static async Task<(ScreenOption Option, Presenter.Windows.Monitor Monitor)?> Pick(double aspect, string heading, CancellationToken ct = default)
    {
        var choice = await ScreenPicker.ChooseAsync(Now, Remembered(), aspect, heading, PreflightNotes(), ct);
        if (choice is null) return null;
        Remember(choice);
        var monitor = Monitors.All().FirstOrDefault(m => m.Device == choice.Device)
                      ?? throw new InvalidOperationException($"{choice.Device} is gone");
        return (choice, monitor);
    }

    /// <summary>What the speaker should check before the show (E19, E6).</summary>
    public static List<string> PreflightNotes()
    {
        var notes = new List<string> { "Switch Do not disturb on, or notifications will show over the slides." };
        if (!Power.OnMains()) notes.Add("On battery: plug in the charger, Paint is slower and less steady without it.");
        return notes;
    }

    /// <summary>The option for a monitor chosen without the picker.</summary>
    public static ScreenOption For(Presenter.Windows.Monitor m) => Now().First(o => o.Device == m.Device);
}

internal static class PickCommand
{
    public static async Task<int> Run(string[] args)
    {
        double aspect = args.Length > 0 ? double.Parse(args[0]) : 16.0 / 9;
        var picked = await ScreenOptions.Pick(aspect, "Where do you present?");
        if (picked is null)
        {
            Console.WriteLine("cancelled");
            return 1;
        }
        Console.WriteLine($"{picked.Value.Option} -> slides {picked.Value.Option.SlideFor(aspect)}");
        return 0;
    }
}
