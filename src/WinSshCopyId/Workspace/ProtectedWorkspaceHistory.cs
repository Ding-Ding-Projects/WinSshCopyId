using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WinSshCopyId.Workspace;

public sealed record ProtectedHistoryRevision(int Version, Guid Id, Guid RecordId, DateTimeOffset At,
    string Action, string RecordKind, Guid? RestoredFrom, string Nonce, string Ciphertext, string Tag);
public sealed record HistoryDifference(bool Changed, string BeforeJson, string AfterJson);
public sealed record HistoryRetention(int KeepRecent = 100, int KeepDays = 90);
public sealed record HistoryArchiveResult(int Archived, string? Warning = null);

public sealed class ProtectedHistoryAccess : IDisposable
{
    private byte[]? key;
    internal readonly Guid Owner;
    internal readonly DateTimeOffset Expires;
    internal bool IsDisposed => key is null;
    internal ProtectedHistoryAccess(Guid owner, byte[] key, DateTimeOffset expires) { Owner = owner; this.key = key; Expires = expires; }
    internal byte[] GetKey(Guid owner, DateTimeOffset now)
    { if (owner != Owner || now >= Expires || key is null) throw new UnauthorizedAccessException("Open history with its own credential first."); return key; }
    public void Dispose() { var value = Interlocked.Exchange(ref key, null); if (value is not null) CryptographicOperations.ZeroMemory(value); }
}

/// <summary>Encrypted application-owned snapshots. The injected credential delegates must use the platform vault.</summary>
public sealed class ProtectedWorkspaceHistory : IDisposable
{
    private readonly string directory;
    private readonly string? executable;
    private readonly IProcessExecutor executor;
    private readonly IClock clock;
    private readonly Func<string, string?> get;
    private readonly Action<string, string> set;
    private readonly string vaultKey;
    private readonly Guid owner = Guid.NewGuid();
    private readonly SemaphoreSlim serial = new(1);
    private bool disposed;
    private readonly List<ProtectedHistoryAccess> accesses = [];
    private static readonly HashSet<string> Actions = ["created", "updated", "deleted", "restored", "undone", "imported", "settings-changed", "discarded", "renamed", "reset", "archived", "labeled"];
    private static readonly HashSet<string> Kinds = ["profile", "settings", "display-name", "schedule", "tab", "document", "credential"];
    public ProtectedWorkspaceHistory(string isolatedApplicationHistoryDirectory, string? gitExecutable,
        IProcessExecutor executor, IClock clock, Func<string, string?> getCredential, Action<string, string> setCredential,
        string credentialReference = "history/protection-v1")
    {
        directory = Path.GetFullPath(isolatedApplicationHistoryDirectory); executable = gitExecutable; this.executor = executor;
        this.clock = clock; get = getCredential; set = setCredential; vaultKey = credentialReference;
    }
    private sealed record VaultMaterial(int Version, string Key, string Salt, string Verifier);
    public void ConfigureCredential(string credential)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (credential.Length is < 4 or > 512) throw new ArgumentException("Choose a credential within the supported input bounds.");
        if (get(vaultKey) is not null) throw new InvalidOperationException("History already has its own credential.");
        var key = RandomNumberGenerator.GetBytes(32); var salt = RandomNumberGenerator.GetBytes(16);
        var verifier = Rfc2898DeriveBytes.Pbkdf2(credential, salt, 210000, HashAlgorithmName.SHA256, 32);
        try { set(vaultKey, JsonSerializer.Serialize(new VaultMaterial(1, Convert.ToBase64String(key), Convert.ToBase64String(salt), Convert.ToBase64String(verifier)))); }
        finally { CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(verifier); }
    }
    public ProtectedHistoryAccess Open(string credential, TimeSpan? duration = null)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (credential.Length is < 1 or > 512) throw new UnauthorizedAccessException("History credential did not match.");
        var material = JsonSerializer.Deserialize<VaultMaterial>(get(vaultKey) ?? throw new InvalidOperationException("History credential storage is unavailable.")) ?? throw new InvalidDataException();
        if (material.Version != 1) throw new InvalidDataException("Unsupported history key format.");
        var verifier = Rfc2898DeriveBytes.Pbkdf2(credential, Convert.FromBase64String(material.Salt), 210000, HashAlgorithmName.SHA256, 32);
        try { if (!CryptographicOperations.FixedTimeEquals(verifier, Convert.FromBase64String(material.Verifier))) throw new UnauthorizedAccessException("History credential did not match."); }
        finally { CryptographicOperations.ZeroMemory(verifier); }
        var lifetime = duration ?? TimeSpan.FromMinutes(5);
        if (lifetime <= TimeSpan.Zero || lifetime > TimeSpan.FromMinutes(30)) throw new ArgumentOutOfRangeException(nameof(duration));
        var key = Convert.FromBase64String(material.Key);
        if (key.Length != 32) { CryptographicOperations.ZeroMemory(key); throw new InvalidDataException("History key is unavailable."); }
        lock (accesses)
        {
            accesses.RemoveAll(a => a.IsDisposed);
            if (accesses.Count >= 32) { CryptographicOperations.ZeroMemory(key); throw new InvalidOperationException("Close an existing history session first."); }
            var access = new ProtectedHistoryAccess(owner, key, clock.UtcNow + lifetime); accesses.Add(access); return access;
        }
    }
    private byte[] Access(ProtectedHistoryAccess access)
    { ObjectDisposedException.ThrowIf(disposed, this); return access.GetKey(owner, clock.UtcNow); }
    public async Task<HistoryWriteResult> RecordAsync(ProtectedHistoryAccess access, Guid recordId, string action, string kind,
        ReadOnlyMemory<byte> snapshotJson, Guid? restoredFrom = null, CancellationToken cancellationToken = default)
    {
        var key = Access(access);
        if (recordId == Guid.Empty || !Actions.Contains(action) || !Kinds.Contains(kind)) throw new ArgumentException("Unsupported history metadata.");
        if (snapshotJson.Length > 700000) throw new InvalidDataException("Snapshot exceeds its size limit.");
        using (JsonDocument.Parse(snapshotJson, new JsonDocumentOptions { MaxDepth = 32 })) { }
        if (executable is null || !File.Exists(executable)) return new(false, "A genuine Git executable is unavailable.");
        await serial.WaitAsync(cancellationToken);
        try
        {
            _ = Access(access);
            Directory.CreateDirectory(directory);
            if (!Directory.Exists(Path.Combine(directory, ".git")) && await Run(["init", "--quiet"], cancellationToken) != 0) return new(false, "History initialization failed.");
            var nonce = RandomNumberGenerator.GetBytes(12); var cipher = new byte[snapshotJson.Length]; var tag = new byte[16];
            using (var aes = new AesGcm(key, 16)) aes.Encrypt(nonce, snapshotJson.Span, cipher, tag, Encoding.UTF8.GetBytes(recordId.ToString("D")));
            var revision = new ProtectedHistoryRevision(1, Guid.NewGuid(), recordId, clock.UtcNow, action, kind, restoredFrom,
                Convert.ToBase64String(nonce), Convert.ToBase64String(cipher), Convert.ToBase64String(tag));
            var relative = revision.Id.ToString("N") + ".revision.json";
            await new DirectoryPrivateStorage(directory).WriteAtomicAsync(relative, JsonSerializer.SerializeToUtf8Bytes(revision), cancellationToken);
            if (await Run(["add", "--", relative], cancellationToken) != 0) return new(false, "History staging failed; encrypted snapshot remains available for recovery.");
            if (await Run(["-c", "user.name=WinSshCopyId Local History", "-c", "user.email=local-history@localhost", "-c", "commit.gpgsign=false", "commit", "--quiet", "-m", kind + ": " + action, "--", relative], cancellationToken) != 0)
                return new(false, "History commit failed; encrypted snapshot remains available for recovery.");
            return new(true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        { return new(false, "History is unavailable; no successful revision is claimed."); }
        finally { serial.Release(); }
    }
    private Task<int> Run(IReadOnlyList<string> args, CancellationToken cancellationToken) => executor.RunAsync(executable!, directory, args, cancellationToken);
    public async Task<IReadOnlyList<HistoryRecord>> BrowseAsync(ProtectedHistoryAccess access, DateTimeOffset? from = null,
        DateTimeOffset? until = null, IReadOnlySet<string>? actions = null, CancellationToken cancellationToken = default)
    {
        _ = Access(access); var revisions = await ReadRevisions(cancellationToken);
        return revisions.Where(r => (from is null || r.At >= from) && (until is null || r.At <= until) && (actions is null || actions.Contains(r.Action)))
            .OrderByDescending(r => r.At).Select(r => new HistoryRecord(r.Id, r.At, r.RecordId, r.Action, r.RecordKind, r.RestoredFrom)).ToArray();
    }
    private async Task<IReadOnlyList<ProtectedHistoryRevision>> ReadRevisions(CancellationToken cancellationToken)
    {
        if (executable is null || !Directory.Exists(directory)) return [];
        var revisions = new List<ProtectedHistoryRevision>();
        foreach (var file in Directory.EnumerateFiles(directory, "*.revision.json").Take(10000))
        {
            if (new FileInfo(file).Length > 1500000) continue;
            if (await Run(["cat-file", "-e", "HEAD:" + Path.GetFileName(file)], cancellationToken) != 0 ||
                await Run(["diff", "--quiet", "HEAD", "--", Path.GetFileName(file)], cancellationToken) != 0) continue;
            var revision = JsonSerializer.Deserialize<ProtectedHistoryRevision>(await File.ReadAllBytesAsync(file, cancellationToken));
            if (revision is not null && revision.Version == 1 && Actions.Contains(revision.Action) && Kinds.Contains(revision.RecordKind)) revisions.Add(revision);
        }
        return revisions;
    }
    public async Task<byte[]> ReadSnapshotAsync(ProtectedHistoryAccess access, Guid revisionId, CancellationToken cancellationToken = default)
    {
        var key = Access(access);
        var revision = (await ReadRevisions(cancellationToken)).Single(r => r.Id == revisionId);
        var ciphertext = Convert.FromBase64String(revision.Ciphertext); var plaintext = new byte[ciphertext.Length];
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(Convert.FromBase64String(revision.Nonce), ciphertext, Convert.FromBase64String(revision.Tag), plaintext, Encoding.UTF8.GetBytes(revision.RecordId.ToString("D")));
            return plaintext;
        }
        catch { CryptographicOperations.ZeroMemory(plaintext); throw; }
    }
    public async Task<HistoryDifference> DiffAsync(ProtectedHistoryAccess access, Guid before, Guid after, CancellationToken cancellationToken = default)
    {
        var left = await ReadSnapshotAsync(access, before, cancellationToken); var right = await ReadSnapshotAsync(access, after, cancellationToken);
        try { return new(!left.AsSpan().SequenceEqual(right), Encoding.UTF8.GetString(left), Encoding.UTF8.GetString(right)); }
        finally { CryptographicOperations.ZeroMemory(left); CryptographicOperations.ZeroMemory(right); }
    }
    public async Task<HistoryWriteResult> RestoreAsync(ProtectedHistoryAccess access, Guid revisionId,
        Func<ReadOnlyMemory<byte>, CancellationToken, Task> applySnapshot, CancellationToken cancellationToken = default)
    {
        var revision = (await ReadRevisions(cancellationToken)).Single(r => r.Id == revisionId);
        var plaintext = await ReadSnapshotAsync(access, revisionId, cancellationToken);
        try
        {
            await applySnapshot(plaintext, cancellationToken);
            return await RecordAsync(access, revision.RecordId, "restored", revision.RecordKind, plaintext, revision.Id, cancellationToken);
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }
    public async Task<byte[]> ExportRedactedAsync(ProtectedHistoryAccess access, CancellationToken cancellationToken = default) =>
        JsonSerializer.SerializeToUtf8Bytes(new { Version = 1, Omitted = "Snapshot content, credentials, replacement data and file metadata are omitted.", Records = await BrowseAsync(access, cancellationToken: cancellationToken) });
    public async Task<HistoryArchiveResult> ArchiveAsync(ProtectedHistoryAccess access, HistoryRetention retention, CancellationToken cancellationToken = default)
    {
        _ = Access(access);
        if (retention.KeepRecent is < 1 or > 10000 || retention.KeepDays is < 1 or > 36500) throw new ArgumentOutOfRangeException(nameof(retention));
        var revisions = (await ReadRevisions(cancellationToken)).OrderByDescending(r => r.At).ToArray();
        var selected = revisions.Skip(retention.KeepRecent).Where(r => r.At < clock.UtcNow.AddDays(-retention.KeepDays)).ToArray();
        if (selected.Length == 0) return new(0);
        // Archival records add an index and retain every encrypted original and commit. No history rewriting occurs.
        var payload = JsonSerializer.SerializeToUtf8Bytes(new { Version = 1, Archived = selected.Select(r => r.Id), Retention = retention });
        var result = await RecordAsync(access, Guid.NewGuid(), "archived", "document", payload, cancellationToken: cancellationToken);
        return result.Recorded ? new(selected.Length) : new(0, result.UnavailableReason);
    }
    public async Task<HistoryWriteResult> LabelAsync(ProtectedHistoryAccess access, Guid revisionId, string label, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(label) || label.Length > 120 || label.Any(char.IsControl)) throw new ArgumentException("Invalid revision label.");
        var revision = (await ReadRevisions(cancellationToken)).Single(r => r.Id == revisionId);
        return await RecordAsync(access, revision.RecordId, "labeled", revision.RecordKind, JsonSerializer.SerializeToUtf8Bytes(new { Revision = revisionId, Label = label }), revisionId, cancellationToken);
    }
    public void Dispose() { disposed = true; lock (accesses) { foreach (var access in accesses) access.Dispose(); accesses.Clear(); } }
}
