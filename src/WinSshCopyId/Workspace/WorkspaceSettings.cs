using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinSshCopyId.Workspace;

public interface IClock { DateTimeOffset UtcNow { get; } }
public sealed class SystemClock : IClock { public DateTimeOffset UtcNow => DateTimeOffset.UtcNow; }
public enum WorkspaceLanguage { English, Cantonese, Bilingual }
public enum WorkspaceTheme { System, Light, Dark }
public enum WorkspaceDensity { Comfortable, Compact, Spacious }
public sealed record AttentionPreferences(bool Focus = false, bool LowStimulation = false,
    bool TimeAwareness = false, bool OneThingAtATime = false, bool Momentum = false);
public sealed record NarratorPreferences(bool Enabled = false, WorkspaceLanguage Language = WorkspaceLanguage.English,
    string? EnglishVoiceId = null, string? CantoneseVoiceId = null, double Rate = 1, double Pitch = 1);
public sealed record WorkspaceSettings
{
    public const string ProductIdentity = "WinSshCopyId";
    public int Version { get; init; } = 1;
    public string DisplayName { get; init; } = ProductIdentity;
    public WorkspaceLanguage Language { get; init; } = WorkspaceLanguage.English;
    public WorkspaceTheme Theme { get; init; } = WorkspaceTheme.System;
    public WorkspaceDensity Density { get; init; } = WorkspaceDensity.Comfortable;
    public string SeedColor { get; init; } = "#0061A4";
    public string FontFamily { get; init; } = "Segoe UI Variable";
    public double FontSize { get; init; } = 14;
    public int EnglishFunnyLevel { get; init; } = 5;
    public int CantoneseFunnyLevel { get; init; } = 5;
    public bool ShowDialogEmojis { get; init; } = true;
    public bool ReducedMotion { get; init; }
    public bool AccessibilityButtonVisible { get; init; } = true;
    public AttentionPreferences Attention { get; init; } = new();
    public NarratorPreferences Narrator { get; init; } = new();
    public WorkspaceSettings Validate()
    {
        if (Version != 1 || string.IsNullOrWhiteSpace(DisplayName) || DisplayName.Length > 120 ||
            DisplayName.Any(char.IsControl) || !Enum.IsDefined(Language) || !Enum.IsDefined(Theme) ||
            !Enum.IsDefined(Density) || EnglishFunnyLevel is < 1 or > 5 || CantoneseFunnyLevel is < 1 or > 5 ||
            FontSize is < 10 or > 48 || !double.IsFinite(FontSize) || string.IsNullOrWhiteSpace(FontFamily) || FontFamily.Length > 160 ||
            SeedColor is null || !System.Text.RegularExpressions.Regex.IsMatch(SeedColor, "^#[0-9a-fA-F]{6}([0-9a-fA-F]{2})?$", System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)) ||
            Attention is null || Narrator is null || !Enum.IsDefined(Narrator.Language) ||
            !double.IsFinite(Narrator.Rate) || Narrator.Rate is < .1 or > 10 || !double.IsFinite(Narrator.Pitch) || Narrator.Pitch is < 0 or > 2 ||
            Narrator.EnglishVoiceId?.Length > 256 || Narrator.CantoneseVoiceId?.Length > 256)
            throw new InvalidDataException("Settings are unsupported or outside supported limits.");
        return this;
    }
}

public interface IPrivateStorage
{
    Task<byte[]?> ReadAsync(string key, CancellationToken cancellationToken = default);
    Task WriteAtomicAsync(string key, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);
    Task DeleteAsync(string key, CancellationToken cancellationToken = default);
}

public sealed class DirectoryPrivateStorage : IPrivateStorage
{
    private readonly string root;
    public DirectoryPrivateStorage(string privateApplicationDataRoot) => root = Path.GetFullPath(privateApplicationDataRoot);
    private string PathFor(string key)
    {
        if (key.Length is < 1 or > 100 || key.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '.' and not '-' and not '_') || key.Contains(".."))
            throw new ArgumentException("Invalid storage key.", nameof(key));
        return Path.Combine(root, key);
    }
    public async Task<byte[]?> ReadAsync(string key, CancellationToken cancellationToken = default)
    {
        var path = PathFor(key);
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException("Private record exceeds its size limit.");
        return await File.ReadAllBytesAsync(path, cancellationToken);
    }
    public async Task WriteAtomicAsync(string key, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        if (data.Length > 1024 * 1024) throw new InvalidDataException("Private record exceeds its size limit.");
        Directory.CreateDirectory(root);
        var path = PathFor(key);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { await file.WriteAsync(data, cancellationToken); await file.FlushAsync(cancellationToken); file.Flush(true); }
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(path)) File.Replace(temporary, path, path + ".bak");
            else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); File.Delete(PathFor(key)); File.Delete(PathFor(key) + ".bak"); return Task.CompletedTask; }
}

public sealed record SettingsLoadResult(WorkspaceSettings Settings, bool Recovered, string? Warning);
public sealed class WorkspaceSettingsStore(IPrivateStorage storage)
{
    internal static readonly JsonSerializerOptions JsonOptions = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 16 };
    private readonly SemaphoreSlim mutex = new(1);
    public async Task<SettingsLoadResult> LoadAsync(CancellationToken cancellationToken = default)
    {
        await mutex.WaitAsync(cancellationToken);
        try
        {
            foreach (var key in new[] { "settings.json", "settings.json.bak" })
            {
                try
                {
                    var bytes = await storage.ReadAsync(key, cancellationToken);
                    if (bytes is null) continue;
                    var value = JsonSerializer.Deserialize<WorkspaceSettings>(bytes, JsonOptions)?.Validate() ?? throw new InvalidDataException();
                    return new(value, key.EndsWith(".bak"), key.EndsWith(".bak") ? "Recovered the previous valid settings." : null);
                }
                catch (Exception e) when (e is JsonException or InvalidDataException or IOException) { }
            }
            return new(new(), false, "No valid saved settings were available; defaults are active.");
        }
        finally { mutex.Release(); }
    }
    public async Task SaveAsync(WorkspaceSettings settings, CancellationToken cancellationToken = default)
    {
        settings.Validate();
        await mutex.WaitAsync(cancellationToken);
        try { await storage.WriteAtomicAsync("settings.json", JsonSerializer.SerializeToUtf8Bytes(settings, JsonOptions), cancellationToken); }
        finally { mutex.Release(); }
    }
}

