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
        if (parts.Length < 2) throw new ArgumentException("Only OpenSSH public keys can be deployed.", nameof(publicKey));
        try
        {
            byte[] blob = Convert.FromBase64String(parts[1]);
            if (blob.Length > 16384) throw new FormatException();
            var reader = new PublicKeyReader(blob);
            if (System.Text.Encoding.ASCII.GetString(reader.Read()) != parts[0]) throw new FormatException();
            switch (parts[0])
            {
                case "ssh-ed25519":
                    if (reader.Read().Length != 32) throw new FormatException();
                    break;
                case "ssh-rsa":
                    byte[] exponent = reader.ReadPositiveInteger();
                    byte[] modulus = reader.ReadPositiveInteger();
                    if (exponent.Length > 8 || modulus.Length < 128 || modulus.Length > 1024 ||
                        (exponent[^1] & 1) == 0 || (exponent.Length == 1 && exponent[0] < 3)) throw new FormatException();
                    using (var rsa = System.Security.Cryptography.RSA.Create())
                        rsa.ImportParameters(new() { Exponent = exponent, Modulus = modulus });
                    break;
                case "ecdsa-sha2-nistp256":
                case "ecdsa-sha2-nistp384":
                case "ecdsa-sha2-nistp521":
                    string curve = System.Text.Encoding.ASCII.GetString(reader.Read());
                    if (parts[0] != "ecdsa-sha2-" + curve) throw new FormatException();
                    int coordinateSize = curve switch { "nistp256" => 32, "nistp384" => 48, "nistp521" => 66, _ => throw new FormatException() };
                    byte[] point = reader.Read();
                    if (point.Length != 1 + 2 * coordinateSize || point[0] != 4) throw new FormatException();
                    var ecCurve = curve switch
                    {
                        "nistp256" => System.Security.Cryptography.ECCurve.NamedCurves.nistP256,
                        "nistp384" => System.Security.Cryptography.ECCurve.NamedCurves.nistP384,
                        _ => System.Security.Cryptography.ECCurve.NamedCurves.nistP521,
                    };
                    using (var ec = System.Security.Cryptography.ECDsa.Create(new System.Security.Cryptography.ECParameters
                    {
                        Curve = ecCurve,
                        Q = new() { X = point[1..(1 + coordinateSize)], Y = point[(1 + coordinateSize)..] },
                    })) { }
                    break;
                default: throw new FormatException();
            }
            if (!reader.Finished) throw new FormatException();
        }
        catch (Exception ex) when (ex is FormatException or System.Security.Cryptography.CryptographicException or ArgumentException)
        { throw new ArgumentException("Invalid or unsupported public key encoding.", nameof(publicKey)); }
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
        string target = username + "@" + (host.Contains(':') ? "[" + host + "]" : host);
        return $"ssh -o IdentitiesOnly=yes -i {QuotePowerShell(identityPath)} -p {port} {QuotePowerShell(target)}";
    }
    private static string QuotePowerShell(string value) => "'" + value.Replace("'", "''") + "'";

    private sealed class PublicKeyReader(byte[] bytes)
    {
        private int _offset;
        public bool Finished => _offset == bytes.Length;
        public byte[] Read()
        {
            if (bytes.Length - _offset < 4) throw new FormatException();
            uint length = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(_offset, 4));
            _offset += 4;
            if (length > bytes.Length - _offset) throw new FormatException();
            byte[] value = bytes.AsSpan(_offset, (int)length).ToArray();
            _offset += (int)length;
            return value;
        }
        public byte[] ReadPositiveInteger()
        {
            byte[] value = Read();
            if (value.Length == 0 || (value[0] & 128) != 0) throw new FormatException();
            if (value[0] != 0) return value;
            if (value.Length == 1 || (value[1] & 128) == 0) throw new FormatException();
            return value[1..];
        }
    }

}
