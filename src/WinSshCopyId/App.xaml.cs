using Microsoft.UI.Xaml;
using WinSshCopyId.UI;
namespace WinSshCopyId;
public partial class App : Application
{
    private Window? window;
    public App() { InitializeComponent(); }
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var arguments = Environment.GetCommandLineArgs().Skip(1).ToArray();
        if (arguments.Any(a => a is "--squirrel-install" or "--squirrel-updated" or "--squirrel-uninstall" or "--squirrel-obsolete")) { Exit(); return; }
        string? evidenceDirectory = null;
        var index = Array.IndexOf(arguments, "--evidence-profile");
        if (index >= 0) { if (index + 1 >= arguments.Length) { Exit(); return; } evidenceDirectory = arguments[index + 1]; }
        try { window = new MainWindow(new WorkspaceData(evidenceDirectory)); window.Activate(); }
        catch (ArgumentException) { Exit(); }
    }
}
