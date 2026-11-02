using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;

namespace ScreenSwitcher;

public partial class App : Application
{
    const string BringToFrontEvent = "ScreenSwitcher.ShowWindow";
    const string SingleWindowMutex = "ScreenSwitcher.SingleWindow";
    const int AnyProcess = -1;

    Mutex? _singleWindow;
    EventWaitHandle? _bringToFront;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;

        if (e.Args.Length > 0)
        {
            Shutdown(RunCommand(e.Args[0]));
            return;
        }

        _singleWindow = new Mutex(true, SingleWindowMutex, out var isFirst);
        if (!isFirst)
        {
            // Starting the program again just brings the open window to the front.
            AllowSetForegroundWindow(AnyProcess);
            if (EventWaitHandle.TryOpenExisting(BringToFrontEvent, out var request))
            {
                request.Set();
                request.Dispose();
            }
            Shutdown();
            return;
        }

        var window = new MainWindow();
        window.Show();

        _bringToFront = new EventWaitHandle(false, EventResetMode.AutoReset, BringToFrontEvent);
        ThreadPool.RegisterWaitForSingleObject(_bringToFront,
            (_, _) => Dispatcher.BeginInvoke(() =>
            {
                if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
                window.Activate();
            }),
            null, Timeout.Infinite, executeOnlyOnce: false);
    }

    // Works without a window, for a shortcut or for LaunchBox: surround, extended or toggle.
    static int RunCommand(string command)
    {
        try
        {
            switch (command.ToLowerInvariant())
            {
                case "surround":
                    Switcher.SwitchTo(ScreenMode.Surround);
                    return 0;
                case "extended":
                    Switcher.SwitchTo(ScreenMode.Extended);
                    return 0;
                case "toggle":
                    Switcher.Toggle();
                    return 0;
                default:
                    throw new ArgumentException($"Unknown command: {command}\n\nThe program understands: surround, extended, toggle.");
            }
        }
        catch (Exception e)
        {
            MessageBox.Show(e.Message, "Screen Switcher", MessageBoxButton.OK, MessageBoxImage.Error);
            return 1;
        }
    }

    static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show("Something went wrong:\n\n" + e.Exception.Message, "Screen Switcher",
            MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    [DllImport("user32.dll")]
    static extern bool AllowSetForegroundWindow(int processId);
}
