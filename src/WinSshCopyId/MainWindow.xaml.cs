using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using WinSshCopyId.Controls;
using WinSshCopyId.Localization;
using WinSshCopyId.Services;
using WinSshCopyId.UI;
using WinSshCopyId.Workspace;

namespace WinSshCopyId;

public sealed partial class MainWindow : Window
{
    private readonly WorkspaceData data;
    private WorkspaceSettings preferences = new();
    private WorkspaceSession session = new();
    private readonly KeyManager keyManager = new();
    private readonly ICredentialStore credentials = new WinCredentialStore();
    private readonly WorkspaceSearch searchEngine = new();
    private readonly List<ConnectionRecord> connections = [];
    private readonly List<KeyEntry> keys = [];
    private IReadOnlyList<DeploymentOutcome> outcomes = [];
    private readonly List<string> notifications = [];
    private readonly Dictionary<string,StackPanel> pages = new();
    private readonly Dictionary<string,ScopedSearchBox> searches = new();
    private readonly TextBox nameBox = new(), hostsBox = new() {AcceptsReturn=true,MinHeight=90,TextWrapping=TextWrapping.Wrap}, userBox = new();
    private readonly NumberBox portBox = new() {Value=22,Minimum=1,Maximum=65535,SpinButtonPlacementMode=NumberBoxSpinButtonPlacementMode.Inline};
    private readonly PasswordBox passwordBox = new();
    private readonly CheckBox rememberPassword = new();
    private readonly TextBlock keyLabel = new() {TextWrapping=TextWrapping.Wrap}, resultDetail = new() {TextWrapping=TextWrapping.Wrap,IsTextSelectionEnabled=true};
    private readonly ListView connectionList = new() {MaxHeight=420,SelectionMode=ListViewSelectionMode.Single}, keyList = new() {MaxHeight=400}, resultList = new() {MaxHeight=400,SelectionMode=ListViewSelectionMode.Multiple};
    private readonly ProgressBar progress = new() {Minimum=0,Maximum=100};
    private KeyEntry? selectedKey;
    private Guid? selectedConnection;
    private string route = "Connections";
    private CancellationTokenSource? operation;
    private bool initialized, closed, busy;
    private Button? installButton, cancelButton;
    private Grid? connectionGrid;
    private Border? recordsSurface;
    private readonly string[] destinations=["Connections","Keys","Results","Tools","History","Settings","Help"];

    public MainWindow(WorkspaceData provider)
    {
        data=provider; InitializeComponent();
        ExtendsContentIntoTitleBar=true; SetTitleBar(DragRegion);
        AppWindow.Resize(new SizeInt32(1200,800));
        var icon=Path.Combine(AppContext.BaseDirectory,"Assets","WinSshCopyId.ico");if(File.Exists(icon))AppWindow.SetIcon(icon);
        if(AppWindow.Presenter is OverlappedPresenter presenter){presenter.IsResizable=true;presenter.IsMaximizable=true;presenter.IsMinimizable=true;}
        Root.SizeChanged += (_,_) => Adapt();
        AppWindow.Changed += (_,e) => {if(e.DidSizeChange){var scale=Root.XamlRoot?.RasterizationScale??1;var size=AppWindow.Size;if(size.Width<640*scale||size.Height<480*scale)AppWindow.Resize(new SizeInt32(Math.Max(size.Width,(int)(640*scale)),Math.Max(size.Height,(int)(480*scale))));}};
        var shortcut=new KeyboardAccelerator {Key=VirtualKey.F,Modifiers=VirtualKeyModifiers.Control|VirtualKeyModifiers.Shift};
        shortcut.Invoked+=(_,e)=>{e.Handled=true;OpenPalette(PaletteButton);}; Root.KeyboardAccelerators.Add(shortcut);
        Closed+=(_,_)=>{closed=true;operation?.Cancel();passwordBox.Password="";};
        Root.Loaded+=async(_,_)=>{if(!initialized)await InitializeAsync();};
    }
    private string L(string value)=>WorkspaceText.Get(value,preferences.Language);
    private Style StyleFor(string key)=>(Style)Application.Current.Resources[key];
    private TextBlock Text(string value,bool heading=false)=>new(){Text=L(value),TextWrapping=TextWrapping.Wrap,Style=StyleFor(heading?"MaterialTitleStyle":"MaterialBodyStyle")};
    private Button Action(string label,Action action,bool primary=false)
    {
        var button=new Button {Content=L(label),Style=StyleFor(primary?"MaterialPrimaryButtonStyle":"MaterialButtonStyle")};
        AutomationProperties.SetAutomationId(button,route+"."+label.Replace(" ",""));
        button.Click+=(_,_)=>action();return button;
    }
    private Button AsyncAction(string label,Func<Task> action,bool primary=false)=>Action(label,async()=>{try{if(busy){Show("Wait for the active operation or cancel it before starting another action.");return;}await action();}catch(OperationCanceledException){Show("Operation cancelled.");}catch(Exception ex){Show(SafeError(ex),true);}},primary);
    private static string SafeError(Exception ex)=>ex switch {ArgumentException=>ex.Message,InvalidDataException=>ex.Message,UnauthorizedAccessException=>"Access was denied. Check the selected destination and permissions.",IOException=>"The file operation could not complete. The previous state has been retained.",_=>"The operation could not complete. Review its inputs and try again."};
    private StackPanel Row(params UIElement[] elements){var panel=new StackPanel {Spacing=8};foreach(var e in elements)panel.Children.Add(e);return panel;}
    private Border Card(UIElement content)=>new(){Child=content,Style=StyleFor("MaterialSurfaceStyle")};
    private ScopedSearchBox Search(string id,string label,EventHandler? changed=null)
    {
        var box=new ScopedSearchBox(id,L(label));if(changed is not null)box.QueryChanged+=changed;searches[id]=box;return box;
    }
    private async Task InitializeAsync()
    {
        initialized=true;
        try
        {
            var load=await data.Settings.LoadAsync();preferences=load.Settings;session=await data.Sessions.LoadAsync();
            if(session.Tabs.Count==0){foreach(var destination in destinations)session=session.Open(destination,destination);session=session with{ActiveTabId=session.Tabs.First().Id};}
            connections.AddRange(await data.LoadRecordsAsync());
            var settings=data.LoadConnection();
            if(!data.IsEvidence){SettingsStore.MigrateLegacyCredential(settings,credentials);keys.AddRange(keyManager.FindExistingKeys().Where(k=>ValidKey(k.PublicKeyLine)));}
            ApplyConnection(settings);
            if(!data.IsEvidence&&settings.RememberPassword)passwordBox.Password=credentials.Get("ssh-password")??"";
            ApplyPreferences();BuildPages();Navigate(session.Tabs.FirstOrDefault(t=>t.Id==session.ActiveTabId)?.Route??"Connections");
            Show(data.IsEvidence?L("Evidence profile: SSH and credential storage disabled"):WorkspaceText.Ready(preferences));
        }
        catch(Exception ex){DetachFields();ApplyPreferences();BuildPages();Navigate("Connections");Show(SafeError(ex),true);}
    }
    private static bool ValidKey(string value){try{SshInput.ValidatePublicKey(value);return true;}catch(ArgumentException){return false;}}
    private void ApplyPreferences()
    {
        Root.RequestedTheme=preferences.Theme switch {WorkspaceTheme.Light=>ElementTheme.Light,WorkspaceTheme.Dark=>ElementTheme.Dark,_=>ElementTheme.Default};
        Root.Resources["ContentControlThemeFontFamily"]=new FontFamily(preferences.FontFamily);
        ProductTitle.Text=preferences.DisplayName;Title=preferences.DisplayName;
        MainArea.Padding=new Thickness(preferences.Density switch{WorkspaceDensity.Compact=>12,WorkspaceDensity.Spacious=>32,_=>24});
        PaletteButton.Content=L("Commands");AccessibilityButton.Content=L("Accessibility");CompactTabs.Content=L("Workspace tabs");
        AccessibilityButton.Visibility=preferences.AccessibilityButtonVisible?Visibility.Visible:Visibility.Collapsed;
        BuildText.Text=$"v{typeof(App).Assembly.GetName().Version?.ToString(3)}"+(data.IsEvidence?" · Evidence profile":"");
    }
    private void BuildPages()
    {
        PageHost.Children.Clear();pages.Clear();searches.Clear();NavigationPanel.Children.Clear();
        var navSearch=Search("tabs.current","Workspace tabs",(_,_)=>FilterNavigation());NavigationPanel.Children.Add(navSearch);
        foreach(var name in destinations){var b=Action(name,()=>Navigate(name));b.HorizontalAlignment=HorizontalAlignment.Stretch;b.Tag=name;NavigationPanel.Children.Add(b);}
        BuildConnections();BuildKeys();BuildResults();BuildSettings();BuildTools();BuildHistory();BuildHelp();
        Adapt();
    }
    private void FilterNavigation(){foreach(var child in NavigationPanel.Children.OfType<Button>())child.Visibility=searchEngine.Filter(new[]{(string)child.Tag},x=>L(x),searches["tabs.current"].Query).Any()?Visibility.Visible:Visibility.Collapsed;}
    private void Navigate(string destination)
    {
        if(!pages.ContainsKey(destination))destination="Connections";route=destination;PageTitle.Text=L(destination);PageHost.Children.Clear();PageHost.Children.Add(pages[destination]);
        foreach(var button in NavigationPanel.Children.OfType<Button>())button.Style=StyleFor((string)button.Tag==destination?"MaterialPrimaryButtonStyle":"MaterialButtonStyle");
        var tab=session.Tabs.FirstOrDefault(t=>t.Route==destination);if(tab is not null){session=session with{ActiveTabId=tab.Id};_ = SaveSessionAsync();}
        if(destination=="Results")RefreshResults();if(destination=="History")RefreshHistory();
    }
    private async Task SaveSessionAsync(){try{await data.Sessions.SaveAsync(session);}catch{Show("Workspace position could not be saved.",true);}}
    private void BuildConnections()
    {
        nameBox.Header=L("Display name");hostsBox.Header=L("Hosts");hostsBox.PlaceholderText="example.test\n192.0.2.10:22";userBox.Header=L("Username");portBox.Header=L("Port");passwordBox.Header=L("Password");rememberPassword.Content=L("Remember password in Credential Manager");rememberPassword.IsEnabled=!data.IsEvidence;
        var records=Row(Search("connections.records","Search connections",(_,_)=>RefreshConnections()),connectionList,Action("New connection",NewConnection),AsyncAction("Save connection",SaveRecordAsync));
        connectionList.SelectionChanged-=ConnectionSelected;connectionList.SelectionChanged+=ConnectionSelected;
        recordsSurface=Card(records);connectionGrid=new Grid {ColumnSpacing=20,RowSpacing=16};connectionGrid.ColumnDefinitions.Add(new(){Width=new GridLength(280)});connectionGrid.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});
        connectionGrid.Children.Add(recordsSurface);
        var fields=Row(Search("connections.options","Search options"),nameBox,hostsBox,userBox,portBox,keyLabel,AsyncAction("Choose public key",PickKeyAsync),passwordBox,rememberPassword);
        installButton=AsyncAction("Install public key",()=>DeployAsync(null),true);cancelButton=Action("Cancel",CancelOperation);cancelButton.IsEnabled=false;
        installButton.IsEnabled=!data.IsEvidence;fields.Children.Add(installButton);fields.Children.Add(cancelButton);fields.Children.Add(progress);
        fields.Children.Add(AsyncAction("Import profile",ImportProfileAsync));fields.Children.Add(AsyncAction("Export profile",ExportProfileAsync));fields.Children.Add(Text("No credential is included in a portable profile."));
        var detail=Card(fields);Grid.SetColumn(detail,1);connectionGrid.Children.Add(detail);pages["Connections"]=Row(connectionGrid);RefreshConnections();UpdateKeyLabel();
    }
    private void Adapt()
    {
        var compact=Root.ActualWidth<1000;NavigationColumn.Width=new GridLength(compact?0:224);NavigationSurface.Visibility=compact?Visibility.Collapsed:Visibility.Visible;CompactTabs.Visibility=compact?Visibility.Visible:Visibility.Collapsed;
        ProductTitle.Visibility=Root.ActualWidth<820?Visibility.Collapsed:Visibility.Visible;
        if(connectionGrid is not null&&recordsSurface is not null)
        {
            var small=Root.ActualWidth<1000;connectionGrid.ColumnDefinitions[0].Width=new GridLength(small?0:280);recordsSurface.Visibility=small?Visibility.Collapsed:Visibility.Visible;
            if(small&&!pages["Connections"].Children.OfType<Button>().Any())pages["Connections"].Children.Insert(0,Action("Search connections",()=>OpenConnectionsDrawer(CompactTabs)));
        }
    }
    private void RefreshConnections()
    {
        if(!searches.TryGetValue("connections.records",out var query))return;
        connectionList.ItemsSource=searchEngine.Filter(connections,c=>c.ToString(),query.Query).ToArray();
    }
    private void ConnectionSelected(object sender,SelectionChangedEventArgs e)
    {
        if(connectionList.SelectedItem is not ConnectionRecord item)return;
        selectedConnection=item.Id;nameBox.Text=item.Name;ApplyConnection(new(){Hosts=item.Hosts,Username=item.Username,Port=item.Port,KeyPath=item.KeyPath});passwordBox.Password="";
    }
    private void NewConnection(){if(busy)return;selectedConnection=null;nameBox.Text="";hostsBox.Text="";userBox.Text="";portBox.Value=22;passwordBox.Password="";selectedKey=null;rememberPassword.IsChecked=false;UpdateKeyLabel();Navigate("Connections");nameBox.Focus(FocusState.Programmatic);}
    private async Task SaveRecordAsync()
    {
        var record=new ConnectionRecord(selectedConnection??Guid.NewGuid(),nameBox.Text.Trim(),hostsBox.Text,userBox.Text.Trim(),Port,selectedKey?.PublicKeyPath);record.Validate();
        var proposed=connections.Where(x=>x.Id!=record.Id).Append(record).ToList();await data.SaveRecordsAsync(proposed);
        data.SaveConnection(CollectConnection());
        if(!data.IsEvidence){if(rememberPassword.IsChecked==true&&passwordBox.Password.Length>0)credentials.Set("ssh-password",passwordBox.Password);else credentials.Delete("ssh-password");}
        connections.Clear();connections.AddRange(proposed);selectedConnection=record.Id;RefreshConnections();Show("Connection saved.");
    }
    private int Port=>double.IsNaN(portBox.Value)?22:checked((int)portBox.Value);
    private AppSettings CollectConnection()=>new(){Hosts=hostsBox.Text,Username=userBox.Text.Trim(),Port=Port,KeyPath=selectedKey?.PublicKeyPath,RememberPassword=rememberPassword.IsChecked==true&&!data.IsEvidence,AutoSave=true,Theme=preferences.Theme.ToString()};
    private void ApplyConnection(AppSettings settings)
    {
        KeyEntry? candidate=null;
        if(!string.IsNullOrWhiteSpace(settings.KeyPath)&&File.Exists(settings.KeyPath)){candidate=ImportValidated(settings.KeyPath);}
        hostsBox.Text=settings.Hosts;userBox.Text=settings.Username;portBox.Value=settings.Port;rememberPassword.IsChecked=settings.RememberPassword;
        selectedKey=candidate;UpdateKeyLabel();
    }
    private KeyEntry ImportValidated(string path)
    {
        if(new FileInfo(path).Length>65536)throw new InvalidDataException("The public-key file exceeds 64 KiB.");
        var key=keyManager.ImportPublicKey(path);SshInput.ValidatePublicKey(key.PublicKeyLine);return key;
    }
    private static string Fingerprint(KeyEntry key)=>"SHA256:"+Convert.ToBase64String(SHA256.HashData(Convert.FromBase64String(key.PublicKeyLine.Split(' ',StringSplitOptions.RemoveEmptyEntries)[1]))).TrimEnd('=');
    private void UpdateKeyLabel()=>keyLabel.Text=selectedKey is null?L("No public keys"):selectedKey.FileName+"\n"+selectedKey.KeyType+" · "+Fingerprint(selectedKey);
    private async Task PickKeyAsync()
    {
        var picker=OpenPicker(".pub","*");var file=await picker.PickSingleFileAsync();if(file is null)return;
        var key=ImportValidated(file.Path);if(keys.All(k=>k.PublicKeyPath!=key.PublicKeyPath))keys.Add(key);selectedKey=key;UpdateKeyLabel();RefreshKeys();Show("Public key imported.");
    }
    private async Task ImportProfileAsync()
    {
        passwordBox.Password="";var file=await OpenPicker(".json").PickSingleFileAsync();if(file is null)return;
        var imported=SettingsStore.Import(file.Path);var merged=SettingsStore.ApplyProfile(CollectConnection(),imported);
        // Parse every referenced key before replacing visible fields or persisted state.
        if(!string.IsNullOrWhiteSpace(merged.KeyPath)){if(!File.Exists(merged.KeyPath))throw new InvalidDataException("The profile's public-key file is unavailable. No fields were applied.");_ = ImportValidated(merged.KeyPath);}
        data.SaveConnection(merged);ApplyConnection(merged);rememberPassword.IsChecked=false;Show("Profile imported. Enter the password to deploy.");
    }
    private async Task ExportProfileAsync(){var file=await SavePicker("connection-profile","JSON profile",".json").PickSaveFileAsync();if(file is not null){SettingsStore.Export(file.Path,CollectConnection());Show("Profile exported without credentials.");}}

    private async Task DeployAsync(IReadOnlyList<DeploymentRequest>? retry)
    {
        if(busy)return;if(data.IsEvidence){Show(L("Evidence profile: SSH and credential storage disabled"),true);return;}
        if(retry is null&&selectedKey is null)throw new ArgumentException("Choose a public key first.");
        var username=userBox.Text.Trim();if(retry is null)SshInput.ValidateUser(username);
        var targets=retry is null?HostTarget.ParseList(hostsBox.Text,Port):Array.Empty<HostTarget>();if(retry is null&&targets.Count==0)throw new ArgumentException("Enter at least one host.");
        var requests=retry??targets.Select(t=>new DeploymentRequest(t.Host,t.Port,username,selectedKey!.PublicKeyLine,selectedKey.PrivateKeyPath??"",selectedKey.PrivateKeyPath is not null)).ToArray();
        foreach(var request in requests)SshInput.ValidateEndpoint(request.Host,request.Port);
        if(passwordBox.Password.Length==0)throw new ArgumentException("Enter the password for this operation.");
        var review=string.Join("\n",requests.Select(r=>$"{r.Username}@{r.Host}:{r.Port}\n{PublicFingerprint(r.PublicKey)}"))+"\n\nThe public key will be appended if missing. Completed changes remain installed if this operation is cancelled.";
        if(!await ConfirmAsync(L("Review installation"),review,L("Continue")))return;
        operation=new CancellationTokenSource();SetBusy(true);progress.IsIndeterminate=true;
        var token=operation.Token;var password=passwordBox.Password;passwordBox.Password="";
        try
        {
            var deployer=new SshDeployer(new HostTrustStore(Path.Combine(data.Root,"trusted-hosts.json")),approval=>ApproveHost(approval,token));
            var service=new SequentialDeploymentService(new SshDeploymentTransport(deployer));
            var updates=new Progress<DeploymentProgress>(p=>{progress.IsIndeterminate=false;progress.Value=p.Total==0?0:100d*p.Completed/p.Total;StatusText.Text=$"{p.Stage} · {p.Host} · {p.Completed}/{p.Total}";});
            var batch=await service.DeployAsync(requests,_=>Task.FromResult<string?>(password),()=>RequestPassphrase(token),updates,token);
            outcomes=retry is null?batch:outcomes.Where(o=>!requests.Contains(o.Request)).Concat(batch).ToArray();
            var completed=batch.Count(o=>o.State is DeploymentState.Installed or DeploymentState.AlreadyPresent);
            Show($"{completed}/{requests.Count} installation outcomes confirmed. Completed changes remain installed."+(token.IsCancellationRequested?" Cancellation has settled.":""),completed!=requests.Count);
            RefreshResults();Navigate("Results");
        }
        finally{password="";SetBusy(false);progress.IsIndeterminate=false;operation.Dispose();operation=null;}
    }
    private bool ApproveHost(HostKeyApproval approval,CancellationToken token)
    {
        var completion=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token);timeout.CancelAfter(TimeSpan.FromMinutes(2));
        using var registration=timeout.Token.Register(()=>completion.TrySetResult(false));
        if(!DispatcherQueue.TryEnqueue(async()=>
        {
            if(closed||timeout.IsCancellationRequested){completion.TrySetResult(false);return;}
            ContentDialog? dialog=null;
            using var cancel=timeout.Token.Register(()=>DispatcherQueue.TryEnqueue(()=>dialog?.Hide()));
            try
            {
                dialog=new ContentDialog {XamlRoot=Root.XamlRoot,Title=L("Host identity approval"),PrimaryButtonText=L("Approve identity"),CloseButtonText=L("Cancel"),DefaultButton=ContentDialogButton.Close,
                    Content=Text($"{approval.Host}:{approval.Port}\nAlgorithm: {approval.Algorithm}\nSHA256:{approval.Fingerprint}\n\nCompare this fingerprint with an independent trusted source. Approval records this exact endpoint identity before authentication. Changed identities are never approved automatically.")};
                var answer=await dialog.ShowAsync();completion.TrySetResult(answer==ContentDialogResult.Primary&&!timeout.IsCancellationRequested);
            }
            catch{completion.TrySetResult(false);}
        }))return false;
        return completion.Task.GetAwaiter().GetResult();
    }
    private string? RequestPassphrase(CancellationToken token)
    {
        var completion=new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token);timeout.CancelAfter(TimeSpan.FromMinutes(2));using var cancel=timeout.Token.Register(()=>completion.TrySetResult(null));
        if(!DispatcherQueue.TryEnqueue(async()=>{if(closed||timeout.IsCancellationRequested){completion.TrySetResult(null);return;}var input=new PasswordBox {Header="Private-key passphrase"};var dialog=new ContentDialog{XamlRoot=Root.XamlRoot,Title="Unlock the selected private key",Content=input,PrimaryButtonText=L("Continue"),CloseButtonText=L("Cancel")};using var registration=timeout.Token.Register(()=>DispatcherQueue.TryEnqueue(dialog.Hide));try{var result=await dialog.ShowAsync();completion.TrySetResult(result==ContentDialogResult.Primary&&!timeout.IsCancellationRequested?input.Password:null);}catch{completion.TrySetResult(null);}finally{input.Password="";}}))return null;
        return completion.Task.GetAwaiter().GetResult();
    }
    private void CancelOperation(){if(operation is null)return;operation.Cancel();StatusText.Text="Cancelling. Waiting for the active operation to settle; completed changes remain installed.";if(cancelButton is not null)cancelButton.IsEnabled=false;GlobalCancel.IsEnabled=false;}
    private void SetBusy(bool value){busy=value;GlobalCancel.Visibility=value?Visibility.Visible:Visibility.Collapsed;GlobalCancel.IsEnabled=value;if(installButton is not null)installButton.IsEnabled=!value&&!data.IsEvidence;if(cancelButton is not null)cancelButton.IsEnabled=value;hostsBox.IsEnabled=!value;userBox.IsEnabled=!value;portBox.IsEnabled=!value;passwordBox.IsEnabled=!value;}
    private async Task<bool> ConfirmAsync(string title,string message,string action)
    {var dialog=new ContentDialog{XamlRoot=Root.XamlRoot,Title=title,Content=Text(message),PrimaryButtonText=action,CloseButtonText=L("Cancel"),DefaultButton=ContentDialogButton.Close};return await dialog.ShowAsync()==ContentDialogResult.Primary;}

    private void BuildKeys()
    {
        keyList.SelectionChanged+=(_,_)=>{if(keyList.SelectedItem is KeyEntry key){selectedKey=key;UpdateKeyLabel();}};
        pages["Keys"]=Row(Card(Row(Search("keys.list","Search keys",(_,_)=>RefreshKeys()),keyList,AsyncAction("Import public key",PickKeyAsync),AsyncAction("Generate key",GenerateKeyAsync,true),AsyncAction("Export public key",ExportKeyAsync),Action("Refresh",()=>{if(!data.IsEvidence){keys.Clear();keys.AddRange(keyManager.FindExistingKeys().Where(k=>ValidKey(k.PublicKeyLine)));RefreshKeys();}}))));RefreshKeys();
    }
    private void RefreshKeys(){if(searches.TryGetValue("keys.list",out var query))keyList.ItemsSource=searchEngine.Filter(keys,k=>k.Display,query.Query).ToArray();}
    private async Task GenerateKeyAsync()
    {
        if(busy)return;if(data.IsEvidence){Show("Key generation is disabled in the evidence profile.",true);return;}
        var supported=keyManager.GetSupportedAlgorithms();string algorithm=supported[0];
        var destination=new TextBox{Header=L("Output path"),Text=Path.Combine(KeyManager.SshDir,"id_"+algorithm+"_new")};
        var comment=new TextBox{Header=L("Comment"),Text="WinSshCopyId"};
        var choices=Row(Text("Algorithm"));foreach(var available in supported){var choice=new RadioButton{Content=available=="rsa"?"RSA · 3072 bits":"Ed25519",GroupName="key-algorithm",IsChecked=available==algorithm};choice.Checked+=(_,_)=>algorithm=available;choices.Children.Add(choice);}
        var browse=AsyncAction("Browse",async()=>{var picker=new FolderPicker();picker.FileTypeFilter.Add("*");WinRT.Interop.InitializeWithWindow.Initialize(picker,WinRT.Interop.WindowNative.GetWindowHandle(this));var folder=await picker.PickSingleFolderAsync();if(folder is not null)destination.Text=Path.Combine(folder.Path,Path.GetFileName(destination.Text));});
        var error=Text("");var content=Row(new ScopedSearchBox("keys.generate.options",L("Search options")),choices,destination,browse,comment,Text("A private file and its .pub public file will be created. Existing files are never overwritten: choose a new basename if either exists. The private key is unencrypted with owner-only permissions."),error);
        var dialog=new ContentDialog{XamlRoot=Root.XamlRoot,Title=L("Generate key"),Content=new ScrollViewer{Content=content,MaxHeight=480},PrimaryButtonText=L("Generate key"),CloseButtonText=L("Cancel")};
        dialog.PrimaryButtonClick+=(_,e)=>{try{var target=Path.GetFullPath(destination.Text);if(!Path.IsPathFullyQualified(destination.Text)||target.EndsWith(".pub",StringComparison.OrdinalIgnoreCase)||File.Exists(target)||File.Exists(target+".pub")||Directory.Exists(target))throw new ArgumentException("Choose a new absolute identity basename without .pub; existing files will not be overwritten.");if(comment.Text.Any(char.IsControl))throw new ArgumentException("The comment cannot contain control characters.");}catch(Exception ex){e.Cancel=true;error.Text=SafeError(ex);}};
        if(await dialog.ShowAsync()!=ContentDialogResult.Primary)return;
        var output=destination.Text;var annotation=comment.Text;operation=new CancellationTokenSource();SetBusy(true);progress.IsIndeterminate=true;
        try{var key=await Task.Run(()=>keyManager.GenerateKey(output,algorithm,annotation,_=>{},operation.Token));SshInput.ValidatePublicKey(key.PublicKeyLine);keys.Add(key);selectedKey=key;RefreshKeys();UpdateKeyLabel();Show("Key generated and private-file permissions verified. Private-key contents were not displayed or exported.");}
        finally{SetBusy(false);progress.IsIndeterminate=false;operation.Dispose();operation=null;}
    }
    private async Task ExportKeyAsync(){if(selectedKey is null)throw new ArgumentException("Choose a public key first.");var file=await SavePicker(selectedKey.FileName,"OpenSSH public key",".pub").PickSaveFileAsync();if(file is not null){await File.WriteAllTextAsync(file.Path,selectedKey.PublicKeyLine+Environment.NewLine,new UTF8Encoding(false));Show("Public key exported.");}}
    private void BuildResults()
    {
        resultList.SelectionChanged+=(_,_)=>{if(resultList.SelectedItem is OutcomeRow item)resultDetail.Text=OutcomeText(item.Outcome);};
        pages["Results"]=Row(Card(Row(Search("results.list","Search results",(_,_)=>RefreshResults()),resultList,resultDetail,AsyncAction("Retry failed targets",RetryFailedAsync),Action("Copy command",CopyResultCommands),AsyncAction("Export results",ExportResultsAsync))));
    }
    private sealed record OutcomeRow(DeploymentOutcome Outcome){public override string ToString()=>$"{Outcome.Request.Username}@{Outcome.Request.Host}:{Outcome.Request.Port}\n{Outcome.State} · {(Outcome.Verified?"Key login verified":"Key login not verified")}";}
    private static string PublicFingerprint(string publicKey)=>"SHA256:"+Convert.ToBase64String(SHA256.HashData(Convert.FromBase64String(publicKey.Split(' ',StringSplitOptions.RemoveEmptyEntries)[1]))).TrimEnd('=');
    private string OutcomeText(DeploymentOutcome o)=>$"{o.Request.Username}@{o.Request.Host}:{o.Request.Port}\nOutcome: {o.State}\nInstallation: {o.Installation}\nVerification: {o.Verification}\nCancellation requested: {o.CancellationRequested}\nNext safe action: {o.RetryAction}\nPublic key: {PublicFingerprint(o.Request.PublicKey)}\nHost fingerprint: {o.Fingerprint??"Unavailable"}\n{o.Error??""}\nCompleted remote changes are not rolled back by cancellation."+(o.HostKeyChange is {} changed?$"\nHOST IDENTITY CHANGED: STOPPED\nAlgorithm: {changed.Algorithm}\nPrevious SHA256:{changed.ExpectedFingerprint}\nObserved SHA256:{changed.ObservedFingerprint}\nInvestigate this change independently. Automatic replacement and retry are not available.":"");
    private void RefreshResults(){if(searches.TryGetValue("results.list",out var query)){var rows=outcomes.Select(o=>new OutcomeRow(o));resultList.ItemsSource=searchEngine.Filter(rows,r=>OutcomeText(r.Outcome),query.Query).ToArray();if(outcomes.Count==0)resultDetail.Text=L("No results yet");}}
    private async Task RetryFailedAsync()
    {
        if(data.IsEvidence){Show(L("Evidence profile: SSH and credential storage disabled"),true);return;}
        var selected=SelectedOutcomes().ToArray();var eligible=selected.Where(o=>o.IsRetryableFailure&&o.HostKeyChange is null).ToArray();
        if(eligible.Length==0){Show("No selected target can be retried automatically. Completed, changed-identity and uncertain outcomes are excluded; inspect uncertain targets before another installation.",true);return;}
        if(!await ConfirmAsync("Review failed-target retry",string.Join("\n",eligible.Select(o=>$"{o.Request.Host}:{o.Request.Port} · {o.RetryAction}"))+$"\n\n{selected.Length-eligible.Length} selected targets excluded. Verification-only retries never reinstall the key.",L("Continue")))return;
        var verification=eligible.Where(o=>o.RetryAction==DeploymentRetryAction.VerifyOnly).ToArray();
        if(verification.Length>0)
        {
            operation=new CancellationTokenSource();SetBusy(true);var token=operation.Token;
            try
            {
                var deployer=new SshDeployer(new HostTrustStore(Path.Combine(data.Root,"trusted-hosts.json")),a=>ApproveHost(a,token));var updated=outcomes.ToList();
                foreach(var previous in verification)
                {
                    if(token.IsCancellationRequested)break;DeploymentOutcome replacement=previous;
                    try{if(string.IsNullOrEmpty(previous.Fingerprint))throw new InvalidDataException("The installation fingerprint is unavailable.");var verified=await Task.Run(()=>deployer.VerifyKeyLogin(previous.Request.Host,previous.Request.Port,previous.Request.Username,previous.Request.PrivateKeyPath,previous.Fingerprint,_=>{},()=>RequestPassphrase(token),token));replacement=previous with{Verified=verified,Verification=verified?VerificationState.Succeeded:VerificationState.Failed,RetryAction=verified?DeploymentRetryAction.None:DeploymentRetryAction.VerifyOnly,Error=verified?null:"Key login verification failed. Installation remains in place."};}
                    catch(HostKeyChangedException changed){replacement=previous with{Verified=false,Verification=VerificationState.Failed,HostKeyChange=changed,RetryAction=DeploymentRetryAction.Reconcile,Error=changed.Message};}
                    catch(Exception){replacement=previous with{Verified=false,Verification=token.IsCancellationRequested?VerificationState.Cancelled:VerificationState.Failed,CancellationRequested=token.IsCancellationRequested,Error="Verification did not complete. Installed changes remain in place."};}
                    var index=updated.IndexOf(previous);if(index>=0)updated[index]=replacement;
                }
                outcomes=updated;RefreshResults();Show("Verification retry settled. Installation state was retained.");
            }
            finally{SetBusy(false);operation.Dispose();operation=null;}
        }
        var installs=eligible.Where(o=>o.RetryAction==DeploymentRetryAction.Deploy).Select(o=>o.Request).ToArray();if(installs.Length>0)await DeployAsync(installs);
    }
    private IEnumerable<DeploymentOutcome> SelectedOutcomes()=>resultList.SelectedItems.Count>0?resultList.SelectedItems.OfType<OutcomeRow>().Select(x=>x.Outcome):resultList.Items.OfType<OutcomeRow>().Select(x=>x.Outcome);
    private void CopyResultCommands()
    {
        var selected=SelectedOutcomes().ToArray();if(selected.Length==0){Show(L("No results yet"));return;}
        var commands=selected.Where(o=>!string.IsNullOrWhiteSpace(o.Request.PrivateKeyPath)).Select(o=>SshInput.BuildConnectCommand(o.Request.Host,o.Request.Port,o.Request.Username,o.Request.PrivateKeyPath));var text=string.Join(Environment.NewLine,commands);if(text.Length==0){Show("No local private-key path is available for these commands.",true);return;}var package=new DataPackage();package.SetText(text);Clipboard.SetContent(package);Show("PowerShell-safe connection commands copied.");
    }
    private async Task ExportResultsAsync(){var selected=SelectedOutcomes().ToArray();if(selected.Length==0)throw new ArgumentException("There are no results in this selection.");var file=await SavePicker("ssh-results","JSON results",".json").PickSaveFileAsync();if(file is null)return;var exported=selected.Select(o=>new{o.Request.Host,o.Request.Port,o.Request.Username,State=o.State.ToString(),o.Verified,o.Fingerprint,o.Error});await File.WriteAllTextAsync(file.Path,JsonSerializer.Serialize(exported,new JsonSerializerOptions{WriteIndented=true}));Show("Filtered results exported. Credentials and private-key data were omitted.");}

    private void BuildSettings()
    {
        var panel=Row(Search("settings.general","Search options"));
        var displayName=new TextBox{Header=L("Display name"),Text=preferences.DisplayName};panel.Children.Add(displayName);
        void Choices<T>(string heading,IEnumerable<T> values,T current,Action<T> apply) where T:struct,Enum
        {var group=Row(Text(heading));foreach(var value in values){var radio=new RadioButton{Content=L(value.ToString()),IsChecked=EqualityComparer<T>.Default.Equals(current,value),GroupName=heading};radio.Checked+=(_,_)=>apply(value);group.Children.Add(radio);}panel.Children.Add(Card(group));}
        Choices("Theme",Enum.GetValues<WorkspaceTheme>(),preferences.Theme,v=>{preferences=preferences with{Theme=v};ApplyPreferences();});
        Choices("Language",Enum.GetValues<WorkspaceLanguage>(),preferences.Language,v=>preferences=preferences with{Language=v});
        Choices("Density",Enum.GetValues<WorkspaceDensity>(),preferences.Density,v=>{preferences=preferences with{Density=v};ApplyPreferences();});
        var en=new Slider{Minimum=1,Maximum=5,StepFrequency=1,Value=preferences.EnglishFunnyLevel,Header=L("English funny level")};en.ValueChanged+=(_,_)=>{preferences=preferences with{EnglishFunnyLevel=(int)en.Value};StatusText.Text=WorkspaceText.Ready(preferences);};panel.Children.Add(en);
        var yue=new Slider{Minimum=1,Maximum=5,StepFrequency=1,Value=preferences.CantoneseFunnyLevel,Header=L("Cantonese funny level")};yue.ValueChanged+=(_,_)=>{preferences=preferences with{CantoneseFunnyLevel=(int)yue.Value};StatusText.Text=WorkspaceText.Ready(preferences);};panel.Children.Add(yue);
        panel.Children.Add(Text("Funny levels style message tone; operation facts do not change. Both languages default to level 5. Full message-category coverage is still being implemented."));
        var reduced=new CheckBox{Content=L("Reduced motion"),IsChecked=preferences.ReducedMotion};var visible=new CheckBox{Content=L("Show accessibility control"),IsChecked=preferences.AccessibilityButtonVisible};panel.Children.Add(reduced);panel.Children.Add(visible);
        panel.Children.Add(AsyncAction("Save settings",async()=>{preferences=(preferences with{DisplayName=displayName.Text.Trim(),ReducedMotion=reduced.IsChecked==true,AccessibilityButtonVisible=visible.IsChecked==true}).Validate();await data.Settings.SaveAsync(preferences);ApplyPreferences();DetachFields();BuildPages();Navigate("Settings");Show("Settings saved.");},true));
        panel.Children.Add(Text("Stored in the private workspace settings record. Compiled defaults: system theme, comfortable density, English, 14-point UI text. Advanced appearance, per-element locks and schedules remain incomplete."));pages["Settings"]=Row(Card(panel));
    }
    private void DetachFields()
    {
        PageHost.Children.Clear();foreach(var element in new FrameworkElement[]{nameBox,hostsBox,userBox,portBox,passwordBox,rememberPassword,keyLabel,connectionList,keyList,resultList,resultDetail,progress,notificationList})if(element.Parent is Panel parent)parent.Children.Remove(element);
    }
    private void BuildTools()
    {
        var regex=Search("tools.regex","Regex workbench");pages["Tools"]=Row(Card(Row(Text("Regex workbench",true),regex,Text("Use the adjacent builder to evaluate local patterns, captures and replacements."))),Card(Row(Text("Additional tools",true),Text("Converter, local-model and server services are present, but their complete native management surfaces are not implemented. No installation, server lifecycle or model download is implied by this status."))));
    }
    private readonly ListView notificationList=new(){MaxHeight=500};
    private void BuildHistory(){pages["History"]=Row(Card(Row(Search("history.notifications","Search results",(_,_)=>RefreshHistory()),notificationList,Action("Refresh",RefreshHistory),Text("This view shows the current session's notifications. Persistent revision browsing and restore remain incomplete."))));}
    private void RefreshHistory(){if(searches.TryGetValue("history.notifications",out var query))notificationList.ItemsSource=searchEngine.Filter(notifications,x=>x,query.Query).Reverse().ToArray();}
    private void BuildHelp(){pages["Help"]=Row(Card(Row(Search("help.articles","Search options"),Text("Install a public key",true),Text("1. Enter the destination and account.\n2. Choose an OpenSSH public key.\n3. Review the exact targets.\n4. Independently verify a new host fingerprint before approval.\n5. Inspect each result. A cancelled operation can retain completed remote changes."),Text("Keyboard: Ctrl+Shift+F opens Commands. Tab moves between controls. Native title-bar controls support window management."),Text("About",true),Text("WinSshCopyId · Native Windows SSH key installation\nThis development workspace does not claim full required-feature completion."))));}
    private void Show(string message,bool error=false)
    {
        if(closed)return;Notice.Message=message;Notice.Severity=error?InfoBarSeverity.Error:InfoBarSeverity.Informational;Notice.IsOpen=true;StatusText.Text=message;notifications.Add(DateTimeOffset.Now.ToString("HH:mm:ss")+" · "+message);if(notifications.Count>200)notifications.RemoveAt(0);
    }
    private FileOpenPicker OpenPicker(params string[] extensions){var picker=new FileOpenPicker();WinRT.Interop.InitializeWithWindow.Initialize(picker,WinRT.Interop.WindowNative.GetWindowHandle(this));foreach(var extension in extensions)picker.FileTypeFilter.Add(extension);return picker;}
    private FileSavePicker SavePicker(string name,string description,string extension){var picker=new FileSavePicker{SuggestedFileName=name};WinRT.Interop.InitializeWithWindow.Initialize(picker,WinRT.Interop.WindowNative.GetWindowHandle(this));picker.FileTypeChoices.Add(description,new List<string>{extension});return picker;}
    private void OpenNavigation(FrameworkElement anchor,string scope)
    {
        var list=new ListView{MaxHeight=380};var query=new ScopedSearchBox(scope,L("Workspace tabs"));void Filter()=>list.ItemsSource=searchEngine.Filter(destinations,x=>L(x),query.Query).ToArray();query.QueryChanged+=(_,_)=>Filter();Filter();
        var flyout=new Flyout{Content=Row(query,list),Placement=Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Bottom};list.SelectionChanged+=(_,_)=>{if(list.SelectedItem is string target){flyout.Hide();Navigate(target);CompactTabs.Focus(FocusState.Programmatic);}};flyout.ShowAt(anchor);
    }
    private void OpenConnectionsDrawer(FrameworkElement anchor)
    {
        var query=new ScopedSearchBox("connections.compact",L("Search connections"));var list=new ListView{MaxHeight=380};void Filter()=>list.ItemsSource=searchEngine.Filter(connections,c=>c.ToString(),query.Query).ToArray();query.QueryChanged+=(_,_)=>Filter();Filter();var flyout=new Flyout{Content=Row(query,list,Action("New connection",NewConnection))};list.SelectionChanged+=(_,_)=>{if(list.SelectedItem is ConnectionRecord item){connectionList.SelectedItem=item;flyout.Hide();}};flyout.ShowAt(anchor);
    }
    private void OpenPalette(FrameworkElement anchor)=>OpenNavigation(anchor,"palette.commands");
    private void GlobalCancel_Click(object sender,RoutedEventArgs e)=>CancelOperation();
    private void Palette_Click(object sender,RoutedEventArgs e)=>OpenPalette(PaletteButton);
    private void CompactTabs_Click(object sender,RoutedEventArgs e)=>OpenNavigation(CompactTabs,"tabs.compact");
    private void Accessibility_Click(object sender,RoutedEventArgs e){Navigate("Settings");Show("Accessibility controls are available in Settings. The visibility preference can always be restored there.");}
}
