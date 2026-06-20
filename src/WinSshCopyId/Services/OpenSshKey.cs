using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace WinSshCopyId.Services;

/// <summary>
/// Pure-managed helpers for producing OpenSSH-format key material.
/// Used as a fallback when the OpenSSH "ssh-keygen" tool is not installed.
/// </summary>
public static class OpenSshKey
{
    /// <summary>
    /// Generate a fresh RSA key pair.
    /// Returns the PKCS#1 PEM private key and the single-line OpenSSH public key.
    /// </summary>
    public static (string PrivatePem, string PublicLine) GenerateRsa(int bits, string comment)
    {
        using var rsa = RSA.Create(bits);
        RSAParameters p = rsa.ExportParameters(true);
        string publicLine = FormatRsaPublicKey(p, comment);
        string privatePem = rsa.ExportRSAPrivateKeyPem();
        if (!privatePem.EndsWith('\n'))
        {
            privatePem += "\n";
        }
        return (privatePem, publicLine);
    }

    /// <summary>
    /// Build the OpenSSH "ssh-rsa AAAA... comment" public key line from RSA parameters.
    /// </summary>
    public static string FormatRsaPublicKey(RSAParameters p, string comment)
    {
        using var ms = new MemoryStream();
        WriteLengthPrefixed(ms, Encoding.ASCII.GetBytes("ssh-rsa"));
        WriteMpint(ms, p.Exponent!);
        WriteMpint(ms, p.Modulus!);
        string b64 = System.Convert.ToBase64String(ms.ToArray());
        return string.IsNullOrWhiteSpace(comment) ? $"ssh-rsa {b64}" : $"ssh-rsa {b64} {comment}";
    }

    private static void WriteLengthPrefixed(Stream s, byte[] data)
    {
        WriteUInt32(s, (uint)data.Length);
        s.Write(data, 0, data.Length);
    }

    // SSH "mpint" (RFC 4251): big-endian, two's-complement. Strip leading zero
    // bytes; the value zero is a zero-length string. For a positive number whose
    // top byte has the high bit set, a leading 0x00 must be added.
    private static void WriteMpint(Stream s, byte[] value)
    {
        int start = 0;
        while (start < value.Length && value[start] == 0)
        {
            start++;
        }
        int len = value.Length - start;
        if (len == 0)
        {
            WriteUInt32(s, 0); // value is zero (or empty)
            return;
        }
        bool needsPad = (value[start] & 0x80) != 0;
        WriteUInt32(s, (uint)(len + (needsPad ? 1 : 0)));
        if (needsPad)
        {
            s.WriteByte(0x00);
        }
        s.Write(value, start, len);
    }

    private static void WriteUInt32(Stream s, uint v)
    {
        s.WriteByte((byte)(v >> 24));
        s.WriteByte((byte)(v >> 16));
        s.WriteByte((byte)(v >> 8));
        s.WriteByte((byte)v);
    }
}
