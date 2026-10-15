using System.Text;
using WinSshCopyId.Workspace;
using WinSshCopyId.Workspace.Security;

var passed = 0;
void Check(bool value, string name) { if (!value) throw new Exception(name); passed++; }
void Reject(Action action, string name) { try { action(); } catch (Exception e) when (e is InvalidDataException or System.Text.Json.JsonException or ArgumentException) { passed++; return; } throw new Exception(name); }
var defaults = new WorkspaceSettings();
Check(defaults.EnglishFunnyLevel == 5 && defaults.CantoneseFunnyLevel == 5 && !defaults.Narrator.Enabled && !defaults.Attention.Focus, "defaults");
Reject(() => (defaults with { EnglishFunnyLevel = 0 }).Validate(), "invalid level");
var memory = new MemoryStorage(); var settings = new WorkspaceSettingsStore(memory);
await settings.SaveAsync(defaults with { DisplayName = "Custom" });
Check((await settings.LoadAsync()).Settings.DisplayName == "Custom", "settings roundtrip");
memory.Data["settings.json.bak"] = memory.Data["settings.json"]; memory.Data["settings.json"] = [0xff];
Check((await settings.LoadAsync()).Recovered, "backup recovery");
var search = new WorkspaceSearch();
var matches = search.Evaluate("a1 a2", new(@"a(?<digit>\d)", true), "${digit}");
Check(matches.Matches.Count == 2 && matches.Matches[0].Captures.Any(c => c.Group == "digit") && matches.ReplacementPreview == "1 2", "captures replacement");
Check(search.Evaluate("a", new("[", true)).ValidationError != null, "invalid regex");
Check(search.Evaluate("a.b", new(".")).Matches.Count == 1, "literal default");
Check(search.Evaluate(new string('a', 2000), new("a", true)).Limited, "match limit");
var tabs = new WorkspaceSession().Open("First", "profiles").Open("Second", "history");
Check(tabs.Close(tabs.ActiveTabId!.Value).Tabs.Count == 1, "tab close");
var vocabulary = new PersonalVocabulary(memory);
await vocabulary.ImportAsync(Encoding.UTF8.GetBytes("{\"schemaVersion\":1,\"entries\":{\"Greeting\":\"Welcome\"}}"));
Check(vocabulary.ApplyPrivateText("Greeting") == "Welcome", "mapping");
Reject(() => PersonalVocabulary.Validate(Encoding.UTF8.GetBytes("{\"schemaVersion\":1,\"entries\":{\"a\":\"x\",\"a\":\"y\"}}")), "duplicate");
Reject(() => PersonalVocabulary.Validate(Encoding.UTF8.GetBytes("{\"schemaVersion\":1,\"entries\":{\"__proto__\":\"x\"}}")), "unsafe");
Reject(() => PersonalVocabulary.Validate(Encoding.UTF8.GetBytes("{\"schemaVersion\":2,\"entries\":{}}")), "version");
await vocabulary.ClearAsync(); Check(vocabulary.ApplyPrivateText("Greeting") == "Greeting", "clear fallback");
var rule = new SettingsSchedule(Guid.NewGuid(), "Overnight", true, new(22,0), new(6,0), new HashSet<DayOfWeek>{DayOfWeek.Monday}, new(Theme: WorkspaceTheme.Dark));
Check(rule.Matches(DateTimeOffset.Parse("2026-10-13T02:00:00Z"), TimeZoneInfo.Utc), "cross midnight weekday");
Check(!rule.Matches(DateTimeOffset.Parse("2026-10-13T06:00:00Z"), TimeZoneInfo.Utc), "exclusive end");
Check((rule with {StartTime = new(6,0), EndTime = new(6,0), Weekdays = new HashSet<DayOfWeek>()}).Matches(DateTimeOffset.UtcNow, TimeZoneInfo.Utc), "full day");
var doc = new ScheduleDocument(1, "UTC", [rule with {Weekdays = new HashSet<DayOfWeek>(),StartTime = new(0,0),EndTime = new(0,0)}, rule with {Weekdays = new HashSet<DayOfWeek>(),StartTime = new(0,0),EndTime = new(0,0),Values = new(Theme: WorkspaceTheme.Light)}]);
// Stable identifiers must be distinct even when rules otherwise match.
doc = doc with { Rules = [doc.Rules[0], doc.Rules[1] with {Id = Guid.NewGuid()}] };
Check(doc.Evaluate(defaults, DateTimeOffset.UtcNow).Theme == WorkspaceTheme.Light, "later wins");
var scheduleStore = new ScheduleStore(memory);
await scheduleStore.SaveAsync(doc);
Check((await scheduleStore.LoadAsync())!.Rules.Count == 2, "schedule persistence");
var sessionStore = new WorkspaceSessionStore(memory); await sessionStore.SaveAsync(tabs);
Check((await sessionStore.LoadAsync()).Tabs.Count == 2, "tab persistence");
var external = new ExternalSettingsSource(new FakeTransport("{\"Version\":1,\"Values\":{\"Theme\":2}}"), _ => "synthetic");
Check((await external.FetchAsync(new(ScheduleSourceKind.Https,"https://example.com"),new()))!.Theme == WorkspaceTheme.Dark,"external schema");
var boolean = new ExternalSettingsSource(new FakeTransport("{\"state\":\"off\"}"), _ => "synthetic");
Check(await boolean.FetchAsync(new(ScheduleSourceKind.HomeAssistant,"https://example.com","input_boolean.mode","reference"),new()) is null,"boolean off");
var delayed = new DelayedTransport();
await using(var coordinator=new ScheduleCoordinator(new ExternalSettingsSource(delayed,_=>null),new SystemClock()))
{
    var externalRule=doc.Rules[0] with {Source=new(ScheduleSourceKind.Https,"https://example.com")};
    var oldTask=coordinator.RefreshAsync(defaults,new(1,"UTC",[externalRule]));
    await delayed.Started.Task;
    var newest=await coordinator.RefreshAsync(defaults,new(1,"UTC",[doc.Rules[1]]));
    delayed.Release.SetResult(Encoding.UTF8.GetBytes("{\"Version\":1,\"Values\":{\"Theme\":2}}"));
    Check(await oldTask is null&&coordinator.Current!.Effective.Theme==WorkspaceTheme.Light,"stale source response suppressed");
    var restored=await coordinator.RefreshAsync(defaults,new(1,"UTC",[]));
    Check(restored!.Effective.Theme==defaults.Theme,"base restored when schedule ends");
}
ExternalSettingsSource.ValidateUri(new("https://192.168.1.5"),new("192.168.1.5",["192.168.1.5"]));passed++;
Reject(()=>ExternalSettingsSource.ValidateUri(new("https://192.168.1.6"),new("192.168.1.5",["192.168.1.5"])),"private target exact authority");
Reject(() => ExternalSettingsSource.ValidateUri(new("http://example.com")), "http rejected");
Reject(() => ExternalSettingsSource.ValidateUri(new("https://127.0.0.1")), "loopback rejected");
Reject(() => ExternalSettingsSource.ValidateUri(new("https://user:secret@example.com")), "userinfo rejected");
var history = new WorkspaceHistory("unused", null, new ProcessExecutor(), new SystemClock());
Check(!(await history.RecordAsync(Guid.NewGuid(), "created", "profile")).Recorded, "missing git honest");
Check(Convert.ToHexString(SharedScrypt.Derive("synthetic-pass",Convert.FromHexString("00112233445566778899aabbccddeeff"))).ToLowerInvariant() == "f0b1826e2e45130c0a5f936d684e368098266673770ed903345ced729a21d2ef", "shared protocol scrypt compatibility");
var temporary = Path.Combine(Path.GetTempPath(), "WinSshCopyId-workspace-check-" + Guid.NewGuid().ToString("N"));
try
{
    var privateStorage = new DirectoryPrivateStorage(temporary);
    await privateStorage.WriteAtomicAsync("settings.json", Encoding.UTF8.GetBytes("first"));
    await privateStorage.WriteAtomicAsync("settings.json", Encoding.UTF8.GetBytes("second"));
    Check(Encoding.UTF8.GetString((await privateStorage.ReadAsync("settings.json.bak"))!) == "first", "atomic backup bytes");
    Reject(() => privateStorage.ReadAsync("../outside").GetAwaiter().GetResult(), "storage traversal");
    var git = HistoryGitResolver.Resolve(AppContext.BaseDirectory);
    if (git is not null)
    {
        var realHistory = new WorkspaceHistory(Path.Combine(temporary,"history"),git,new ProcessExecutor(),new SystemClock());
        Check((await realHistory.RecordAsync(Guid.NewGuid(),"created","profile")).Recorded,"genuine history commit");
        Check((await realHistory.ReadAsync()).Count == 1,"history browse metadata");
    }
    else throw new Exception("Git is required for this integration check.");
    var vault = new Dictionary<string,string>();
    using var protectedHistory = new ProtectedWorkspaceHistory(Path.Combine(temporary,"protected"),git,new ProcessExecutor(),new SystemClock(),key=>vault.GetValueOrDefault(key),(key,value)=>vault[key]=value);
    protectedHistory.ConfigureCredential("synthetic-pass");
    try { using var wrong = protectedHistory.Open("wrong"); throw new Exception("wrong history credential"); } catch(UnauthorizedAccessException) { passed++; }
    using var access = protectedHistory.Open("synthetic-pass");
    var recordId=Guid.NewGuid();
    Check((await protectedHistory.RecordAsync(access,recordId,"created","document",Encoding.UTF8.GetBytes("{\"text\":\"first private content\"}"))).Recorded,"encrypted history commit");
    var revision=(await protectedHistory.BrowseAsync(access)).Single();
    Check(!Directory.EnumerateFiles(Path.Combine(temporary,"protected"),"*.json").Select(File.ReadAllText).Any(text=>text.Contains("first private content")),"history encrypted at rest");
    Check(Encoding.UTF8.GetString(await protectedHistory.ReadSnapshotAsync(access,revision.Id)).Contains("first private content"),"history decrypt");
    Check((await protectedHistory.RecordAsync(access,recordId,"updated","document",Encoding.UTF8.GetBytes("{\"text\":\"second\"}"))).Recorded,"history update");
    var newer=(await protectedHistory.BrowseAsync(access)).First();
    Check((await protectedHistory.DiffAsync(access,revision.Id,newer.Id)).Changed,"history diff");
    var applied=false;
    Check((await protectedHistory.RestoreAsync(access,revision.Id,(data,cancel)=>{applied=Encoding.UTF8.GetString(data.Span).Contains("first private content");return Task.CompletedTask;})).Recorded&&applied,"append only restore");
    Check((await protectedHistory.BrowseAsync(access)).Count==3,"restore adds revision");
    Check(!Encoding.UTF8.GetString(await protectedHistory.ExportRedactedAsync(access)).Contains("first private content"),"redacted export");
    var modePath=Path.Combine(temporary,"shared","school-mode.v1.json");
    await using var mode=new SharedSchoolMode(modePath); await mode.StartAsync();
    Check(mode.Snapshot.Known&&!mode.Snapshot.Restricted,"shared missing record");
    await mode.ConfigureAsync("Study time","pin","1234"); await mode.SetEnabledAsync(true,null);
    Check(mode.Snapshot.Restricted&&mode.Snapshot.DisplayName=="Study time","shared enabled chosen name");
    Check(mode.Snapshot.Project(defaults with {Language=WorkspaceLanguage.Cantonese}).Language==WorkspaceLanguage.English,"shared effective projection");
    try {await mode.SetEnabledAsync(false,"9999");throw new Exception("wrong shared credential");}catch(UnauthorizedAccessException){passed++;}
    await mode.SetEnabledAsync(false,"1234");
    for(var i=0;i<20&&mode.Snapshot.Restricted;i++){await Task.Delay(20);await mode.ReloadAsync();}
    Check(!mode.Snapshot.Restricted,"shared verified disable");
    await using var secondMode=new SharedSchoolMode(modePath);await secondMode.StartAsync();
    var liveRename=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    secondMode.Changed+=(_,snapshot)=>{if(snapshot.Known&&snapshot.DisplayName=="Quiet desk")liveRename.TrySetResult();};
    await mode.RenameAsync("Quiet desk");await liveRename.Task.WaitAsync(TimeSpan.FromSeconds(3));
    Check(secondMode.Snapshot.DisplayName=="Quiet desk","shared cross instance rename");
    Check(!System.Text.Json.JsonSerializer.Serialize(secondMode.Snapshot).Contains("Verifier"),"shared snapshot excludes verifier");
}
finally
{
    if (Directory.Exists(temporary))
    {
        foreach(var file in Directory.EnumerateFiles(temporary,"*",SearchOption.AllDirectories)) File.SetAttributes(file,FileAttributes.Normal);
        Directory.Delete(temporary,true);
    }
}
var queue = new BoundedQueue<int>(2); queue.Enqueue(1);queue.Enqueue(2);queue.Enqueue(3);Check(queue.Snapshot().SequenceEqual([2,3]), "queue bound");
var securityStorage=new MemoryStorage();var securityVault=new Dictionary<string,string>();
var delegates=new VaultDelegates(key=>securityVault.GetValueOrDefault(key),(key,value)=>securityVault[key]=value,key=>securityVault.Remove(key));
using(var security=new WorkspaceSecuritySession(securityStorage,delegates,new SystemClock()))
{
    Check(await security.LoadAsync(),"security first load");
    await security.CreateAsync("profile-row",LockPolicy.Pin,["1234"],TimeSpan.FromMinutes(1),1);
    Check(!await security.AuthenticateAsync("profile-row",["9999"]),"security wrong answer");
    Check((await security.StatusAsync("profile-row")).WaitingUntil is not null,"security wait recorded");
}
using(var restarted=new WorkspaceSecuritySession(securityStorage,delegates,new SystemClock()))
{
    Check(await restarted.LoadAsync(),"security persisted reload");
    var state=await restarted.StatusAsync("profile-row");
    Check(state.Locked&&state.WaitingUntil is not null&&state.AttemptsRemaining==0,"security restart keeps wait budget and lock");
}
securityStorage.Data["security-state.json"][20]^=1;
using(var corrupted=new WorkspaceSecuritySession(securityStorage,delegates,new SystemClock()))Check(!await corrupted.LoadAsync()&&!corrupted.IsReady,"security corrupt state unavailable");
Console.WriteLine($"PASS {passed} workspace checks");

sealed class MemoryStorage : IPrivateStorage
{
    public Dictionary<string, byte[]> Data {get;} = new();
    public Task<byte[]?> ReadAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult(Data.GetValueOrDefault(key));
    public Task WriteAtomicAsync(string key, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default) {Data[key] = data.ToArray();return Task.CompletedTask;}
    public Task DeleteAsync(string key, CancellationToken cancellationToken = default) {Data.Remove(key);return Task.CompletedTask;}
}
sealed class FakeTransport(string json) : ISettingsSourceTransport
{
    public Task<byte[]> GetAsync(Uri uri,string? bearerCredential,CancellationToken cancellationToken) => Task.FromResult(Encoding.UTF8.GetBytes(json));
}
sealed class DelayedTransport : ISettingsSourceTransport
{
    public TaskCompletionSource Started {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource<byte[]> Release {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
    public async Task<byte[]> GetAsync(Uri uri,string? bearerCredential,CancellationToken cancellationToken){Started.SetResult();return await Release.Task;}
}
