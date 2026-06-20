using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinSshCopyId.Services;

namespace WinSshCopyId;

public sealed partial class MainWindow : Window
{
    private static readonly string[] DefaultIdentityNames =
        ["id_rsa", "id_ed25519", "id_ecdsa", "id_dsa"];

    private readonly KeyManager _keyManager = new();
    private readonly SshDeployer _deployer = new();
    private readonly List<KeyEntry> _keys = new();
    private bool _initialized;
    private bool _suppressKeySelection;

    public MainWindow()
    {
        InitializeComponent();
        Title = "WinSshCopyId";
        ConfigureWindow();
        LoadKeys(selectPath: null);

        ApplySettings(SettingsStore.Load(), applyPassword: true);

        _initialized = true;
        Closed += OnWindowClosed;
    }

    private void ConfigureWindow()
    {
        TryResize(760, 960);
        if (AppWindow?.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
        }
    }

    // ---------- Theme ----------

    private string SelectedTheme =>
        (ThemeCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "System";

    private void ThemeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ApplyTheme(SelectedTheme);
        if (_initialized)
        {
            TrySaveSettings();
        }
    }

    private void ApplyTheme(string theme)
    {
        if (Content is FrameworkElement root)
        {
            root.RequestedTheme = theme switch
            {
                "Light" => ElementTheme.Light,
                "Dark" => ElementTheme.Dark,
                _ => ElementTheme.Default,
            };
        }
    }

    private void SetThemeCombo(string theme)
    {
        ThemeCombo.SelectedIndex = theme switch
        {
            "Light" => 1,
            "Dark" => 2,
            _ => 0,
        };
    }

    private KeyEntry? SelectedKey => KeyCombo.SelectedItem as KeyEntry;

    private int DefaultPort => double.IsNaN(PortBox.Value) ? 22 : (int)PortBox.Value;

    // ---------- Key list ----------

    private void LoadKeys(string? selectPath)
    {
        _keys.Clear();
        foreach (KeyEntry k in _keyManager.FindExistingKeys())
        {
            _keys.Add(k);
        }
        RebindKeyCombo(selectPath);
    }

    private void RebindKeyCombo(string? selectPath)
    {
        // Suppress the transient SelectionChanged churn caused by clearing and
        // reassigning ItemsSource; update the preview once at the end instead.
        _suppressKeySelection = true;
        try
        {
            KeyCombo.ItemsSource = null;
            KeyCombo.ItemsSource = _keys;
            if (_keys.Count == 0)
            {
                return;
            }
            int index = 0;
            if (selectPath is not null)
            {
                int found = _keys.FindIndex(k =>
                    string.Equals(k.PublicKeyPath, selectPath, StringComparison.OrdinalIgnoreCase));
                if (found >= 0)
                {
                    index = found;
                }
            }
            KeyCombo.SelectedIndex = index;
        }
        finally
        {
            _suppressKeySelection = false;
        }
        UpdateKeyPreview();
    }

    private void SelectKeyByPath(string publicKeyPath)
    {
        bool present = _keys.Any(k =>
            string.Equals(k.PublicKeyPath, publicKeyPath, StringComparison.OrdinalIgnoreCase));
        if (!present && File.Exists(publicKeyPath))
        {
            try
            {
                _keys.Add(_keyManager.ImportPublicKey(publicKeyPath));
            }
            catch
            {
                // The remembered key is gone or unreadable; leave selection alone.
            }
        }
        RebindKeyCombo(publicKeyPath);
    }

    private void KeyCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressKeySelection)
        {
            return;
        }
        UpdateKeyPreview();
    }

    private void UpdateKeyPreview()
    {
        KeyEntry? key = SelectedKey;
        if (key is null)
        {
            KeyPreview.Text = string.Empty;
            return;
        }
        string preview = key.PublicKeyLine.Length > 70
            ? key.PublicKeyLine[..70] + "..."
            : key.PublicKeyLine;
        string priv = key.PrivateKeyPath is null
            ? "private key not found (you can still install the public key)"
            : key.PrivateKeyPath;
        KeyPreview.Text = $"{preview}\nprivate key: {priv}";
    }

    // ---------- Key buttons ----------

    private async void GenerateButton_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true, "Generating key...");
        try
        {
            string comment = $"{Environment.UserName}@{Environment.MachineName}";
            KeyEntry created = await Task.Run(() => _keyManager.GenerateKey(comment, Log));
            LoadKeys(selectPath: created.PublicKeyPath);
            Status($"Created {created.FileName}.");
        }
        catch (Exception ex)
        {
            Log("ERROR: " + ex.Message);
            Status("Key generation failed.");
        }
        finally
        {
            SetBusy(false, null);
        }
    }

    private async void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        FileOpenPicker picker = CreateOpenPicker();
        picker.FileTypeFilter.Add(".pub");
        picker.FileTypeFilter.Add("*");

        StorageFile? file = await picker.PickSingleFileAsync();
        if (file is null)
        {
            return;
        }
        try
        {
            KeyEntry entry = _keyManager.ImportPublicKey(file.Path);
            if (!_keys.Any(k => string.Equals(k.PublicKeyPath, entry.PublicKeyPath,
                    StringComparison.OrdinalIgnoreCase)))
            {
                _keys.Add(entry);
            }
            RebindKeyCombo(entry.PublicKeyPath);
            Log("Imported public key: " + entry.PublicKeyPath);
        }
        catch (Exception ex)
        {
            Log("ERROR: " + ex.Message);
            Status("Could not import that file.");
        }
    }

    // ---------- Deploy / test ----------

    private async void DeployButton_Click(object sender, RoutedEventArgs e)
    {
        IReadOnlyList<HostTarget> hosts = HostTarget.ParseList(HostsBox.Text, DefaultPort);
        string user = UserBox.Text.Trim();
        string password = PasswordBox.Password;
        KeyEntry? key = SelectedKey;
        bool addAlias = AliasCheck.IsChecked == true;

        if (hosts.Count == 0) { Status("Enter at least one host."); return; }
        if (user.Length == 0) { Status("Enter a username."); return; }
        if (password.Length == 0) { Status("Enter the password for the first login."); return; }
        if (key is null) { Status("Select or generate an SSH key first."); return; }

        SetBusy(true, $"Installing on {hosts.Count} host(s)...");
        try
        {
            List<HostOutcome> outcomes = await Task.Run(() =>
                DeployAll(hosts, user, password, key, addAlias));

            int installed = outcomes.Count(o => o.Error is null);
            int verified = outcomes.Count(o => o.Verified);
            Status($"Done. {installed}/{outcomes.Count} installed, {verified} verified.");

            if (RememberCheck.IsChecked == true)
            {
                TrySaveSettings();
            }
            await ShowSummaryAsync(outcomes, user, key);
        }
        catch (Exception ex)
        {
            Log("ERROR: " + ex.Message);
            Status("Install failed. See log.");
        }
        finally
        {
            if (SavePasswordCheck.IsChecked != true)
            {
                PasswordBox.Password = string.Empty;
            }
            SetBusy(false, null);
        }
    }

    private List<HostOutcome> DeployAll(
        IReadOnlyList<HostTarget> hosts, string user, string password, KeyEntry key, bool addAlias)
    {
        var outcomes = new List<HostOutcome>();
        foreach (HostTarget h in hosts)
        {
            Log($"=== {h.Display} ===");
            try
            {
                DeployResult res = _deployer.InstallKey(h.Host, h.Port, user, password, key.PublicKeyLine, Log);

                bool aliasAdded = false;
                if (addAlias)
                {
                    aliasAdded = SshConfig.AddHostEntry(h.Host, h.Host, user, h.Port, key.PrivateKeyPath);
                    Log(aliasAdded
                        ? $"Added ~/.ssh/config entry '{h.Host}'."
                        : $"~/.ssh/config already had a Host '{h.Host}'.");
                }

                bool verified = false;
                bool verifyAttempted = false;
                if (key.PrivateKeyPath is not null)
                {
                    verifyAttempted = true;
                    try
                    {
                        verified = _deployer.VerifyKeyLogin(
                            h.Host, h.Port, user, key.PrivateKeyPath, res.ServerFingerprint, Log);
                    }
                    catch (Exception ve)
                    {
                        Log("verify error: " + ve.Message);
                    }
                }
                outcomes.Add(new HostOutcome(
                    h.Host, h.Port, true, res.KeyWasAlreadyPresent, verified, verifyAttempted, aliasAdded, null));
            }
            catch (Exception ex)
            {
                Log("ERROR: " + ex.Message);
                outcomes.Add(new HostOutcome(h.Host, h.Port, false, false, false, false, false, ex.Message));
            }
        }
        return outcomes;
    }

    private async void TestButton_Click(object sender, RoutedEventArgs e)
    {
        IReadOnlyList<HostTarget> hosts = HostTarget.ParseList(HostsBox.Text, DefaultPort);
        string user = UserBox.Text.Trim();
        KeyEntry? key = SelectedKey;

        if (hosts.Count == 0) { Status("Enter at least one host."); return; }
        if (user.Length == 0) { Status("Enter a username."); return; }
        if (key?.PrivateKeyPath is null)
        {
            Status("Selected key has no private key file on this machine to test with.");
            return;
        }

        SetBusy(true, $"Testing {hosts.Count} host(s)...");
        try
        {
            int ok = await Task.Run(() =>
            {
                int passed = 0;
                foreach (HostTarget h in hosts)
                {
                    Log($"=== {h.Display} ===");
                    try
                    {
                        if (_deployer.VerifyKeyLogin(h.Host, h.Port, user, key.PrivateKeyPath, null, Log))
                        {
                            passed++;
                        }
                    }
                    catch (Exception ex)
                    {
                        Log("ERROR: " + ex.Message);
                    }
                }
                return passed;
            });
            Status($"{ok}/{hosts.Count} host(s) accept the key.");
        }
        catch (Exception ex)
        {
            Log("ERROR: " + ex.Message);
            Status("Test failed. See log.");
        }
        finally
        {
            SetBusy(false, null);
        }
    }

    // ---------- Profile: save / export / import ----------

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (TrySaveSettings())
        {
            Status("Settings saved.");
        }
    }

    private bool TrySaveSettings()
    {
        try
        {
            SettingsStore.Save(CollectSettings());
            return true;
        }
        catch (Exception ex)
        {
            Log("Could not save settings: " + ex.Message);
            Status("Saving settings failed.");
            return false;
        }
    }

    private async void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileSavePicker();
        InitWithWindow(picker);
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        picker.SuggestedFileName = "winsshcopyid-profile";
        picker.FileTypeChoices.Add("WinSshCopyId profile", new List<string> { ".json" });

        StorageFile? file = await picker.PickSaveFileAsync();
        if (file is null)
        {
            return;
        }
        try
        {
            SettingsStore.Export(file.Path, CollectSettings());
            Log("Exported profile to " + file.Path);
            Status("Profile exported (password is never included).");
        }
        catch (Exception ex)
        {
            Log("ERROR: " + ex.Message);
            Status("Export failed.");
        }
    }

    private async void ImportButton_Click(object sender, RoutedEventArgs e)
    {
        FileOpenPicker picker = CreateOpenPicker();
        picker.FileTypeFilter.Add(".json");
        picker.FileTypeFilter.Add("*");

        StorageFile? file = await picker.PickSingleFileAsync();
        if (file is null)
        {
            return;
        }
        try
        {
            AppSettings imported = SettingsStore.Import(file.Path);
            ApplySettings(imported, applyPassword: false);
            Log("Imported profile from " + file.Path);
            Status("Profile imported (enter the password to deploy).");
        }
        catch (Exception ex)
        {
            Log("ERROR: " + ex.Message);
            Status("Import failed.");
        }
    }

    private AppSettings CollectSettings()
    {
        var settings = new AppSettings
        {
            Hosts = HostsBox.Text,
            Username = UserBox.Text.Trim(),
            Port = DefaultPort,
            KeyPath = SelectedKey?.PublicKeyPath,
            AddAliases = AliasCheck.IsChecked == true,
            RememberPassword = SavePasswordCheck.IsChecked == true,
            Theme = SelectedTheme,
            AutoSave = RememberCheck.IsChecked == true,
        };
        if (settings.RememberPassword && PasswordBox.Password.Length > 0)
        {
            try
            {
                settings.ProtectedPassword = Dpapi.Protect(PasswordBox.Password);
            }
            catch (Exception ex)
            {
                Log("Could not encrypt password: " + ex.Message);
                settings.RememberPassword = false;
            }
        }
        return settings;
    }

    private void ApplySettings(AppSettings settings, bool applyPassword)
    {
        HostsBox.Text = settings.Hosts ?? string.Empty;
        UserBox.Text = settings.Username ?? string.Empty;
        PortBox.Value = settings.Port is < 1 or > 65535 ? 22 : settings.Port;
        AliasCheck.IsChecked = settings.AddAliases;
        SavePasswordCheck.IsChecked = settings.RememberPassword;
        RememberCheck.IsChecked = settings.AutoSave;
        SetThemeCombo(settings.Theme ?? "System");

        if (!string.IsNullOrEmpty(settings.KeyPath))
        {
            SelectKeyByPath(settings.KeyPath);
        }

        if (applyPassword && settings.RememberPassword && settings.ProtectedPassword is not null)
        {
            string? pw = Dpapi.Unprotect(settings.ProtectedPassword);
            if (pw is not null)
            {
                PasswordBox.Password = pw;
            }
        }
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        if (RememberCheck.IsChecked == true)
        {
            try
            {
                SettingsStore.Save(CollectSettings());
            }
            catch
            {
                // Closing anyway; nothing useful to do here.
            }
        }
    }

    // ---------- Success summary ----------

    private async Task ShowSummaryAsync(List<HostOutcome> outcomes, string user, KeyEntry key)
    {
        var commands = new List<string>();
        var sb = new StringBuilder();
        foreach (HostOutcome o in outcomes)
        {
            if (o.Error is not null)
            {
                sb.AppendLine($"FAILED  {o.Display}: {o.Error}");
                continue;
            }
            string state;
            if (o.Verified)
            {
                state = "VERIFIED";
            }
            else if (o.VerifyAttempted)
            {
                state = "VERIFY FAILED";
            }
            else if (o.AlreadyPresent)
            {
                state = "ALREADY (unverified)";
            }
            else
            {
                state = "INSTALLED (unverified)";
            }
            string command = BuildLoginCommand(o.Host, o.Port, user, key, o.AliasAdded);
            commands.Add(command);
            sb.AppendLine($"{state}  {o.Display}  ->  {command}");
        }

        int installed = outcomes.Count(o => o.Error is null);
        int verified = outcomes.Count(o => o.Verified);
        int verifyFailed = outcomes.Count(o => o.Error is null && o.VerifyAttempted && !o.Verified);
        string headline = $"{installed}/{outcomes.Count} host(s) installed, {verified} verified."
            + (verifyFailed > 0 ? $" {verifyFailed} installed but could NOT be verified." : string.Empty);

        var detail = new TextBox
        {
            Text = sb.ToString().TrimEnd(),
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
            FontSize = 12,
            Height = 220,
        };

        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock
        {
            Text = headline,
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(detail);

        var dialog = new ContentDialog
        {
            Title = "Results",
            Content = panel,
            PrimaryButtonText = "Copy commands",
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };
        dialog.PrimaryButtonClick += (_, _) =>
        {
            var data = new DataPackage();
            data.SetText(string.Join(Environment.NewLine, commands));
            Clipboard.SetContent(data);
        };
        await dialog.ShowAsync();
    }

    private static string BuildLoginCommand(string host, int port, string user, KeyEntry key, bool aliasAdded)
    {
        // The alias shortcut only works if the config entry got an IdentityFile,
        // which only happens when we know the private key path.
        if (aliasAdded && key.PrivateKeyPath is not null)
        {
            return $"ssh {host}";
        }

        string portPart = port == 22 ? string.Empty : $"-p {port} ";
        string identityPart = string.Empty;
        if (key.PrivateKeyPath is not null)
        {
            string name = Path.GetFileName(key.PrivateKeyPath);
            if (!DefaultIdentityNames.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                identityPart = $"-i \"{key.PrivateKeyPath}\" ";
            }
        }
        return $"ssh {identityPart}{portPart}{user}@{host}";
    }

    // ---------- Pickers / window helpers ----------

    private FileOpenPicker CreateOpenPicker()
    {
        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.ComputerFolder,
        };
        InitWithWindow(picker);
        return picker;
    }

    private void InitWithWindow(object picker)
    {
        nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
    }

    private void SetBusy(bool busy, string? status)
    {
        BusyRing.IsActive = busy;
        DeployButton.IsEnabled = !busy;
        TestButton.IsEnabled = !busy;
        GenerateButton.IsEnabled = !busy;
        BrowseButton.IsEnabled = !busy;
        ExportButton.IsEnabled = !busy;
        ImportButton.IsEnabled = !busy;
        SaveButton.IsEnabled = !busy;
        if (status is not null)
        {
            Status(status);
        }
    }

    private void Status(string text)
    {
        DispatcherQueue.TryEnqueue(() => StatusText.Text = text);
    }

    private void Log(string message)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            LogBox.Text += message + Environment.NewLine;
            LogBox.Select(LogBox.Text.Length, 0);
        });
    }

    private void TryResize(int width, int height)
    {
        try
        {
            AppWindow?.Resize(new SizeInt32(width, height));
        }
        catch
        {
            // Resizing is best-effort; ignore where unavailable.
        }
    }

    private sealed record HostOutcome(
        string Host, int Port, bool Installed, bool AlreadyPresent,
        bool Verified, bool VerifyAttempted, bool AliasAdded, string? Error)
    {
        public string Display => Port == 22 ? Host : $"{Host}:{Port}";
    }
}
