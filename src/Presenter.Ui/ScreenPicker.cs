using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Presenter.Core;

namespace Presenter.Ui;

/// <summary>
/// The screen picker (the user's design, research 05 §5): it pops up, lists
/// the displays with the slide size each would give, and waits for the
/// speaker. While it waits, the list follows the displays: a cable plugged
/// back in shows up at once. Keys: the display's number, Enter for the
/// highlighted default, Esc to cancel.
/// </summary>
public static class ScreenPicker
{
    /// <summary>Show the picker on a thread of its own and wait for the choice
    /// (null when cancelled).</summary>
    /// <param name="options">The displays now; called again on every display change.</param>
    /// <param name="notes">Pre-flight reminders shown under the list (Do not disturb, battery).</param>
    public static Task<ScreenOption?> ChooseAsync(Func<IReadOnlyList<ScreenOption>> options, string? remembered,
                                                  double aspect, string heading, IReadOnlyList<string>? notes = null,
                                                  CancellationToken ct = default)
    {
        var done = new TaskCompletionSource<ScreenOption?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var window = new PickerWindow(options, remembered, aspect, heading, notes ?? []);
                window.Closed += (_, _) =>
                {
                    done.TrySetResult(window.Choice);
                    Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                };
                using var reg = ct.Register(() => window.Dispatcher.BeginInvoke(window.Close));
                window.Show();
                window.Activate();
                Dispatcher.Run();
            }
            catch (Exception e)
            {
                done.TrySetException(e);
            }
        }) { IsBackground = true, Name = "screen picker" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }

    private sealed class PickerWindow : Window
    {
        private readonly Func<IReadOnlyList<ScreenOption>> _options;
        private readonly string? _remembered;
        private readonly double _aspect;
        private readonly StackPanel _list = new() { Margin = new Thickness(0, 12, 0, 12) };
        private readonly DispatcherTimer _poll;
        private IReadOnlyList<ScreenOption> _shown = [];
        private string _signature = "";

        public ScreenOption? Choice { get; private set; }

        public PickerWindow(Func<IReadOnlyList<ScreenOption>> options, string? remembered, double aspect, string heading,
                            IReadOnlyList<string> notes)
        {
            (_options, _remembered, _aspect) = (options, remembered, aspect);
            Title = "Paint Presentator";
            Topmost = true;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.WidthAndHeight;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            AutomationProperties.SetAutomationId(this, "ScreenPicker");
            var identify = new Button { Content = "_Identify", Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 0, 8, 0) };
            identify.Click += (_, _) => Identify();
            AutomationProperties.SetAutomationId(identify, "Identify");
            var cancel = new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(14, 6, 14, 6) };
            cancel.Click += (_, _) => Close();
            AutomationProperties.SetAutomationId(cancel, "Cancel");
            Content = new StackPanel
            {
                Margin = new Thickness(24),
                MinWidth = 560,
                Children =
                {
                    new TextBlock { Text = heading, FontSize = 22, FontWeight = FontWeights.SemiBold },
                    new TextBlock
                    {
                        Text = "Pick a display: its number, or Enter for the highlighted one. The list follows the displays while you choose.",
                        Margin = new Thickness(0, 6, 0, 0), Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap, MaxWidth = 560,
                    },
                    _list,
                    Notes(notes),
                    new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { identify, cancel } },
                },
            };
            PreviewKeyDown += (_, e) =>
            {
                int n = e.Key is >= Key.D1 and <= Key.D9 ? e.Key - Key.D1 : e.Key is >= Key.NumPad1 and <= Key.NumPad9 ? e.Key - Key.NumPad1 : -1;
                if (n >= 0 && n < _shown.Count)
                {
                    Pick(_shown[n]);
                    e.Handled = true;
                }
            };
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaysChanged;
            Closed += (_, _) => Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaysChanged;
            // a fallback for changes that arrive without a settings-changed event
            _poll = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) => Rebuild(), Dispatcher);
            Rebuild();
        }

        private void OnDisplaysChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(Rebuild);

        private static UIElement Notes(IReadOnlyList<string> notes)
        {
            var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
            foreach (var note in notes)
                panel.Children.Add(new TextBlock
                {
                    Text = "•  " + note, TextWrapping = TextWrapping.Wrap, MaxWidth = 560,
                    Foreground = Brushes.DarkGoldenrod, Margin = new Thickness(0, 2, 0, 2),
                });
            return panel;
        }

        private void Rebuild()
        {
            var options = _options();
            string signature = string.Join(";", options.Select(o => $"{o.Device}|{o.Identity}|{o.Resolution}|{o.ScalePercent}|{o.Viewport}"));
            if (signature == _signature) return;
            _signature = signature;
            _shown = options;
            _list.Children.Clear();
            int pick = ScreenChoice.DefaultIndex(options, _remembered);
            if (options.Count == 0)
                _list.Children.Add(new TextBlock { Text = "No display found. Waiting for one…", FontSize = 15 });
            for (int i = 0; i < options.Count; i++)
            {
                var o = options[i];
                var slide = o.SlideFor(_aspect);
                var button = new Button
                {
                    Content = new StackPanel
                    {
                        Children =
                        {
                            new TextBlock { Text = $"{i + 1}.  {o.Name}", FontSize = 17, FontWeight = FontWeights.SemiBold },
                            new TextBlock
                            {
                                Text = $"{o.Connection}{(o.BuiltIn ? " (this laptop)" : "")} · {o.Resolution.Width} × {o.Resolution.Height} at {o.ScalePercent}% · slides {slide.Width} × {slide.Height}",
                                Foreground = Brushes.DimGray,
                            },
                        },
                    },
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Padding = new Thickness(14, 10, 14, 10),
                    Margin = new Thickness(0, 4, 0, 4),
                    IsDefault = i == pick,
                    BorderThickness = new Thickness(i == pick ? 3 : 1),
                    BorderBrush = i == pick ? Brushes.SteelBlue : Brushes.LightGray,
                };
                AutomationProperties.SetAutomationId(button, $"Screen{i + 1}");
                AutomationProperties.SetName(button, $"{i + 1}. {o}");
                AutomationProperties.SetHelpText(button, o.Device);  // for tests: identical monitors share a name
                button.Click += (_, _) => Pick(o);
                _list.Children.Add(button);
                if (i == pick) button.Loaded += (_, _) => button.Focus();
            }
        }

        private void Pick(ScreenOption o)
        {
            Choice = o;
            Close();
        }

        /// <summary>A big number on each display for two seconds.</summary>
        private void Identify()
        {
            for (int i = 0; i < _shown.Count; i++)
            {
                var o = _shown[i];
                var label = new Window
                {
                    WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, Topmost = true, ShowActivated = false,
                    ShowInTaskbar = false, Background = Brushes.SteelBlue, Width = 220, Height = 220,
                    Content = new TextBlock
                    {
                        Text = (i + 1).ToString(), FontSize = 140, Foreground = Brushes.White,
                        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                    },
                    WindowStartupLocation = WindowStartupLocation.Manual,
                };
                label.SourceInitialized += (_, _) => MoveToPixels(label, o.Bounds.Left + 60, o.Bounds.Top + 60);
                label.Show();
                var close = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Normal, (s, _) =>
                {
                    ((DispatcherTimer)s!).Stop();
                    label.Close();
                }, Dispatcher);
            }
        }

        private static void MoveToPixels(Window w, int x, int y)
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(w).Handle;
            SetWindowPos(hwnd, 0, x, y, 0, 0, 0x0001 | 0x0004 | 0x0010);  // NOSIZE | NOZORDER | NOACTIVATE
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int w, int h, uint flags);
    }
}
