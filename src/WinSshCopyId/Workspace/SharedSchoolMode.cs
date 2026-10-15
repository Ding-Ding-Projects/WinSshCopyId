using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinSshCopyId.Workspace;

internal sealed record SharedSchoolModeRecord(int SchemaVersion, string RecordId, long Revision, bool Enabled,
    string DisplayName, string? UnlockKind, string Salt, string Verifier);
public sealed record SharedSchoolModeState(int SchemaVersion,string RecordId,long Revision,bool Enabled,string DisplayName,string? UnlockKind);
public sealed record SharedSchoolModeSnapshot(bool Known, bool Watching, SharedSchoolModeState? Record, string? UnavailableReason)
{
    public bool Restricted => !Known || Record?.Enabled == true;
    public string DisplayName => Record?.DisplayName ?? "School mode";
    public WorkspaceSettings Project(WorkspaceSettings basis) => !Restricted ? basis : basis with
    { Language = WorkspaceLanguage.English, EnglishFunnyLevel = 1, CantoneseFunnyLevel = 1, Narrator = basis.Narrator with { Language = WorkspaceLanguage.English } };
    public bool PersonalVocabularyAvailable => !Restricted;
    public bool PlayfulCapabilitiesAvailable => !Restricted;
}

/// <summary>Reads and writes the shared version-2 protocol used by the application catalog.</summary>
public sealed class SharedSchoolMode : IAsyncDisposable
{
    public static string DefaultRecordPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Ding-Ding-Projects", "global", "school-mode.v1.json");
    private readonly string path;
    private readonly SemaphoreSlim serial = new(1);
    private readonly CancellationTokenSource lifetime = new();
    private FileSystemWatcher? watcher;
    private Task? poll;
    private long generation;
    private bool disposed;
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private static SharedSchoolModeState? PublicState(SharedSchoolModeRecord? record)=>record is null?null:new(record.SchemaVersion,record.RecordId,record.Revision,record.Enabled,record.DisplayName,record.UnlockKind);
    public SharedSchoolModeSnapshot Snapshot { get; private set; } = new(false, false, null, "The shared state has not been read yet.");
    public event EventHandler<SharedSchoolModeSnapshot>? Changed;
    public SharedSchoolMode(string? sharedRecordPath = null) => path = Path.GetFullPath(sharedRecordPath ?? DefaultRecordPath);
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (poll is not null) return;
        ArmWatcher(); await ReloadAsync(cancellationToken);
        poll = PollAsync();
    }
    private void Publish(SharedSchoolModeSnapshot snapshot)
    { Snapshot = snapshot; Changed?.Invoke(this, snapshot); }
    private void Invalidate(string reason)
    {
        Interlocked.Increment(ref generation);
        Publish(new(false, watcher is not null, Snapshot.Record, reason));
    }
    private void ArmWatcher()
    {
        var directory = Path.GetDirectoryName(path)!;
        while (!Directory.Exists(directory)) directory = Path.GetDirectoryName(directory) ?? throw new IOException("No shared state ancestor is available.");
        if (watcher?.Path == directory) return;
        watcher?.Dispose(); watcher = null;
        try
        {
            watcher = new FileSystemWatcher(directory) { IncludeSubdirectories = false, NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.DirectoryName | NotifyFilters.Size };
            watcher.Changed += OnFilesystemChange; watcher.Created += OnFilesystemChange; watcher.Deleted += OnFilesystemChange; watcher.Renamed += OnFilesystemChange;
            watcher.Error += (_, _) => { if (!disposed) { watcher?.Dispose(); watcher = null; Invalidate("The shared state watcher is recovering."); } };
            watcher.EnableRaisingEvents = true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        { watcher?.Dispose(); watcher = null; Invalidate("The shared state cannot be watched."); }
    }
    private void OnFilesystemChange(object sender, FileSystemEventArgs args)
    {
        if (disposed) return;
        var watched = (FileSystemWatcher)sender;
        if (watched.Path == Path.GetDirectoryName(path) && args.FullPath != path && (args is not RenamedEventArgs renamed || renamed.OldFullPath != path)) return;
        Invalidate("The shared state is being refreshed.");
        _ = ReloadAfterEventAsync();
    }
    private async Task ReloadAfterEventAsync()
    {
        try { await ReloadAsync(lifetime.Token); }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Invalidate("The shared state cannot be refreshed."); }
    }
    private async Task PollAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        try { while (await timer.WaitForNextTickAsync(lifetime.Token)) { ArmWatcher(); await ReloadAsync(lifetime.Token); } }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Invalidate("The shared state watcher is unavailable."); }
    }
    public async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        await serial.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var ticket = Interlocked.Read(ref generation); ArmWatcher();
            var record = await Read(cancellationToken);
            if (ticket == Interlocked.Read(ref generation)) Publish(new(watcher is not null, watcher is not null, PublicState(record), watcher is null ? "The shared state cannot be watched." : null));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or FormatException)
        { Publish(new(false, watcher is not null, Snapshot.Record, "The shared state is invalid or unavailable; restricted presentation remains active.")); }
        finally { serial.Release(); }
    }
    private async Task<SharedSchoolModeRecord?> Read(CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 32768) throw new InvalidDataException();
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        using (var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 4 }))
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in json.RootElement.EnumerateObject()) if (!names.Add(property.Name)) throw new InvalidDataException();
            if (names.Count != 8) throw new InvalidDataException();
        }
        var result = JsonSerializer.Deserialize<SharedSchoolModeRecord>(bytes, Options) ?? throw new InvalidDataException();
        if (result.SchemaVersion != 2 || result.Revision is < 1 or > 9007199254740991 ||
            result.RecordId.Length is < 22 or > 64 || result.RecordId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_') ||
            string.IsNullOrWhiteSpace(result.DisplayName) || result.DisplayName.Length > 64 || result.DisplayName.Any(char.IsControl) ||
            result.UnlockKind is not (null or "pin" or "password" or "passkey") || result.Enabled && result.UnlockKind is null ||
            Convert.FromBase64String(result.Salt).Length is < 12 or > 96 || Convert.FromBase64String(result.Verifier).Length != 32) throw new InvalidDataException();
        return result;
    }
    public async Task ConfigureAsync(string displayName, string unlockKind, string credential, CancellationToken cancellationToken = default)
    {
        ValidateName(displayName); ValidateCredential(unlockKind, credential);
        await Mutate(null, 0, old =>
        {
            if (old is not null) throw new InvalidOperationException("The shared state is already configured.");
            var salt = RandomNumberGenerator.GetBytes(16);
            var hash = SharedScrypt.Derive(credential, salt);
            try { return new(2, Convert.ToBase64String(RandomNumberGenerator.GetBytes(18)).TrimEnd('=').Replace('+','-').Replace('/','_'), 1, false, displayName.Trim(), unlockKind, Convert.ToBase64String(salt), Convert.ToBase64String(hash)); }
            finally { CryptographicOperations.ZeroMemory(hash); }
        }, cancellationToken);
    }
    public async Task SetEnabledAsync(bool enabled, string? credential, CancellationToken cancellationToken = default)
    {
        var expected = Snapshot.Record ?? throw new InvalidOperationException("Configure the shared state first.");
        await Mutate(expected.RecordId, expected.Revision, old =>
        {
            if (old is null) throw new InvalidOperationException("The shared state changed.");
            if (!enabled && !Verify(old, credential)) throw new UnauthorizedAccessException("The shared credential did not match.");
            return old with { Revision = checked(old.Revision + 1), Enabled = enabled };
        }, cancellationToken);
    }
    public async Task RenameAsync(string displayName, CancellationToken cancellationToken = default)
    {
        ValidateName(displayName);
        var expected = Snapshot.Record ?? throw new InvalidOperationException("Configure the shared state first.");
        await Mutate(expected.RecordId, expected.Revision, old => (old ?? throw new InvalidOperationException()) with { Revision = checked(old!.Revision + 1), DisplayName = displayName.Trim() }, cancellationToken);
    }
    public async Task ChangeCredentialAsync(string current, string next, string unlockKind, CancellationToken cancellationToken = default)
    {
        ValidateCredential(unlockKind, next);
        var expected = Snapshot.Record ?? throw new InvalidOperationException("Configure the shared state first.");
        await Mutate(expected.RecordId, expected.Revision, old =>
        {
            if (old is null || !Verify(old, current)) throw new UnauthorizedAccessException("The shared credential did not match.");
            var salt = RandomNumberGenerator.GetBytes(16); var hash = SharedScrypt.Derive(next, salt);
            try { return old with { Revision = checked(old.Revision + 1), UnlockKind = unlockKind, Salt = Convert.ToBase64String(salt), Verifier = Convert.ToBase64String(hash) }; }
            finally { CryptographicOperations.ZeroMemory(hash); }
        }, cancellationToken);
    }
    private static void ValidateName(string name)
    { if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 64 || name.Any(char.IsControl)) throw new ArgumentException("Invalid shared mode name."); }
    private static void ValidateCredential(string kind, string credential)
    { if (kind is not ("pin" or "password") || credential.Length is < 4 or > 512 || kind == "pin" && (credential.Length > 64 || credential.Any(c => !char.IsAsciiDigit(c)))) throw new ArgumentException("Unsupported shared credential."); }
    private static bool Verify(SharedSchoolModeRecord record, string? credential)
    {
        if (credential is null || credential.Length is < 1 or > 512 || record.UnlockKind == "passkey") return false;
        var hash = SharedScrypt.Derive(credential, Convert.FromBase64String(record.Salt));
        try { return CryptographicOperations.FixedTimeEquals(hash, Convert.FromBase64String(record.Verifier)); }
        finally { CryptographicOperations.ZeroMemory(hash); }
    }
    private async Task Mutate(string? expectedId, long expectedRevision, Func<SharedSchoolModeRecord?, SharedSchoolModeRecord> mutation, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await serial.WaitAsync(cancellationToken);
        var lockPath = path + ".lock"; FileStream? held = null;
        var owner = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18)).TrimEnd('=').Replace('+','-').Replace('/','_');
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); deadline.CancelAfter(TimeSpan.FromSeconds(2));
            while (held is null)
            {
                deadline.Token.ThrowIfCancellationRequested();
                try { held = new FileStream(lockPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read); }
                catch (IOException) { await Task.Delay(25, deadline.Token); }
            }
            await held.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(new { token = owner, pid = Environment.ProcessId, createdAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }), cancellationToken);
            await held.FlushAsync(cancellationToken); held.Flush(true);
            var current = await Read(cancellationToken);
            if (current?.RecordId != expectedId || (current?.Revision ?? 0) != expectedRevision) throw new InvalidOperationException("The shared state changed in another application; reload before editing.");
            var next = mutation(current);
            await new DirectoryPrivateStorage(Path.GetDirectoryName(path)!).WriteAtomicAsync(Path.GetFileName(path), JsonSerializer.SerializeToUtf8Bytes(next, Options), cancellationToken);
            ArmWatcher(); Interlocked.Increment(ref generation);
            Publish(new(watcher is not null, watcher is not null, PublicState(next), watcher is null ? "The shared state cannot be watched." : null));
        }
        finally
        {
            if (held is not null) { await held.DisposeAsync(); File.Delete(lockPath); }
            serial.Release();
        }
    }
    public async ValueTask DisposeAsync()
    {
        if (disposed) return; disposed = true; lifetime.Cancel(); watcher?.Dispose(); watcher = null;
        if (poll is not null) await poll;
        lifetime.Dispose();
    }
}

/// <summary>RFC 7914 scrypt parameters matching the existing shared state protocol.</summary>
internal static class SharedScrypt
{
    public static byte[] Derive(string secret, byte[] salt)
    {
        const int n = 16384, r = 8;
        var b = Rfc2898DeriveBytes.Pbkdf2(secret, salt, 1, HashAlgorithmName.SHA256, 128 * r);
        var x = new uint[32 * r]; var v = new uint[n * x.Length]; var work = new uint[x.Length]; var block = new uint[16];
        try
        {
            for (var i = 0; i < x.Length; i++) x[i] = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(i * 4, 4));
            for (var i = 0; i < n; i++) { Array.Copy(x, 0, v, i * x.Length, x.Length); Mix(x, work, block, r); }
            for (var i = 0; i < n; i++)
            {
                var j = (int)(x[(2 * r - 1) * 16] & (n - 1));
                for (var k = 0; k < x.Length; k++) x[k] ^= v[j * x.Length + k];
                Mix(x, work, block, r);
            }
            for (var i = 0; i < x.Length; i++) BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(i * 4, 4), x[i]);
            return Rfc2898DeriveBytes.Pbkdf2(System.Text.Encoding.UTF8.GetBytes(secret), b, 1, HashAlgorithmName.SHA256, 32);
        }
        finally { CryptographicOperations.ZeroMemory(b); Array.Clear(x); Array.Clear(v); Array.Clear(work); Array.Clear(block); }
    }
    private static uint Rotate(uint x, int n) => (x << n) | (x >> (32 - n));
    private static void Mix(uint[] b, uint[] y, uint[] x, int r)
    {
        Array.Copy(b, (2 * r - 1) * 16, x, 0, 16);
        for (var i = 0; i < 2 * r; i++)
        {
            for (var k = 0; k < 16; k++) x[k] ^= b[i * 16 + k];
            Salsa(x); Array.Copy(x, 0, y, i * 16, 16);
        }
        for (var i = 0; i < r; i++) { Array.Copy(y, i * 32, b, i * 16, 16); Array.Copy(y, i * 32 + 16, b, (i + r) * 16, 16); }
    }
    private static void Salsa(uint[] b)
    {
        Span<uint> x = stackalloc uint[16]; b.CopyTo(x);
        unchecked
        {
            for (var i = 0; i < 8; i += 2)
            {
                x[4]^=Rotate(x[0]+x[12],7);x[8]^=Rotate(x[4]+x[0],9);x[12]^=Rotate(x[8]+x[4],13);x[0]^=Rotate(x[12]+x[8],18);
                x[9]^=Rotate(x[5]+x[1],7);x[13]^=Rotate(x[9]+x[5],9);x[1]^=Rotate(x[13]+x[9],13);x[5]^=Rotate(x[1]+x[13],18);
                x[14]^=Rotate(x[10]+x[6],7);x[2]^=Rotate(x[14]+x[10],9);x[6]^=Rotate(x[2]+x[14],13);x[10]^=Rotate(x[6]+x[2],18);
                x[3]^=Rotate(x[15]+x[11],7);x[7]^=Rotate(x[3]+x[15],9);x[11]^=Rotate(x[7]+x[3],13);x[15]^=Rotate(x[11]+x[7],18);
                x[1]^=Rotate(x[0]+x[3],7);x[2]^=Rotate(x[1]+x[0],9);x[3]^=Rotate(x[2]+x[1],13);x[0]^=Rotate(x[3]+x[2],18);
                x[6]^=Rotate(x[5]+x[4],7);x[7]^=Rotate(x[6]+x[5],9);x[4]^=Rotate(x[7]+x[6],13);x[5]^=Rotate(x[4]+x[7],18);
                x[11]^=Rotate(x[10]+x[9],7);x[8]^=Rotate(x[11]+x[10],9);x[9]^=Rotate(x[8]+x[11],13);x[10]^=Rotate(x[9]+x[8],18);
                x[12]^=Rotate(x[15]+x[14],7);x[13]^=Rotate(x[12]+x[15],9);x[14]^=Rotate(x[13]+x[12],13);x[15]^=Rotate(x[14]+x[13],18);
            }
            for (var i = 0; i < 16; i++) b[i] += x[i];
        }
    }
}
