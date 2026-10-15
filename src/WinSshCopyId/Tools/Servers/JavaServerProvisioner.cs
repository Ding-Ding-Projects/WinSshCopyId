using System.Net.Http;
using System.Security.Cryptography;

namespace WinSshCopyId.Tools.Servers;

public sealed record JavaProvisionReceipt(string Version, string Directory, string ServerJar, string Sha256,
    string SourceSha1, Uri Source, int? RequiredJavaMajorVersion, DateTimeOffset ProvisionedAt);

/// <summary>Creates a new directory from a selected resolved official distribution. Never starts Java or replaces a world.</summary>
public sealed class JavaServerProvisioner : IDisposable
{
    private readonly HttpClient http;
    private readonly bool ownsHttp;
    public JavaServerProvisioner(HttpClient? controlledTransport = null)
    {
        http = controlledTransport ?? new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(10) });
        ownsHttp = controlledTransport is null;
    }
    public async Task<JavaProvisionReceipt> CreateAsync(ResolvedJavaDistribution distribution, string newInstanceDirectory,
        bool eulaAccepted, int serverPort, IProgress<ServerProgress>? progress = null, CancellationToken token = default)
    {
        if (!eulaAccepted) throw new InvalidOperationException("Explicit Minecraft EULA consent is required before provisioning.");
        if (serverPort is < 1 or > 65535 || distribution.Bytes is < 1 or > 2L * 1024 * 1024 * 1024 ||
            distribution.Sha1.Length != 40 || !distribution.Sha1.All(Uri.IsHexDigit) || string.IsNullOrWhiteSpace(distribution.Version))
            throw new InvalidDataException("Invalid resolved official distribution.");
        var uri = distribution.Download;
        if (uri.Scheme != "https" || !uri.IsDefaultPort || uri.Host is not ("piston-data.mojang.com" or "launcher.mojang.com"))
            throw new InvalidDataException("Distribution download is not an admitted official source.");
        if (!Path.IsPathFullyQualified(newInstanceDirectory)) throw new ArgumentException("An absolute new instance directory is required.");
        var target = Path.GetFullPath(newInstanceDirectory).TrimEnd(Path.DirectorySeparatorChar);
        var parent = Path.GetDirectoryName(target) ?? throw new ArgumentException("An instance parent directory is required.");
        RejectLinks(parent);
        if (!Directory.Exists(parent)) throw new DirectoryNotFoundException("Choose an existing parent directory.");
        if (Directory.Exists(target) || File.Exists(target)) throw new IOException("Provisioning creates a new directory; existing data is preserved.");
        if (new DriveInfo(Path.GetPathRoot(target)!).AvailableFreeSpace < distribution.Bytes + 64 * 1024 * 1024)
            throw new IOException("Insufficient storage for selected server distribution.");
        var stage = target + ".provision-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(stage);
        try
        {
            var jar = Path.Combine(stage, "server.jar");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromMinutes(10));
            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            response.EnsureSuccessStatusCode();
            if (response.RequestMessage?.RequestUri != uri) throw new InvalidDataException("Distribution source redirect was not admitted.");
            if (response.Content.Headers.ContentLength is { } length && length != distribution.Bytes) throw new InvalidDataException("Distribution length does not match official metadata.");
            await using (var input = await response.Content.ReadAsStreamAsync(deadline.Token))
            await using (var output = new FileStream(jar, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
            {
                var buffer = new byte[65536]; long copied = 0; int count;
                while ((count = await input.ReadAsync(buffer, deadline.Token)) != 0)
                {
                    copied += count;
                    if (copied > distribution.Bytes) throw new InvalidDataException("Distribution exceeded admitted length.");
                    await output.WriteAsync(buffer.AsMemory(0, count), deadline.Token);
                    progress?.Report(new(Guid.Empty, "Downloading selected official distribution", copied, distribution.Bytes));
                }
                if (copied != distribution.Bytes) throw new InvalidDataException("Distribution download is incomplete.");
            }
            await using var verification = new FileStream(jar, FileMode.Open, FileAccess.Read, FileShare.Read);
            var sha1 = Convert.ToHexString(await SHA1.HashDataAsync(verification, deadline.Token));
            if (!sha1.Equals(distribution.Sha1, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Distribution does not match official metadata SHA-1.");
            verification.Position = 0;
            var sha256 = Convert.ToHexString(await SHA256.HashDataAsync(verification, deadline.Token));
            verification.Close();
            await File.WriteAllTextAsync(Path.Combine(stage, "eula.txt"), "eula=true\n", deadline.Token);
            await File.WriteAllTextAsync(Path.Combine(stage, "server.properties"), "online-mode=true\nserver-port=" + serverPort.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n", deadline.Token);
            var receipt = new JavaProvisionReceipt(distribution.Version, target, Path.Combine(target, "server.jar"), sha256,
                sha1, uri, distribution.JavaMajorVersion, DateTimeOffset.UtcNow);
            await File.WriteAllTextAsync(Path.Combine(stage, ".winsshcopyid-provision.json"), System.Text.Json.JsonSerializer.Serialize(receipt), deadline.Token);
            deadline.Token.ThrowIfCancellationRequested(); RejectLinks(parent);
            Directory.Move(stage, target);
            return receipt;
        }
        finally { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
    }
    private static void RejectLinks(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Provisioning paths may not contain reparse points.");
    }
    public void Dispose() { if (ownsHttp) http.Dispose(); }
}
