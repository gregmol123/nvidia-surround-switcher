using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ScreenSwitcher;

public partial class MainWindow : Window
{
    const string ActiveTag = "Active";

    bool _switching;

    public MainWindow()
    {
        InitializeComponent();
    }

    void Window_Loaded(object sender, RoutedEventArgs e)
    {
        var grids = ShowCurrentMode();

        // In Surround the middle of the screen is the seam between two monitors, so the window opens in the middle of the first one.
        var columns = grids.Count > 0 ? grids.Max(grid => grid.Columns) : 1;
        var area = SystemParameters.WorkArea;
        Left = area.Left + (area.Width / columns - ActualWidth) / 2;
        Top = area.Top + (area.Height - ActualHeight) / 2;
    }

    // The mode can also change through the shortcut while this window sits in the background.
    void Window_Activated(object? sender, EventArgs e)
    {
        if (IsLoaded && !_switching) ShowCurrentMode();
    }

    void SurroundCard_Click(object sender, RoutedEventArgs e) => Switch(ScreenMode.Surround);

    void ExtendedCard_Click(object sender, RoutedEventArgs e) => Switch(ScreenMode.Extended);

    List<Grid> ShowCurrentMode()
    {
        try
        {
            var grids = Switcher.ReadAndRemember();
            var mode = Switcher.ModeOf(grids);
            CurrentText.Text = $"Now: {mode} ({Switcher.Describe(grids)})";
            MarkActive(SurroundCard, mode == ScreenMode.Surround);
            MarkActive(ExtendedCard, mode == ScreenMode.Extended);
            return grids;
        }
        catch (Exception e) when (e is InvalidOperationException or DllNotFoundException or IOException or UnauthorizedAccessException)
        {
            CurrentText.Text = "Could not read the screen layout.";
            ShowError(e.Message);
            Cards.IsEnabled = false;
            return [];
        }
    }

    static void MarkActive(Button card, bool active)
    {
        card.Tag = active ? ActiveTag : null;
        card.IsEnabled = !active;
    }

    async void Switch(ScreenMode target)
    {
        _switching = true;
        Cards.IsEnabled = false;
        Cards.Opacity = 0.5;
        StatusText.Foreground = (Brush)FindResource("TextDim");
        StatusText.Text = "Switching the screens…";

        try
        {
            var done = await Task.Run(() => Switcher.SwitchTo(target));
            if (done)
            {
                // The screens themselves show the result, and the window would only be in the way of whatever comes next.
                Close();
                return;
            }
            StatusText.Text = "Another switch is still running. Try again in a moment.";
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            ShowError(e.Message);
        }

        _switching = false;
        Cards.IsEnabled = true;
        Cards.Opacity = 1;
        ShowCurrentMode();
    }

    void ShowError(string message)
    {
        StatusText.Foreground = (Brush)FindResource("StateFailed");
        StatusText.Text = message;
    }
}
