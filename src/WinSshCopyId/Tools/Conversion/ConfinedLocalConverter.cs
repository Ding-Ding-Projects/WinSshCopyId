using System.Collections.Immutable;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace WinSshCopyId.Tools.Conversion;

public sealed class ConfinedLocalConverter
{
    private readonly string packageDirectory;
    private readonly ImmutableDictionary<string, string> inventory;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ConfinedWorkerReceipt> receipts = new();
    public ConfinedLocalConverter(string packagedWorkerDirectory, ImmutableDictionary<string, string> pinnedPackageInventory)
    {
        packageDirectory = Path.GetFullPath(packagedWorkerDirectory);
        inventory = pinnedPackageInventory.WithComparers(StringComparer.Ordinal);
        if (inventory.Count is < 1 or > 4096 || !inventory.ContainsKey("WinSshCopyId.ToolWorker.exe"))
            throw new ArgumentException("A complete pinned worker package inventory is required.");
        foreach (var (name, hash) in inventory)
            if (Path.IsPathRooted(name) || name.Contains('\\') || name.Split('/').Any(x => x is ".." or "." or "")
                || name.Split('/').Any(x => x.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                || hash.Length != 64 || !hash.All(Uri.IsHexDigit)) throw new ArgumentException("Invalid packaged worker inventory.");
    }

    public ImmutableArray<ConversionAdapter> AvailableAdapters => ConversionCatalog.Adapters
        .Where(x => receipts.ContainsKey(x.Id))
        .Select(x => x with { Available = true, AvailabilityReason = "This instance completed a genuine confined adapter verification with the pinned package." })
        .ToImmutableArray();

    public async Task<ConfinedWorkerReceipt> VerifyAdapterAsync(string adapterId, CancellationToken token = default)
    {
        var sample = adapterId switch
        {
            "json-format" => Encoding.UTF8.GetBytes("{\"value\":1}"),
            "csv-json" => Encoding.UTF8.GetBytes("a,b\r\n1,2\r\n"),
            "text-utf8" => Encoding.UTF8.GetBytes("Text\r\n"),
            "base64-encode" => new byte[] { 1, 2, 3 },
            "base64-decode" => Encoding.ASCII.GetBytes("AQID"),
            "gzip" => Encoding.UTF8.GetBytes("Text"),
            "gunzip" => GzipSample(),
            _ => throw new NotSupportedException("No managed verification sample exists.")
        };
        var stage = await PrepareAsync(sample, token);
        try
        {
            var receipt = await AppContainerWorker.ExecuteAsync(new(stage, adapterId, inventory), token);
            var output = await File.ReadAllBytesAsync(Path.Combine(stage, "result", "output.bin"), token);
            ValidateSample(adapterId, output);
            receipts[adapterId] = receipt;
            return receipt;
        }
        finally { DeleteOwnedStage(stage); }
    }

    public async Task<ConversionResult> ConvertAsync(string source, string destination, string adapterId, CancellationToken token = default)
    {
        if (!receipts.ContainsKey(adapterId)) throw new NotSupportedException("Verify this adapter in the confined packaged worker before conversion.");
        var sourcePath = Path.GetFullPath(source); var outputPath = Path.GetFullPath(destination);
        if (string.Equals(sourcePath, outputPath, StringComparison.OrdinalIgnoreCase) || File.Exists(outputPath))
            throw new IOException("A new destination is required; existing files are never overwritten.");
        var directory = Path.GetDirectoryName(outputPath)!;
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("Destination folder does not exist.");
        if (new DriveInfo(Path.GetPathRoot(outputPath)!).AvailableFreeSpace < LocalConverter.MaximumOutputBytes + 1024 * 1024)
            throw new IOException("Destination storage capacity is insufficient.");
        var bytes = await ReadInputAsync(sourcePath, token);
        var stage = await PrepareAsync(bytes, token);
        var temporary = Path.Combine(directory, ".conversion-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            var receipt = await AppContainerWorker.ExecuteAsync(new(stage, adapterId, inventory), token);
            var stagedOutput = Path.Combine(stage, "result", "output.bin");
            await using (var input = File.OpenRead(stagedOutput))
            await using (var target = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                if (input.Length > LocalConverter.MaximumOutputBytes) throw new InvalidDataException("Output exceeds its byte limit.");
                await input.CopyToAsync(target, token);
                await target.FlushAsync(token);
            }
            await using (var readback = File.OpenRead(temporary))
                if (!Convert.ToHexString(await SHA256.HashDataAsync(readback, token)).Equals(receipt.OutputSha256, StringComparison.Ordinal))
                    throw new InvalidDataException("Final output hash differs from the confined result.");
            token.ThrowIfCancellationRequested();
            var length = new FileInfo(temporary).Length;
            File.Move(temporary, outputPath, false);
            return new("Converted", length, "Confined conversion validated and saved atomically; source unchanged.");
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); DeleteOwnedStage(stage); }
    }

    /// <summary>Explicit test-only acquisition of hashes. Production must use a reviewed packaged inventory.</summary>
    public static async Task<ConfinedLocalConverter> CreateForPackageVerificationOnlyAsync(string packageDirectory, CancellationToken token = default)
    {
        var hashes = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(packageDirectory, "*", SearchOption.AllDirectories))
        {
            if (hashes.Count == 4096) throw new InvalidDataException("Package member limit exceeded.");
            RejectReparse(path);
            await using var file = File.OpenRead(path);
            hashes.Add(Path.GetRelativePath(packageDirectory, path).Replace('\\', '/'), Convert.ToHexString(await SHA256.HashDataAsync(file, token)));
        }
        return new(packageDirectory, hashes.ToImmutable());
    }

    private async Task<string> PrepareAsync(byte[] input, CancellationToken token)
    {
        var stage = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinSshCopyId", "ConversionSandbox", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            long packageBytes = 0;
            foreach (var (relative, expected) in inventory)
            {
                token.ThrowIfCancellationRequested();
                var original = Path.Combine(packageDirectory, relative.Replace('/', Path.DirectorySeparatorChar));
                RejectReparse(original);
                await using var source = new FileStream(original, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
                if (source.Length > 256L * 1024 * 1024 || (packageBytes = checked(packageBytes + source.Length)) > 512L * 1024 * 1024)
                    throw new InvalidDataException("Worker package exceeds its staging byte limit.");
                var target = Path.Combine(stage, "package", relative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await using (var destination = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                    await source.CopyToAsync(destination, 81920, token);
                await using var copy = File.OpenRead(target);
                if (!Convert.ToHexString(await SHA256.HashDataAsync(copy, token)).Equals(expected, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Pinned package hash mismatch.");
            }
            await File.WriteAllBytesAsync(Path.Combine(stage, "input.bin"), input, token);
            return stage;
        }
        catch { DeleteOwnedStage(stage); throw; }
    }
    private static async Task<byte[]> ReadInputAsync(string source, CancellationToken token)
    {
        await using var file = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        if (file.Length > LocalConverter.MaximumInputBytes) throw new InvalidDataException("Input exceeds its byte limit.");
        using var output = new MemoryStream(); var buffer = new byte[81920]; int count;
        while ((count = await file.ReadAsync(buffer, token)) != 0)
        { if (output.Length + count > LocalConverter.MaximumInputBytes) throw new InvalidDataException("Input exceeds its byte limit."); output.Write(buffer, 0, count); }
        return output.ToArray();
    }
    private static void RejectReparse(string path)
    {
        for (var item = Path.GetFullPath(path); !string.IsNullOrEmpty(item); item = Path.GetDirectoryName(item))
            if ((File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0) throw new IOException("Reparse points are not supported.");
    }
    private static void DeleteOwnedStage(string stage)
    {
        // The only caller values come from PrepareAsync's freshly generated GUID path.
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinSshCopyId", "ConversionSandbox");
        if (!string.Equals(Path.GetDirectoryName(stage), root, StringComparison.OrdinalIgnoreCase) || !Guid.TryParseExact(Path.GetFileName(stage), "N", out _))
            throw new IOException("Staging ownership cannot be proven.");
        if (Directory.Exists(stage)) { RejectReparse(stage); Directory.Delete(stage, true); }
    }
    private static byte[] GzipSample()
    { using var output = new MemoryStream(); using (var gzip = new GZipStream(output, CompressionLevel.Optimal, true)) gzip.Write("Text"u8); return output.ToArray(); }
    private static void ValidateSample(string adapter, byte[] output)
    {
        bool valid = adapter switch
        {
            "json-format" => Encoding.UTF8.GetString(output).Contains("\"value\": 1", StringComparison.Ordinal),
            "csv-json" => Encoding.UTF8.GetString(output) == "[[\"a\",\"b\"],[\"1\",\"2\"]]",
            "text-utf8" => output.AsSpan().SequenceEqual("Text\r\n"u8),
            "base64-encode" => output.AsSpan().SequenceEqual("AQID"u8),
            "base64-decode" => output.AsSpan().SequenceEqual(new byte[] { 1, 2, 3 }),
            "gunzip" => output.AsSpan().SequenceEqual("Text"u8),
            "gzip" => output.AsSpan().SequenceEqual(GzipSample()),
            _ => false
        };
        if (!valid) throw new InvalidDataException("Confined adapter verification sample did not match its expected result.");
    }
}
