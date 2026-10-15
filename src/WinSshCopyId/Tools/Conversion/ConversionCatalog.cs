using System.Collections.Immutable;

namespace WinSshCopyId.Tools.Conversion;

public sealed record ConversionAdapter(string Id, string Category, string Name, ImmutableArray<string> Sources,
    string Target, bool Available, string AvailabilityReason, string Disclosure);

public static class ConversionCatalog
{
    public static IReadOnlyList<string> Categories { get; } = Array.AsReadOnly(new[]
    { "Documents/PDF", "Images", "Audio", "Video", "Archives", "Structured Data/Spreadsheets", "Code/Text", "Binary Encodings" });

    public static IReadOnlyList<ConversionAdapter> Adapters { get; } = Array.AsReadOnly(new[]
    {
        Managed("json-format", "Structured Data/Spreadsheets", "Format JSON", ["json"], "json", "Whitespace changes; values are preserved. UTF-8 output without BOM."),
        Managed("csv-json", "Structured Data/Spreadsheets", "CSV to JSON rows", ["text", "csv"], "json", "All CSV fields remain strings; the first row is retained as data. UTF-8 output."),
        Managed("text-utf8", "Code/Text", "Strict UTF-8 text", ["text", "json", "csv"], "txt", "Preserves text and line endings; removes a UTF-8 BOM. Other encodings are rejected."),
        Managed("base64-encode", "Binary Encodings", "Encode Base64", ["binary", "text", "json", "csv", "pdf", "zip", "png", "jpeg", "wav", "mp4"], "base64", "Encodes original bytes; no metadata is interpreted."),
        Managed("base64-decode", "Binary Encodings", "Decode Base64", ["text"], "bin", "Decoded bytes are validated by a Base64 round trip; the destination extension does not imply a media format."),
        Managed("gzip", "Archives", "Compress GZip", ["binary", "text", "json", "csv", "pdf", "zip", "png", "jpeg", "wav", "mp4"], "gz", "Single stream only; source filename and timestamps are not stored."),
        Managed("gunzip", "Archives", "Decompress GZip", ["gzip"], "bin", "Single stream output with a strict expanded-byte limit; no archive paths are extracted."),
        Missing("pdf", "Documents/PDF", "PDF inspect, split, merge, extract, reorder, rotate and metadata", "pdf", "A packaged offline PDF engine and isolation receipt are not present."),
        Missing("image", "Images", "Image conversion", "png", "A packaged isolated image decoder and pixel-limit receipt are not present."),
        Missing("audio", "Audio", "Audio conversion", "wav", "A packaged offline audio engine and isolation receipt are not present."),
        Missing("video", "Video", "Video conversion", "mp4", "A packaged offline video engine and isolation receipt are not present."),
        Missing("office", "Structured Data/Spreadsheets", "Office and spreadsheet conversion", "xlsx", "A packaged offline document engine and isolation receipt are not present."),
        Missing("7z", "Archives", "ZIP and 7z extraction", "7z", "A packaged archive engine with path, entry-count and expanded-byte validation is not present.")
    });

    private static ConversionAdapter Managed(string id, string category, string name, string[] sources, string target, string disclosure) =>
        new(id, category, name, sources.ToImmutableArray(), target, false,
            "Managed implementation exists, but packaged offline operation and isolated-process resource enforcement have no verified receipt.", disclosure);
    private static ConversionAdapter Missing(string id, string category, string name, string target, string reason) =>
        new(id, category, name, [], target, false, reason, "Unavailable; no conversion is attempted.");
}
