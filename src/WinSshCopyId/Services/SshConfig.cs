using System;
using System.IO;
using System.Linq;
using System.Text;

namespace WinSshCopyId.Services;

/// <summary>
/// Optionally writes a Host entry into ~/.ssh/config so the user can connect
/// with a short alias (e.g. "ssh myserver") instead of the full command.
/// </summary>
public static class SshConfig
{
    public static string ConfigPath => Path.Combine(KeyManager.SshDir, "config");

    public static bool AliasExists(string alias)
    {
        if (!File.Exists(ConfigPath))
        {
            return false;
        }
        foreach (string raw in File.ReadAllLines(ConfigPath))
        {
            string line = raw.Trim();
            if (line.StartsWith("Host ", StringComparison.OrdinalIgnoreCase))
            {
                string[] hosts = line[5..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (hosts.Any(h => string.Equals(h, alias, StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>
    /// Append a Host stanza. Returns false (no-op) if the alias already exists.
    /// </summary>
    public static bool AddHostEntry(
        string alias, string hostName, string user, int port, string? identityFile)
    {
        Directory.CreateDirectory(KeyManager.SshDir);
        if (AliasExists(alias))
        {
            return false;
        }

        var sb = new StringBuilder();
        if (File.Exists(ConfigPath))
        {
            string existing = File.ReadAllText(ConfigPath);
            if (existing.Length > 0 && !existing.EndsWith('\n'))
            {
                sb.Append('\n');
            }
            sb.Append('\n');
        }

        sb.Append($"Host {alias}\n");
        sb.Append($"    HostName {hostName}\n");
        sb.Append($"    User {user}\n");
        if (port != 22)
        {
            sb.Append($"    Port {port}\n");
        }
        if (!string.IsNullOrWhiteSpace(identityFile))
        {
            sb.Append($"    IdentityFile {identityFile}\n");
        }

        File.AppendAllText(ConfigPath, sb.ToString(), new UTF8Encoding(false));
        return true;
    }
}
