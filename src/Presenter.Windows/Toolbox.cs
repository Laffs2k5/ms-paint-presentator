using System.Text.Json;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;

namespace Presenter.Windows;

/// <summary>
/// Choosing Paint's tools, brushes, colours and sizes, and driving its Text
/// tool (E15, E16, E27). Keys for tools, the Brushes menu for brushes, pen
/// taps for colour swatches (they have no UIA selection), UIA for sliders and
/// the text controls. Never Edit colors: its dialog blocks UIA (E16).
/// Runs on the stage thread.
/// </summary>
public sealed class Toolbox(PaintSession paint, SyntheticPen pen)
{
    /// <summary>Paint's 20 palette colours (E16), by UIA name.</summary>
    public static readonly string[] Palette =
    [
        "Black", "Gray", "Dark red", "Red", "Orange", "Yellow", "Green", "Turquoise", "Indigo", "Purple",
        "White", "Light gray", "Brown", "Rose", "Gold", "Light yellow", "Lime", "Light turquoise", "Blue-gray", "Lavender",
    ];

    /// <summary>A tool by its key (P Pencil, B Fill, T Text, E Eraser, S Select),
    /// tagged so the presenter's hook lets it through (B is also the show's
    /// black-screen key).</summary>
    public void Key(char tool)
    {
        Keyboard.Tap(tool);
        Thread.Sleep(250);
    }

    /// <summary>A brush from the Brushes menu: selecting the split button does
    /// not switch tools, its menu item does. Right after Paint starts the menu
    /// may not open at the first try (E13).</summary>
    public void Brush(string name = "Brush")
    {
        for (int attempt = 0; ; attempt++)
        {
            Use("brushes", () => paint.ById("BrushesSplitButton"), b => b.Patterns.ExpandCollapse.Pattern.Expand());
            Thread.Sleep(500 + 500 * attempt);
            var item = WaitFor(() => paint.ByName(name, ControlType.MenuItem), 2000);
            if (item is not null)
            {
                item.Patterns.Invoke.Pattern.Invoke();
                Thread.Sleep(400);
                return;
            }
            if (attempt == 2) throw new PaintException($"the {name} brush was not found in the Brushes menu");
        }
    }

    /// <summary>Use a cached element; if it is stale (Paint rebuilt that part of
    /// its UI), find it again and try once more.</summary>
    private T Use<T>(string key, Func<AutomationElement?> find, Func<AutomationElement, T> action)
    {
        for (int attempt = 0; ; attempt++)
        {
            var e = paint.Cached(key, find) ?? throw new PaintException($"Paint's {key} was not found");
            try { return action(e); }
            catch (Exception ex) when (attempt == 0 && ex is System.Runtime.InteropServices.COMException
                                       or FlaUI.Core.Exceptions.FlaUIException or InvalidOperationException)
            {
                paint.Forget(key);
            }
        }
    }

    private void Use(string key, Func<AutomationElement?> find, Action<AutomationElement> action) =>
        Use<bool>(key, find, e => { action(e); return true; });

    private AutomationElement? FindSlot(int slot) =>
        paint.Find(cf => cf.ByControlType(ControlType.RadioButton).And(cf.ByName($"Color {slot}:", FlaUI.Core.Definitions.PropertyConditionFlags.MatchSubstring)));

    private void SelectSlot(int slot) =>
        Use($"Color {slot}", () => FindSlot(slot), e => e.Patterns.SelectionItem.Pattern.Select());

    private string SlotName(int slot) => Use($"Color {slot}", () => FindSlot(slot), e => e.Name ?? "");

    /// <summary>A palette colour into slot 1 (what strokes and the bucket use)
    /// or slot 2 (the text box's background), by a pen tap on the swatch.</summary>
    public void Colour(string name, int slot = 1)
    {
        SelectSlot(slot);
        Thread.Sleep(200);
        try
        {
            for (int tap = 0; tap < 2 && !SlotName(slot).EndsWith(": " + name); tap++)  // a second tap if the first missed
                Use($"swatch {name}", () => paint.ByName(name, ControlType.ListItem), TapOn);
            if (!SlotName(slot).EndsWith(": " + name)) throw new PaintException($"could not choose {name} for Color {slot}");
        }
        finally
        {
            if (slot != 1)
            {
                SelectSlot(1);  // draw with colour 1 again
                Thread.Sleep(200);
            }
        }
    }

    /// <summary>The drawing tools' Size slider (0–90; the Brush draws size + 1 px, E27).</summary>
    public void Size(int size) =>
        Use("Size slider", () => paint.Find(cf => cf.ByControlType(ControlType.Slider).And(cf.ByName("Size"))),
            e => e.Patterns.RangeValue.Pattern.SetValue(size));

    private void TapOn(AutomationElement e)
    {
        var r = e.BoundingRectangle;
        pen.Tap((r.Left + r.Right) / 2, (r.Top + r.Bottom) / 2);
        Thread.Sleep(300);
    }

    private static T? WaitFor<T>(Func<T?> find, int timeoutMs) where T : class
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            var found = find();
            if (found is not null || sw.ElapsedMilliseconds > timeoutMs) return found;
            Thread.Sleep(100);
        }
    }

    // --- the Text tool ------------------------------------------------------------------------------
    /// <summary>Open a text box by dragging it out with the Text tool.</summary>
    public void TextBox(int x0, int y0, int x1, int y1)
    {
        Key('T');
        pen.Stroke([(x0, y0), ((x0 + x1) / 2, (y0 + y1) / 2), (x1, y1)], 8);
        Thread.Sleep(600);
    }

    private AutomationElement FontCombo => paint.ById("FontComboBox") ?? throw new PaintException("the font box was not found");
    private AutomationElement SizeCombo => paint.ById("FontSizeComboBox") ?? throw new PaintException("the font size box was not found");

    public string? CurrentFont() => Selected(FontCombo);

    public string? CurrentFontSize()
    {
        try { return SizeCombo.Patterns.Value.Pattern.Value.Value; }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidOperationException) { return null; }
    }

    private static string? Selected(AutomationElement combo)
    {
        try { return combo.Patterns.Selection.Pattern.Selection.Value.FirstOrDefault()?.Name; }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidOperationException) { return null; }
    }

    /// <summary>
    /// Pick a font from the open box's list by its exact name. The list is
    /// virtualised, only about 40 fonts around the current one exist in UIA,
    /// so ask the box for the item (ItemContainer) and realise it first. Never
    /// type the name: "Calibri" can complete to "Calibri Light" (E16).
    /// </summary>
    public bool Font(string font)
    {
        var combo = FontCombo;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            combo.Patterns.ExpandCollapse.Pattern.Expand();
            Thread.Sleep(600);
            Realise(combo, font);
            foreach (var item in combo.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem).And(cf.ByName(font))))
            {
                try
                {
                    item.Patterns.ScrollItem.PatternOrDefault?.ScrollIntoView();
                    item.Patterns.SelectionItem.Pattern.Select();
                }
                catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidOperationException) { continue; }
                Thread.Sleep(400);
                if (Selected(combo) == font) break;
            }
            Collapse(combo);
            if (Selected(combo) == font) return true;
        }
        return false;
    }

    private static void Realise(AutomationElement combo, string name)
    {
        try
        {
            var container = combo.Patterns.ItemContainer.PatternOrDefault;
            var item = container?.FindItemByProperty(null, combo.Automation.PropertyLibrary.Element.Name, name);
            item?.Patterns.VirtualizedItem.PatternOrDefault?.Realize();
            Thread.Sleep(400);
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidOperationException) { }
    }

    private static void Collapse(AutomationElement combo)
    {
        try { combo.Patterns.ExpandCollapse.Pattern.Collapse(); }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidOperationException) { }
        Thread.Sleep(300);
    }

    /// <summary>The largest listed font size not above <paramref name="points"/>
    /// (the smallest if none is). Only a list item takes: a value set through
    /// UIA, or typed, is ignored (E27).</summary>
    public string? FontSize(int points)
    {
        var combo = SizeCombo;
        combo.Patterns.ExpandCollapse.Pattern.Expand();
        Thread.Sleep(500);
        var items = combo.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem))
            .Select(i => (Item: i, Ok: int.TryParse(i.Name, out int v), Value: v)).Where(t => t.Ok).ToList();
        if (items.Count > 0)
        {
            var fits = items.Where(t => t.Value <= points).ToList();
            var pick = fits.Count > 0 ? fits.MaxBy(t => t.Value) : items.MinBy(t => t.Value);
            pick.Item.Patterns.SelectionItem.Pattern.Select();
            Thread.Sleep(400);
        }
        Collapse(combo);
        return CurrentFontSize();
    }

    /// <summary>The Text tool's Background fill switch (UIA lists it twice).</summary>
    public void BackgroundFill(bool on)
    {
        var box = paint.Find(cf => cf.ByControlType(ControlType.CheckBox).And(cf.ByName("Background fill")))
                  ?? throw new PaintException("the Background fill switch was not found");
        var toggle = box.Patterns.Toggle.Pattern;
        if ((toggle.ToggleState.Value == ToggleState.On) != on) toggle.Toggle();
        Thread.Sleep(200);
    }

    /// <summary>
    /// Commit an open text box: a pen tap on the canvas outside it, at (x, y),
    /// then another tool. Choosing another tool straight away committed the
    /// text in some runs and dropped it in others (E27); the tap commits, and
    /// the empty box it opens is dropped by the tool change.
    /// </summary>
    public void CommitText(int x, int y)
    {
        pen.Tap(x, y);
        Thread.Sleep(400);
        CloseText();
    }

    /// <summary>Close (drop) an open, empty text box by choosing the Pencil.</summary>
    public void CloseText()
    {
        Use("Pencil", () => paint.Find(cf => cf.ByControlType(ControlType.Button).And(cf.ByName("Pencil"))),
            e => e.Patterns.Toggle.Pattern.Toggle());
        Thread.Sleep(500);
    }

    // --- the user's text settings -------------------------------------------------------------------
    // Paint keeps the Text tool's font and size across sessions (E16, E27). The
    // originals go to a file before any change, so a show that dies half way
    // is put right by the next one, instead of taking our Comic Sans for theirs.
    private static string StateFile => Path.Combine(Path.GetTempPath(), "PaintPresentator", "text-settings.json");

    private sealed record TextSettings(string? Font, string? Size);

    /// <summary>With a text box open: record the user's font and size, unless
    /// an earlier, interrupted show already did.</summary>
    public void RememberTextSettings()
    {
        if (File.Exists(StateFile)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);
        File.WriteAllText(StateFile, JsonSerializer.Serialize(new TextSettings(CurrentFont(), CurrentFontSize())));
    }

    /// <summary>Put the recorded font and size back and switch Background fill
    /// off, in an empty text box at (x, y). Returns whether there was anything
    /// to restore.</summary>
    public bool RestoreTextSettings(int x, int y)
    {
        if (!File.Exists(StateFile)) return false;
        var saved = JsonSerializer.Deserialize<TextSettings>(File.ReadAllText(StateFile));
        TextBox(x, y, x + 220, y + 80);
        if (saved?.Font is { Length: > 0 } font) Font(font);
        if (int.TryParse(saved?.Size, out int size)) FontSize(size);
        BackgroundFill(false);
        CloseText();
        File.Delete(StateFile);
        return true;
    }
}
