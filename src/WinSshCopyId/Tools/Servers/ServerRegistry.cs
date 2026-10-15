using System.Collections.Immutable;
using System.Text.Json;

namespace WinSshCopyId.Tools.Servers;

public sealed class ServerRegistry
{
    private readonly string path;
    private readonly SemaphoreSlim mutex = new(1, 1);
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private sealed record Document(int Version, ImmutableArray<ServerInstance> Instances);
    public ServerRegistry(string path) => this.path = Path.GetFullPath(path);
    public async Task<ImmutableArray<ServerInstance>> ReadAsync(CancellationToken cancellationToken = default)
    {
        await mutex.WaitAsync(cancellationToken);
        try { return await ReadCoreAsync(cancellationToken); }
        finally { mutex.Release(); }
    }
    private async Task<ImmutableArray<ServerInstance>> ReadCoreAsync(CancellationToken token)
    {
        if (!File.Exists(path)) return [];
        if (new FileInfo(path).Length > 16 * 1024 * 1024) throw new InvalidDataException("Registry exceeds 16 MiB.");
        await using var stream = File.OpenRead(path);
        var document = await JsonSerializer.DeserializeAsync<Document>(stream, Json, token)
            ?? throw new InvalidDataException("Empty registry.");
        if (document.Version != 1 || document.Instances.IsDefault) throw new InvalidDataException("Unsupported registry format.");
        if (document.Instances.Select(x => x.Id).Distinct().Count() != document.Instances.Length)
            throw new InvalidDataException("Duplicate instance identities.");
        return document.Instances;
    }
    public async Task SaveAsync(ServerInstance instance, CancellationToken cancellationToken = default)
    {
        ServerLifecyclePlanner.ValidateInstance(instance);
        await mutex.WaitAsync(cancellationToken);
        string? staging = null;
        try
        {
            var current = await ReadCoreAsync(cancellationToken);
            var updated = current.Where(x => x.Id != instance.Id).Append(instance).ToImmutableArray();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            staging = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await using (var stream = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                81920, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, new Document(1, updated), Json, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(staging, path, true);
            staging = null;
        }
        finally { if (staging is not null) File.Delete(staging); mutex.Release(); }
    }
}
