using System.Collections.Immutable;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace WinSshCopyId.Tools.Servers;

public sealed record ServerVersion(string Id, string Type, Uri Metadata, string MetadataSha1,
    DateTimeOffset ReleasedAt, string DistributionState = "Unresolved");
public sealed record ServerCatalogueSnapshot(DateTimeOffset RetrievedAt, string ResponseSha256,
    ImmutableArray<ServerVersion> Versions, bool Complete, ImmutableArray<string> Gaps);
public sealed record ResolvedJavaDistribution(string Version, Uri Download, string Sha1, long Bytes,
    int? JavaMajorVersion, Uri MetadataSource);
public sealed class ServerCatalogue : IDisposable
{
    public static readonly Uri JavaManifest = new("https://piston-meta.mojang.com/mc/game/version_manifest_v2.json");
    private readonly HttpClient client;
    private readonly bool ownsClient;
    public ServerCatalogue()
    {
        client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(10) });
        ownsClient = true;
    }
    public ServerCatalogue(HttpClient client) => this.client = client;
    public void Dispose() { if (ownsClient) client.Dispose(); }
    private static bool Allowed(Uri uri) => uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort &&
        uri.Host is "piston-meta.mojang.com" or "piston-data.mojang.com" or "launchermeta.mojang.com" or "launcher.mojang.com";
    private async Task<byte[]> ReadAsync(Uri uri, CancellationToken token)
    {
        if (!Allowed(uri)) throw new InvalidDataException("Unapproved catalog source.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        response.EnsureSuccessStatusCode();
        if (response.RequestMessage?.RequestUri is not { } final || !Allowed(final)) throw new InvalidDataException("Unapproved catalog redirect.");
        if (response.Content.Headers.ContentLength > 16 * 1024 * 1024) throw new InvalidDataException("Catalog exceeds 16 MiB.");
        await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
        using var result = new MemoryStream();
        var buffer = new byte[32768];
        int read;
        while ((read = await stream.ReadAsync(buffer, deadline.Token)) != 0)
        {
            if (result.Length + read > 16 * 1024 * 1024) throw new InvalidDataException("Catalog exceeds 16 MiB.");
            result.Write(buffer, 0, read);
        }
        return result.ToArray();
    }
    public async Task<ServerCatalogueSnapshot> RefreshJavaAsync(CancellationToken token = default)
    {
        var bytes = await ReadAsync(JavaManifest, token);
        using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
        var entries = ImmutableArray.CreateBuilder<ServerVersion>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var version in json.RootElement.GetProperty("versions").EnumerateArray())
        {
            var id = version.GetProperty("id").GetString() ?? throw new InvalidDataException("Missing version id.");
            var metadata = new Uri(version.GetProperty("url").GetString()!);
            var sha1 = version.GetProperty("sha1").GetString()!;
            if (!ids.Add(id) || !Allowed(metadata) || sha1.Length != 40 || !sha1.All(Uri.IsHexDigit)) throw new InvalidDataException("Invalid catalog entry.");
            entries.Add(new(id, version.GetProperty("type").GetString()!, metadata, sha1, version.GetProperty("releaseTime").GetDateTimeOffset()));
        }
        return new(DateTimeOffset.UtcNow, Convert.ToHexString(SHA256.HashData(bytes)), entries.ToImmutable(), false,
            ["All returned Java metadata entries retained; dedicated server distribution and runtime must be resolved per version.",
             "Complete historical Bedrock and third-party flavor catalogs are unavailable."]);
    }
    public async Task<ResolvedJavaDistribution?> ResolveJavaAsync(ServerVersion version, CancellationToken token = default)
    {
        var bytes = await ReadAsync(version.Metadata, token);
        if (!Convert.ToHexString(SHA1.HashData(bytes)).Equals(version.MetadataSha1, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Metadata digest mismatch.");
        using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 64 });
        if (!json.RootElement.TryGetProperty("downloads", out var downloads) || !downloads.TryGetProperty("server", out var server)) return null;
        var url = new Uri(server.GetProperty("url").GetString()!);
        var sha1 = server.GetProperty("sha1").GetString()!;
        var size = server.GetProperty("size").GetInt64();
        if (!Allowed(url) || size <= 0 || sha1.Length != 40 || !sha1.All(Uri.IsHexDigit)) throw new InvalidDataException("Invalid distribution descriptor.");
        int? java = json.RootElement.TryGetProperty("javaVersion", out var runtime) && runtime.TryGetProperty("majorVersion", out var major) ? major.GetInt32() : null;
        return new(version.Id, url, sha1, size, java, version.Metadata);
    }
    public static ImmutableArray<ServerVersion> Page(ServerCatalogueSnapshot snapshot, int offset, int count)
    {
        if (offset < 0 || count is < 1 or > 250) throw new ArgumentOutOfRangeException(nameof(count));
        return snapshot.Versions.Skip(offset).Take(count).ToImmutableArray();
    }
}
