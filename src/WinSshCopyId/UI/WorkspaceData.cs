using System.Text.Json;
using WinSshCopyId.Services;
using WinSshCopyId.Workspace;
namespace WinSshCopyId.UI;

public sealed class WorkspaceData
{
    public string Root { get; }
    public bool IsEvidence { get; }
    public IPrivateStorage Storage { get; }
    public WorkspaceSettingsStore Settings { get; }
    public WorkspaceSessionStore Sessions { get; }
    public WorkspaceData(string? evidenceDirectory)
    {
        IsEvidence = evidenceDirectory is not null;
        Root = evidenceDirectory is null ? SettingsStore.Directory : Path.GetFullPath(evidenceDirectory);
        if (IsEvidence && (!Path.IsPathFullyQualified(evidenceDirectory!) || string.Equals(Root.TrimEnd(Path.DirectorySeparatorChar), SettingsStore.Directory.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("The evidence profile must be an absolute, separate application-data directory.");
        Storage = new DirectoryPrivateStorage(Path.Combine(Root, "workspace"));
        Settings = new(Storage); Sessions = new(Storage);
    }
    public AppSettings LoadConnection()
    {
        if (!IsEvidence) return SettingsStore.Load();
        var path = Path.Combine(Root, "connection.json");
        return File.Exists(path) ? SettingsStore.Import(path) : new();
    }
    public void SaveConnection(AppSettings settings)
    {
        settings.ProtectedPassword = null;
        if (IsEvidence) SettingsStore.Export(Path.Combine(Root, "connection.json"), settings);
        else SettingsStore.Save(settings);
    }
    public async Task<IReadOnlyList<ConnectionRecord>> LoadRecordsAsync()
    {
        var bytes = await Storage.ReadAsync("connections.json");
        if (bytes is null) return [];
        var records = JsonSerializer.Deserialize<List<ConnectionRecord>>(bytes) ?? [];
        if (records.Count > 1000 || records.Any(r => r.Id == Guid.Empty || r.Name.Length > 120 || r.Hosts.Length > 30000)) throw new InvalidDataException("The connection list is invalid.");
        foreach (var record in records) record.Validate();
        return records;
    }
    public Task SaveRecordsAsync(IEnumerable<ConnectionRecord> records) => Storage.WriteAtomicAsync("connections.json", JsonSerializer.SerializeToUtf8Bytes(records));
}
public sealed record ConnectionRecord(Guid Id, string Name, string Hosts, string Username, int Port, string? KeyPath)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name) || Name.Length > 120) throw new ArgumentException("Enter a name of at most 120 characters.");
        if (Port is < 1 or > 65535) throw new ArgumentException("Port must be between 1 and 65535.");
        if (!string.IsNullOrWhiteSpace(Username)) SshInput.ValidateUser(Username);
        foreach (var target in HostTarget.ParseList(Hosts, Port)) SshInput.ValidateEndpoint(target.Host, target.Port);
    }
    public override string ToString() => Name + "\n" + Username + "@" + Hosts.Replace('\n', ' ');
}
