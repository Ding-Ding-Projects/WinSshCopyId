using System.Text;
using WinSshCopyId.Workspace;

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
Reject(() => ExternalSettingsSource.ValidateUri(new("http://example.com")), "http rejected");
Reject(() => ExternalSettingsSource.ValidateUri(new("https://127.0.0.1")), "loopback rejected");
Reject(() => ExternalSettingsSource.ValidateUri(new("https://user:secret@example.com")), "userinfo rejected");
var history = new WorkspaceHistory("unused", null, new ProcessExecutor(), new SystemClock());
Check(!(await history.RecordAsync(Guid.NewGuid(), "created", "profile")).Recorded, "missing git honest");
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
