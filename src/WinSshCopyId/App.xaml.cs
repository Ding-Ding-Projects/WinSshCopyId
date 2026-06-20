using Microsoft.UI.Xaml;

namespace WinSshCopyId;

/// <summary>
/// Application entry point. The WinUI XAML build generates Main() automatically.
/// </summary>
public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();

        // Last-resort guard: an unhandled exception escaping an async void event
        // handler would otherwise terminate the process. Contain it so a
        // recoverable I/O error (e.g. a file picker failing) does not crash the app.
        UnhandledException += (_, e) => e.Handled = true;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
    }
}
