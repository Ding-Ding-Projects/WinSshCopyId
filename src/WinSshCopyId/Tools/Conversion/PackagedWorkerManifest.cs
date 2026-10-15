using System.Collections.Immutable;
using System.Text.Json;

namespace WinSshCopyId.Tools.Conversion;

public static class PackagedWorkerManifest
{
    public static async Task<ConfinedLocalConverter> LoadAsync(string manifestPath, string workerDirectory,
        string expectedSourceCommit, CancellationToken token = default)
    {
        if (expectedSourceCommit.Length != 40 || !expectedSourceCommit.All(Uri.IsHexDigit))
            throw new ArgumentException("The application's exact expected source commit is required.");
        if (new FileInfo(manifestPath).Length > 2 * 1024 * 1024) throw new InvalidDataException("Worker manifest exceeds its byte bound.");
        await using var input = File.OpenRead(manifestPath);
        using var json = await JsonDocument.ParseAsync(input, new JsonDocumentOptions { MaxDepth = 16 }, token);
        if (json.RootElement.GetProperty("sourceCommit").GetString() != expectedSourceCommit ||
            json.RootElement.GetProperty("runtime").GetString() != "win-x64")
            throw new InvalidDataException("Worker manifest does not match this application's exact source and runtime.");
        var members = json.RootElement.GetProperty("members");
        if (members.GetArrayLength() is < 1 or > 4096) throw new InvalidDataException("Worker manifest inventory exceeds its count bound.");
        var hashes = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        long total = 0;
        foreach (var member in members.EnumerateArray())
        {
            var relative = member.GetProperty("relativePath").GetString() ?? throw new InvalidDataException("Missing worker member path.");
            var bytes = member.GetProperty("bytes").GetInt64();
            var hash = member.GetProperty("sha256").GetString() ?? throw new InvalidDataException("Missing worker member digest.");
            if (bytes is < 0 or > 256L * 1024 * 1024 || (total = checked(total + bytes)) > 512L * 1024 * 1024 || !hashes.TryAdd(relative, hash))
                throw new InvalidDataException("Worker package inventory is excessive or duplicated.");
        }
        return new(workerDirectory, hashes.ToImmutable());
    }
}
