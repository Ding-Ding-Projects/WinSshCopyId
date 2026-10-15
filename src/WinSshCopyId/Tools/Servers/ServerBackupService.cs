using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;

namespace WinSshCopyId.Tools.Servers;

public sealed record BackupFile(string RelativePath, long Bytes, string Sha256);
public sealed record ServerBackupReceipt(int Version, Guid InstanceId, DateTimeOffset CreatedAt,
    ImmutableArray<BackupFile> Files, long TotalBytes);

/// <summary>Creates directory snapshots of stopped, adopted local instances without following links.</summary>
public sealed class ServerBackupService
{
    public long MaximumBytes { get; init; } = 64L * 1024 * 1024 * 1024;
    public int MaximumFiles { get; init; } = 1_000_000;
    private static void ValidateStopped(ServerInstance instance, ServerObservation observation)
    {
        if (!instance.Adopted || observation.InstanceId != instance.Id || observation.State != ServerObservedState.Stopped ||
            DateTimeOffset.UtcNow - observation.ObservedAt > TimeSpan.FromSeconds(30))
            throw new InvalidOperationException("A fresh stopped observation for an adopted instance is required.");
        if (instance.Target.Kind != ServerTargetKind.LocalProcess) throw new NotSupportedException("File snapshots require an adopted local process target.");
    }
    private static string Root(string path) => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
    private static bool IsWithin(string parent, string child) => Root(child).StartsWith(Root(parent), StringComparison.OrdinalIgnoreCase);
    private static void RejectLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Links and reparse points are not included in backups.");
    }
    public async Task<ServerBackupReceipt> CreateAsync(ServerInstance instance, ServerObservation observation,
        string destination, IProgress<ServerProgress>? progress = null, CancellationToken token = default)
    {
        ValidateStopped(instance, observation);
        var source = Path.GetFullPath(instance.Target.WorkingDirectory);
        var target = Path.GetFullPath(destination);
        if (IsWithin(source, target) || IsWithin(target, source)) throw new ArgumentException("Backup must be outside the instance directory.");
        if (Directory.Exists(target) || File.Exists(target)) throw new IOException("Backup destination already exists.");
        RejectLink(source);
        var staging = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        Directory.CreateDirectory(staging);
        try
        {
            var files = ImmutableArray.CreateBuilder<BackupFile>();
            long total = 0;
            var entryCount = 0;
            var pending = new Stack<string>(); pending.Push(source);
            while (pending.TryPop(out var directory))
            {
                token.ThrowIfCancellationRequested();
                RejectLink(directory);
                foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
                {
                    token.ThrowIfCancellationRequested(); RejectLink(entry);
                    if (++entryCount > MaximumFiles) throw new InvalidDataException("Backup entry limit exceeded.");
                    if (Directory.Exists(entry)) { pending.Push(entry); continue; }
                    if (files.Count >= MaximumFiles) throw new InvalidDataException("Backup file limit exceeded.");
                    var info = new FileInfo(entry);
                    if (info.Length > MaximumBytes - total) throw new InvalidDataException("Backup byte limit exceeded.");
                    var relative = Path.GetRelativePath(source, entry);
                    if (relative.Equals(".backup-receipt.json", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Reserved backup receipt name.");
                    var output = Path.Combine(staging, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                    await using (var input = new FileStream(entry, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true))
                    await using (var result = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                    {
                        var buffer = new byte[81920];
                        long copiedBytes = 0;
                        int read;
                        while ((read = await input.ReadAsync(buffer, token)) > 0)
                        {
                            copiedBytes += read;
                            if (copiedBytes > info.Length || copiedBytes > MaximumBytes - total) throw new InvalidDataException("Source changed or exceeded backup bounds.");
                            await result.WriteAsync(buffer.AsMemory(0, read), token);
                        }
                        if (copiedBytes != info.Length) throw new InvalidDataException("Source changed during backup.");
                    }
                    await using var copied = File.OpenRead(output);
                    var digest = Convert.ToHexString(await SHA256.HashDataAsync(copied, token));
                    total += info.Length; files.Add(new(relative, info.Length, digest));
                    progress?.Report(new(instance.Id, "Backing up", total, null));
                }
            }
            var receipt = new ServerBackupReceipt(1, instance.Id, DateTimeOffset.UtcNow, files.ToImmutable(), total);
            await File.WriteAllTextAsync(Path.Combine(staging, ".backup-receipt.json"), JsonSerializer.Serialize(receipt), token);
            token.ThrowIfCancellationRequested();
            Directory.Move(staging, target);
            return receipt;
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
    }
    public async Task<ServerBackupReceipt> VerifyAsync(string backup, CancellationToken token = default)
    {
        var root = Path.GetFullPath(backup); RejectLink(root);
        var manifest = Path.Combine(root, ".backup-receipt.json");
        if (new FileInfo(manifest).Length > 128 * 1024 * 1024) throw new InvalidDataException("Backup receipt too large.");
        var receipt = JsonSerializer.Deserialize<ServerBackupReceipt>(await File.ReadAllTextAsync(manifest, token))
            ?? throw new InvalidDataException("Invalid receipt.");
        if (receipt.Version != 1 || receipt.Files.IsDefault || receipt.Files.Length > MaximumFiles || receipt.TotalBytes > MaximumBytes)
            throw new InvalidDataException("Unsupported or excessive backup receipt.");
        long total = 0;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in receipt.Files)
        {
            token.ThrowIfCancellationRequested();
            if (!names.Add(entry.RelativePath) || Path.IsPathFullyQualified(entry.RelativePath) || entry.Bytes < 0)
                throw new InvalidDataException("Invalid backup member.");
            var path = Path.GetFullPath(Path.Combine(root, entry.RelativePath));
            if (!path.StartsWith(Root(root), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Backup member escapes root.");
            for (var parent = Path.GetDirectoryName(path); parent is not null && parent.Length >= root.Length; parent = Path.GetDirectoryName(parent)) RejectLink(parent);
            RejectLink(path);
            await using var file = File.OpenRead(path);
            if (file.Length != entry.Bytes || !Convert.ToHexString(await SHA256.HashDataAsync(file, token)).Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Backup member digest mismatch.");
            total = checked(total + entry.Bytes);
            if (total > MaximumBytes) throw new InvalidDataException("Backup byte limit exceeded.");
        }
        if (total != receipt.TotalBytes) throw new InvalidDataException("Backup total mismatch.");
        return receipt;
    }
    public async Task<string> RestoreAsync(ServerInstance instance, ServerObservation observation, string backup,
        CancellationToken token = default)
    {
        ValidateStopped(instance, observation);
        var receipt = await VerifyAsync(backup, token);
        if (receipt.InstanceId != instance.Id) throw new InvalidDataException("Backup belongs to another instance.");
        var target = Path.GetFullPath(instance.Target.WorkingDirectory);
        var stage = target + ".restore-" + Guid.NewGuid().ToString("N");
        var rollback = target + ".rollback-" + Guid.NewGuid().ToString("N");
        if (IsWithin(target, backup) || IsWithin(backup, target)) throw new InvalidDataException("Backup must be outside the instance.");
        Directory.CreateDirectory(stage);
        try
        {
            foreach (var entry in receipt.Files)
            {
                token.ThrowIfCancellationRequested();
                var output = Path.Combine(stage, entry.RelativePath); Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                await using var input = File.OpenRead(Path.Combine(backup, entry.RelativePath));
                await using var result = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
                await input.CopyToAsync(result, token);
            }
            // Verify the staged bytes independently before replacing the original directory.
            await File.WriteAllTextAsync(Path.Combine(stage, ".backup-receipt.json"), JsonSerializer.Serialize(receipt), token);
            await VerifyAsync(stage, token);
            File.Delete(Path.Combine(stage, ".backup-receipt.json"));
            token.ThrowIfCancellationRequested(); RejectLink(target);
            Directory.Move(target, rollback);
            try { Directory.Move(stage, target); }
            catch { Directory.Move(rollback, target); throw; }
            return rollback; // The caller retains this directory for explicit rollback; it is never silently deleted.
        }
        finally { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
    }
}
