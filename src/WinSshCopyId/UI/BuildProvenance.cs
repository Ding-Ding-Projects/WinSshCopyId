using System.Globalization;
using System.Text.Json;

namespace WinSshCopyId.UI;

public sealed record BuildProvenance(string Version,string? SourceCommit,DateTimeOffset? BuiltAtUtc,string? Runtime)
{
    public string Display=>"v"+Version+" · "+(BuiltAtUtc is {} at?at.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'",CultureInfo.InvariantCulture):"Build time unavailable");
    public static BuildProvenance Read(string applicationDirectory,string fallbackVersion)
    {
        var path=Path.Combine(applicationDirectory,"build-info.json");
        try
        {
            if(!File.Exists(path)||new FileInfo(path).Length>16384)return new(fallbackVersion,null,null,null);
            using var document=JsonDocument.Parse(File.ReadAllBytes(path));var root=document.RootElement;
            var version=root.GetProperty("version").GetString();var source=root.GetProperty("sourceCommit").GetString();
            var timestamp=root.GetProperty("builtAtUtc").GetString();
            if(string.IsNullOrWhiteSpace(version)||version.Length>120||version.Any(char.IsControl)||source?.Length!=40||source.Any(c=>!Uri.IsHexDigit(c))||!DateTimeOffset.TryParse(timestamp,CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out var built))return new(fallbackVersion,null,null,null);
            return new(version,source,built,root.TryGetProperty("runtime",out var runtime)?runtime.GetString():null);
        }
        catch(Exception error) when(error is IOException or JsonException or InvalidOperationException or KeyNotFoundException or UnauthorizedAccessException){return new(fallbackVersion,null,null,null);}
    }
}
