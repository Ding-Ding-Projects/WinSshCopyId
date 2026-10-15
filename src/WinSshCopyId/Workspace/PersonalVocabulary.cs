using System.Text.Json;

namespace WinSshCopyId.Workspace;

public sealed class PersonalVocabulary(IPrivateStorage storage)
{
    public const int MaxBytes = 256 * 1024;
    private IReadOnlyDictionary<string, string> entries = new Dictionary<string, string>();
    public bool IsLoaded { get; private set; }
    public string ApplyPrivateText(string shippedText) => entries.TryGetValue(shippedText, out var value) ? value : shippedText;
    public static IReadOnlyDictionary<string, string> Validate(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Length > MaxBytes) throw new InvalidDataException("The selected JSON exceeds 256 KiB.");
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Expected a versioned JSON object.");
        var properties = root.EnumerateObject().ToArray();
        if (properties.Length != 2 || properties.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != 2 ||
            !root.TryGetProperty("schemaVersion", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number) || number != 1 ||
            !root.TryGetProperty("entries", out var values) || values.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Unsupported JSON schema.");
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in values.EnumerateObject())
        {
            if (property.Name.EnumerateRunes().Count() is < 1 or > 160 || property.Name is "__proto__" or "constructor" or "prototype" ||
                property.Value.ValueKind != JsonValueKind.String) throw new InvalidDataException("Invalid replacement entry.");
            var value = property.Value.GetString()!;
            if (value.EnumerateRunes().Count() > 1000 || value.Any(c => c is <= '\u0008' or '\u000b' or '\u000c' or >= '\u000e' and <= '\u001f' or '\u007f') ||
                !result.TryAdd(property.Name, value)) throw new InvalidDataException("Invalid or repeated replacement entry.");
        }
        return result;
    }
    public async Task ImportAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
    {
        var validated = Validate(bytes);
        await storage.WriteAtomicAsync("personal-vocabulary.json", bytes, cancellationToken);
        entries = validated;
        IsLoaded = true;
    }
    public async Task<bool> LoadAsync(CancellationToken cancellationToken = default)
    {
        entries = new Dictionary<string, string>(); IsLoaded = false;
        try
        {
            var bytes = await storage.ReadAsync("personal-vocabulary.json", cancellationToken);
            if (bytes is null) return false;
            entries = Validate(bytes); IsLoaded = true; return true;
        }
        catch (Exception e) when (e is JsonException or InvalidDataException or IOException) { return false; }
    }
    public async Task ClearAsync(CancellationToken cancellationToken = default)
    { await storage.DeleteAsync("personal-vocabulary.json", cancellationToken); entries = new Dictionary<string, string>(); IsLoaded = false; }
}
