using System.Collections.Immutable;
using System.Globalization;
using System.Text;

namespace WinSshCopyId.Tools.Servers;

/// <summary>Preserves comments, unknown keys, ordering and the original newline style.</summary>
public sealed class ServerProperties
{
    private readonly ImmutableArray<string> lines;
    private readonly string newline;
    private readonly bool trailingNewline;
    private ServerProperties(ImmutableArray<string> lines, string newline, bool trailingNewline)
        => (this.lines, this.newline, this.trailingNewline) = (lines, newline, trailingNewline);
    public static ServerProperties Parse(string text)
    {
        if (text.Length > 4 * 1024 * 1024) throw new InvalidDataException("Configuration exceeds 4 MiB.");
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var trailing = text.EndsWith('\n');
        var parts = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (trailing) parts = parts[..^1];
        return new(parts.ToImmutableArray(), newline, trailing);
    }
    public ImmutableDictionary<string, string> Values
    {
        get
        {
            var result = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
            foreach (var line in lines)
            {
                var pair = Split(line);
                if (pair is { } p) result[p.Key] = p.Value;
            }
            return result.ToImmutable();
        }
    }
    private static KeyValuePair<string, string>? Split(string line)
    {
        var trimmed = line.TrimStart();
        if (trimmed.Length == 0 || trimmed[0] is '#' or '!') return null;
        var separator = line.IndexOf('=');
        return separator < 1 ? null : new(line[..separator].Trim(), line[(separator + 1)..]);
    }
    public ServerProperties Set(string key, string value)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Any(c => c is '=' or '\r' or '\n' or '\0') ||
            value.Any(c => c is '\r' or '\n' or '\0')) throw new ArgumentException("Invalid property text.");
        var result = lines.ToBuilder();
        var found = false;
        for (var i = 0; i < result.Count; i++)
            if (Split(result[i]) is { } pair && pair.Key == key) { result[i] = key + "=" + value; found = true; }
        if (!found) result.Add(key + "=" + value);
        return new(result.ToImmutable(), newline, trailingNewline);
    }
    public ServerProperties SetPort(int port)
    {
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        return Set("server-port", port.ToString(CultureInfo.InvariantCulture));
    }
    public override string ToString() => string.Join(newline, lines) + (trailingNewline ? newline : "");
}
