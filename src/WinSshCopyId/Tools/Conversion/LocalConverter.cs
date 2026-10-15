using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace WinSshCopyId.Tools.Conversion;

public sealed record ConversionPreview(string DetectedType, long InputBytes, string Summary);
public sealed record ConversionResult(string Status, long OutputBytes, string Message);

public sealed class LocalConverter
{
    public const long MaximumInputBytes = 16 * 1024 * 1024;
    public const long MaximumOutputBytes = 32 * 1024 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public async Task<ConversionPreview> PreviewAsync(string source, CancellationToken cancellationToken = default)
    {
        var bytes = await ReadBoundedAsync(source, cancellationToken);
        return new(Detect(bytes), bytes.LongLength, "Only the type and byte count are shown; file contents remain local.");
    }

    public async Task<ConversionResult> ConvertAsync(string source, string destination, string adapterId,
        CancellationToken cancellationToken = default)
        => await ConvertCoreAsync(source, destination, adapterId, false, cancellationToken);

    /// <summary>Verification-only entry point. This does not enable a catalog capability or prove packaged isolation.</summary>
    public async Task<ConversionResult> VerifyManagedImplementationAsync(string source, string destination, string adapterId,
        CancellationToken cancellationToken = default)
        => await ConvertCoreAsync(source, destination, adapterId, true, cancellationToken);

    private async Task<ConversionResult> ConvertCoreAsync(string source, string destination, string adapterId,
        bool verificationOnly, CancellationToken cancellationToken)
    {
        var adapter = ConversionCatalog.Adapters.SingleOrDefault(a => a.Id == adapterId)
            ?? throw new NotSupportedException("Unknown conversion adapter.");
        if (!adapter.Available && !verificationOnly) throw new NotSupportedException(adapter.AvailabilityReason);
        if (verificationOnly && adapter.Sources.IsEmpty) throw new NotSupportedException(adapter.AvailabilityReason);
        var inputPath = Path.GetFullPath(source);
        var outputPath = Path.GetFullPath(destination);
        if (string.Equals(inputPath, outputPath, StringComparison.OrdinalIgnoreCase) || File.Exists(outputPath))
            throw new IOException("Choose a new destination. Existing files are never overwritten.");
        var bytes = await ReadBoundedAsync(inputPath, cancellationToken);
        if (!adapter.Sources.Contains(Detect(bytes))) throw new NotSupportedException("The detected source type is not compatible with this adapter.");
        cancellationToken.ThrowIfCancellationRequested();
        byte[] output = adapter.Id switch
        {
            "json-format" => FormatJson(bytes),
            "text-utf8" => Utf8.GetBytes(Text(bytes)),
            "csv-json" => JsonSerializer.SerializeToUtf8Bytes(ParseCsv(Text(bytes))),
            "base64-encode" => Encoding.ASCII.GetBytes(Convert.ToBase64String(bytes)),
            "base64-decode" => Convert.FromBase64String(Text(bytes)),
            "gzip" => Compress(bytes),
            "gunzip" => Expand(bytes, cancellationToken),
            _ => throw new NotSupportedException("Adapter execution is unavailable.")
        };
        if (output.LongLength > MaximumOutputBytes) throw new InvalidDataException("Output exceeds the 32 MiB safety limit.");
        if (adapter.Target == "json") { using var validated = JsonDocument.Parse(output, new JsonDocumentOptions { MaxDepth = 64 }); }
        var directory = Path.GetDirectoryName(outputPath)!;
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("The selected destination folder does not exist.");
        var root = Path.GetPathRoot(outputPath);
        if (root is not null && new DriveInfo(root).AvailableFreeSpace < output.LongLength + 1024 * 1024)
            throw new IOException("Insufficient destination storage capacity.");
        var temporary = Path.Combine(directory, ".conversion-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await File.WriteAllBytesAsync(temporary, output, cancellationToken);
            var reopened = await ReadBoundedAsync(temporary, cancellationToken, MaximumOutputBytes);
            if (!reopened.AsSpan().SequenceEqual(output)) throw new InvalidDataException("Output reopening validation failed.");
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, outputPath, false);
            return new("Converted", output.LongLength, "Output validated and saved atomically; source unchanged.");
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static string Detect(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith("%PDF-"u8)) return "pdf";
        if (bytes.StartsWith(new byte[] { 0x89, 0x50, 0x4e, 0x47, 13, 10, 26, 10 })) return "png";
        if (bytes.StartsWith(new byte[] { 0xff, 0xd8, 0xff })) return "jpeg";
        if (bytes.StartsWith("PK\x03\x04"u8)) return "zip";
        if (bytes.StartsWith(new byte[] { 0x1f, 0x8b })) return "gzip";
        if (bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes.Slice(8, 4).SequenceEqual("WAVE"u8)) return "wav";
        if (bytes.Length >= 12 && bytes.Slice(4, 4).SequenceEqual("ftyp"u8)) return "mp4";
        try
        {
            var text = Text(bytes.ToArray());
            if (text.Any(c => char.IsControl(c) && c is not '\r' and not '\n' and not '\t')) return "binary";
            try { using var json = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 64 }); return "json"; }
            catch (JsonException) { return "text"; }
        }
        catch (DecoderFallbackException) { return "binary"; }
    }

    private static string Text(byte[] bytes) => Utf8.GetString(bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) ? bytes.AsSpan(3) : bytes);
    private static byte[] FormatJson(byte[] bytes)
    {
        using var json = JsonDocument.Parse(Text(bytes), new JsonDocumentOptions { MaxDepth = 64 });
        return JsonSerializer.SerializeToUtf8Bytes(json.RootElement, new JsonSerializerOptions { WriteIndented = true });
    }
    private static byte[] Compress(byte[] bytes)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, true)) gzip.Write(bytes);
        return output.ToArray();
    }
    private static byte[] Expand(byte[] bytes, CancellationToken token)
    {
        using var input = new MemoryStream(bytes);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        int count;
        while ((count = gzip.Read(buffer)) != 0)
        {
            token.ThrowIfCancellationRequested();
            if (output.Length + count > MaximumOutputBytes) throw new InvalidDataException("Expanded output exceeds the 32 MiB safety limit.");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }
    private static async Task<byte[]> ReadBoundedAsync(string path, CancellationToken token, long limit = MaximumInputBytes)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        if (stream.Length > limit) throw new InvalidDataException("Input exceeds the adapter byte limit.");
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        int count;
        while ((count = await stream.ReadAsync(buffer, token)) != 0)
        {
            if (output.Length + count > limit) throw new InvalidDataException("Input exceeds the adapter byte limit.");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }
    private static List<string[]> ParseCsv(string text)
    {
        var rows = new List<string[]>(); var row = new List<string>(); var field = new StringBuilder();
        bool quoted = false, closed = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quoted)
            {
                if (c == '"') { if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; } else { quoted = false; closed = true; } }
                else field.Append(c);
            }
            else if (c == '"' && field.Length == 0 && !closed) quoted = true;
            else if (c == ',' || c is '\r' or '\n')
            {
                row.Add(field.ToString()); field.Clear(); closed = false;
                if (c != ',') { if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++; rows.Add(row.ToArray()); row.Clear(); }
            }
            else { if (closed || c == '"') throw new InvalidDataException("Malformed CSV quoting."); field.Append(c); }
            if (rows.Count > 100000 || row.Count > 4096) throw new InvalidDataException("CSV row or column safety limit exceeded.");
        }
        if (quoted) throw new InvalidDataException("Unclosed CSV quote.");
        if (field.Length != 0 || row.Count != 0 || closed) { row.Add(field.ToString()); rows.Add(row.ToArray()); }
        return rows;
    }
}
