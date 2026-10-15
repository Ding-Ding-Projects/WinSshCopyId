using System.Collections.Immutable;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WinSshCopyId.Tools.Ollama;

public sealed record OllamaPayloadMember(string RelativePath, long Length, string Sha256);
public sealed record OllamaPayloadManifest(string Version, string Architecture, Uri Source, long ArchiveBytes, string ArchiveSha256, ImmutableArray<OllamaPayloadMember> Members, string PublisherRequirement);
public sealed record OllamaInstallationReceipt(string Version, string Architecture, string Directory, string ArchiveSha256, DateTimeOffset InstalledAt, string PreviousVersion);
public sealed record OllamaInstallProgress(string Phase, long Bytes, long Total);

/// <summary>Transactional versioned ZIP installation. Caller supplies reviewed pinned official metadata and publisher verification.</summary>
public sealed class OllamaInstaller : IDisposable
{
    private readonly string root;
    private readonly HttpClient http;
    private readonly bool ownsHttp;
    private readonly SemaphoreSlim transaction = new(1, 1);
    public OllamaInstaller(string? ownedRoot = null, HttpClient? controlledTransport = null)
    {
        root = Path.GetFullPath(ownedRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinSshCopyId", "Tools", "Ollama"));
        http = controlledTransport ?? new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
        ownsHttp = controlledTransport is null;
    }
    public static void ValidateManifest(OllamaPayloadManifest manifest)
    {
        if (!Regex.IsMatch(manifest.Version, @"\A[0-9]+\.[0-9]+\.[0-9]+(?:-[a-zA-Z0-9.-]+)?\z", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)) || manifest.Architecture is not ("x64" or "arm64")) throw new InvalidDataException("Unsupported payload version or architecture.");
        if (manifest.Source.Scheme != "https" || manifest.Source.Host != "github.com" || !manifest.Source.IsDefaultPort || manifest.Source.Query.Length > 0 || manifest.Source.Fragment.Length > 0 || !manifest.Source.AbsolutePath.StartsWith("/ollama/ollama/releases/download/v" + manifest.Version + "/", StringComparison.Ordinal) || !manifest.Source.AbsolutePath.EndsWith(".zip", StringComparison.Ordinal)) throw new InvalidDataException("Payload must name a pinned official Ollama release ZIP.");
        if (manifest.ArchiveBytes is < 1 or > 2147483648L || !DigestValid(manifest.ArchiveSha256) || string.IsNullOrWhiteSpace(manifest.PublisherRequirement) || manifest.Members.IsDefaultOrEmpty || manifest.Members.Length > 10000) throw new InvalidDataException("Payload manifest is incomplete.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase); long expanded = 0;
        foreach (var member in manifest.Members)
        {
            ValidateRelative(member.RelativePath);
            if (!names.Add(member.RelativePath) || member.Length < 0 || !DigestValid(member.Sha256)) throw new InvalidDataException("Payload inventory contains duplicate or invalid members.");
            expanded = checked(expanded + member.Length);
        }
        if (expanded > 16L * 1024 * 1024 * 1024 || !names.Contains("ollama.exe")) throw new InvalidDataException("Payload exceeds limits or has no runtime executable.");
    }
    public async Task<OllamaInstallationReceipt> InstallAsync(OllamaPayloadManifest manifest, Func<string, string, CancellationToken, Task<bool>> verifyPublisher,
        IProgress<OllamaInstallProgress>? progress = null, CancellationToken cancellationToken = default, string? cachedArchivePath = null)
    {
        ValidateManifest(manifest);
        ArgumentNullException.ThrowIfNull(verifyPublisher);
        await transaction.WaitAsync(cancellationToken);
        string? scratch = null; string? promoted = null; bool committed = false;
        try
        {
            RejectReparseAncestors(root);
            Directory.CreateDirectory(root);
            string marker = Path.Combine(root, ".winsshcopyid-owned");
            if (!File.Exists(marker))
            {
                if (Directory.EnumerateFileSystemEntries(root).Any()) throw new InvalidDataException("Refusing to adopt a nonempty unmanaged installation directory.");
                await File.WriteAllTextAsync(marker, "WinSshCopyId Ollama managed versions v1", cancellationToken);
            }
            RejectReparseAncestors(marker);
            if (await File.ReadAllTextAsync(marker, cancellationToken) != "WinSshCopyId Ollama managed versions v1") throw new InvalidDataException("Managed directory ownership marker is invalid.");
            using var lockFile = new FileStream(Path.Combine(root, ".install-lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            string activePath = Path.Combine(root, "active-installation.json");
            RejectReparseAncestors(activePath);
            if (File.Exists(activePath) && new FileInfo(activePath).Length > 4 * 1024 * 1024) throw new InvalidDataException("Active installation history exceeds the record bound.");
            string previous = File.Exists(activePath) ? await File.ReadAllTextAsync(activePath, cancellationToken) : "";
            long required = checked(manifest.ArchiveBytes + manifest.Members.Sum(m => m.Length) + 64L * 1024 * 1024);
            if (new DriveInfo(Path.GetPathRoot(root)!).AvailableFreeSpace < required) throw new IOException("Insufficient storage for download, extraction and rollback reserve.");
            scratch = Path.Combine(root, ".staging-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(scratch);
            string archivePath = Path.Combine(scratch, "payload.zip");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(TimeSpan.FromHours(1));
            if (cachedArchivePath is not null)
            {
                RejectReparseAncestors(cachedArchivePath);
                using var cached = new FileStream(cachedArchivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (cached.Length != manifest.ArchiveBytes || !Convert.ToHexString(await SHA256.HashDataAsync(cached, timeout.Token)).Equals(manifest.ArchiveSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Cached payload length or digest mismatch.");
                cached.Position = 0;
                using var output = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await cached.CopyToAsync(output, 65536, timeout.Token);
                progress?.Report(new("Verified cached payload", cached.Length, manifest.ArchiveBytes));
            }
            else
            {
            // Redirects are followed only through explicitly allowed official release-asset hosts.
            Uri uri = manifest.Source; HttpResponseMessage? response = null;
            for (int redirects = 0; redirects <= 3; redirects++)
            {
                response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                if ((int)response.StatusCode is >= 300 and < 400)
                {
                    Uri next = response.Headers.Location is { } location ? new Uri(uri, location) : throw new InvalidDataException("Release redirect has no location.");
                    response.Dispose(); response = null;
                    if (next.Scheme != "https" || !next.IsDefaultPort || next.Host is not ("release-assets.githubusercontent.com" or "objects.githubusercontent.com")) throw new InvalidDataException("Release redirect left the official asset allowlist.");
                    uri = next; continue;
                }
                break;
            }
            using (response)
            {
                if (response is null || !response.IsSuccessStatusCode) throw new HttpRequestException("Official payload acquisition failed.");
                if (response.Content.Headers.ContentLength is { } length && length != manifest.ArchiveBytes) throw new InvalidDataException("Archive length does not match admitted payload.");
                using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
                using var output = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                var buffer = new byte[65536]; long copied = 0; int count;
                while ((count = await input.ReadAsync(buffer, timeout.Token)) > 0)
                {
                    copied += count; if (copied > manifest.ArchiveBytes) throw new InvalidDataException("Download exceeded admitted byte count.");
                    await output.WriteAsync(buffer.AsMemory(0, count), timeout.Token); progress?.Report(new("Downloading", copied, manifest.ArchiveBytes));
                }
                if (copied != manifest.ArchiveBytes) throw new InvalidDataException("Payload download was incomplete.");
            }
            }
            if (!await HashMatchesAsync(archivePath, manifest.ArchiveSha256, timeout.Token)) throw new InvalidDataException("Payload digest mismatch.");
            string extracted = Path.Combine(scratch, "extracted"); Directory.CreateDirectory(extracted);
            using (var zip = ZipFile.OpenRead(archivePath))
            {
                var expected = manifest.Members.ToDictionary(m => m.RelativePath, StringComparer.OrdinalIgnoreCase);
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in zip.Entries)
                {
                    timeout.Token.ThrowIfCancellationRequested();
                    string name = entry.FullName.Replace('\\', '/');
                    if (name.EndsWith('/')) { ValidateRelative(name.TrimEnd('/')); continue; }
                    ValidateRelative(name);
                    if ((entry.ExternalAttributes & 0x400) != 0 || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000 || !seen.Add(name) || !expected.TryGetValue(name, out var member) || entry.Length != member.Length) throw new InvalidDataException("Archive member inventory mismatch or unsafe member.");
                    string destination = Path.GetFullPath(Path.Combine(extracted, name));
                    if (!destination.StartsWith(extracted + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Unsafe archive destination.");
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    using var input = entry.Open(); using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    var buffer = new byte[65536]; long copied = 0; int count;
                    while ((count = await input.ReadAsync(buffer, timeout.Token)) > 0)
                    { copied += count; if (copied > member.Length) throw new InvalidDataException("Expanded member exceeds inventory."); await output.WriteAsync(buffer.AsMemory(0, count), timeout.Token); }
                    if (copied != member.Length) throw new InvalidDataException("Expanded member is incomplete.");
                }
                if (seen.Count != expected.Count) throw new InvalidDataException("Archive omitted required members.");
            }
            foreach (var member in manifest.Members)
            {
                string path = Path.Combine(extracted, member.RelativePath);
                RejectReparseAncestors(path);
                if (!await HashMatchesAsync(path, member.Sha256, timeout.Token)) throw new InvalidDataException("Extracted member digest mismatch.");
            }
            if (!await verifyPublisher(Path.Combine(extracted, "ollama.exe"), manifest.PublisherRequirement, timeout.Token)) throw new InvalidDataException("Payload publisher verification failed.");
            promoted = Path.Combine(root, manifest.Version + "-" + manifest.Architecture + "-" + Guid.NewGuid().ToString("N"));
            Directory.Move(extracted, promoted);
            var receipt = new OllamaInstallationReceipt(manifest.Version, manifest.Architecture, promoted, manifest.ArchiveSha256, DateTimeOffset.UtcNow, previous);
            string nextRecord = Path.Combine(scratch, "next-record.json");
            await File.WriteAllTextAsync(nextRecord, JsonSerializer.Serialize(receipt), timeout.Token);
            File.Move(nextRecord, activePath, true); committed = true;
            progress?.Report(new("Installed; runtime not started", manifest.ArchiveBytes, manifest.ArchiveBytes));
            return receipt;
        }
        finally
        {
            // Only exact fresh transaction-owned directories are removed. Earlier versions and records survive failure.
            try
            {
                if (!committed && promoted is not null && Directory.Exists(promoted)) Directory.Delete(promoted, true);
                if (scratch is not null && Directory.Exists(scratch)) Directory.Delete(scratch, true);
            }
            finally { transaction.Release(); }
        }
    }
    public async Task RestoreRecordAsync(OllamaInstallationReceipt receipt, CancellationToken cancellationToken = default)
    {
        await transaction.WaitAsync(cancellationToken);
        try
        {
            RejectReparseAncestors(root);
            if (!receipt.Directory.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Receipt is outside the owned installation root.");
            string active = Path.Combine(root, "active-installation.json"); RejectReparseAncestors(active);
            var current = JsonSerializer.Deserialize<OllamaInstallationReceipt>(await File.ReadAllTextAsync(active, cancellationToken));
            if (current != receipt) throw new InvalidOperationException("Installation changed since this receipt was issued.");
            if (string.IsNullOrEmpty(receipt.PreviousVersion)) File.Delete(active);
            else
            {
                var previous = JsonSerializer.Deserialize<OllamaInstallationReceipt>(receipt.PreviousVersion) ?? throw new InvalidDataException("Prior record is invalid.");
                if (!previous.Directory.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(previous.Directory)) throw new InvalidDataException("Prior version is unavailable.");
                RejectReparseAncestors(previous.Directory);
                string temporary = Path.Combine(root, ".restore-" + Guid.NewGuid().ToString("N"));
                try { await File.WriteAllTextAsync(temporary, receipt.PreviousVersion, cancellationToken); File.Move(temporary, active, true); }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
        }
        finally { transaction.Release(); }
    }
    private static async Task<bool> HashMatchesAsync(string path, string expected, CancellationToken cancellationToken)
    { using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).Equals(expected, StringComparison.OrdinalIgnoreCase); }
    private static bool DigestValid(string value) => value is not null && Regex.IsMatch(value, @"\A[0-9a-fA-F]{64}\z", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static void ValidateRelative(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 500 || Path.IsPathRooted(path) || path.Contains('\\') || path.Contains(':') || path.Split('/').Any(p => p is "" or "." or ".." || p.EndsWith('.') || p.EndsWith(' ') || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)) throw new InvalidDataException("Unsafe payload member path.");
    }
    private static void RejectReparseAncestors(string path)
    { for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current)) if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Reparse points are not admitted in the managed installation path."); }
    public void Dispose() { if (ownsHttp) http.Dispose(); transaction.Dispose(); }
}

