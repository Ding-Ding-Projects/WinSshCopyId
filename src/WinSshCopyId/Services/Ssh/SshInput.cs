using System;
using System.Net;
using System.Text.RegularExpressions;

namespace WinSshCopyId.Services;

public static class SshInput
{
    public static void ValidateEndpoint(string host, int port)
    {
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        if (string.IsNullOrWhiteSpace(host) || host.Length > 253 ||
            !(IPAddress.TryParse(host, out _) || Regex.IsMatch(host, @"\A[A-Za-z0-9](?:[A-Za-z0-9.-]*[A-Za-z0-9])?\z")))
            throw new ArgumentException("Enter a hostname or IP address without SSH options.", nameof(host));
    }
    public static void ValidateUser(string username)
    {
        if (string.IsNullOrWhiteSpace(username) || username.Length > 128 ||
            !Regex.IsMatch(username, @"\A[A-Za-z0-9_][A-Za-z0-9_.@-]*[$]?\z"))
            throw new ArgumentException("Invalid SSH username.", nameof(username));
    }
    public static string ValidatePublicKey(string publicKey)
    {
        if (string.IsNullOrWhiteSpace(publicKey) || publicKey.Contains('\r') || publicKey.Contains('\n'))
            throw new ArgumentException("A single public key line is required.", nameof(publicKey));
        string[] parts = publicKey.Trim().Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !(parts[0].StartsWith("ssh-", StringComparison.Ordinal) ||
            parts[0].StartsWith("ecdsa-sha2-", StringComparison.Ordinal) || parts[0].StartsWith("sk-", StringComparison.Ordinal)))
            throw new ArgumentException("Only OpenSSH public keys can be deployed.", nameof(publicKey));
        try
        {
            byte[] blob = Convert.FromBase64String(parts[1]);
            if (blob.Length < 8) throw new FormatException();
            uint length = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(blob);
            if (length > blob.Length - 4 || length == 0 ||
                System.Text.Encoding.ASCII.GetString(blob, 4, (int)length) != parts[0]) throw new FormatException();
        }
        catch (FormatException) { throw new ArgumentException("Invalid public key encoding.", nameof(publicKey)); }
        return publicKey.Trim();
    }
    public static string QuoteConfig(string value)
    {
        if (value.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0) throw new ArgumentException("Invalid configuration value.");
        return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }
    public static string BuildConnectCommand(string host, int port, string username, string identityPath)
    {
        ValidateEndpoint(host, port); ValidateUser(username);
        if (string.IsNullOrWhiteSpace(identityPath) || identityPath.IndexOfAny(new[] { '"', '\r', '\n', '\0' }) >= 0)
            throw new ArgumentException("Invalid identity path.", nameof(identityPath));
        return $"ssh -o IdentitiesOnly=yes -i \"{identityPath}\" -p {port} {username}@{(host.Contains(':') ? "[" + host + "]" : host)}";
    }
}
