using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace WinSshCopyId.Workspace.Security;

public enum OtpAlgorithm { Sha1, Sha256, Sha512 }
public static class Totp
{
    public static byte[] DecodeBase32(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var text = value.Replace(" ", "").ToUpperInvariant();
        var unpadded = text.TrimEnd('=');
        if (unpadded.Length == 0 || unpadded.Length > 4096 || unpadded.Contains('=') || (text.Length != unpadded.Length && text.Length % 8 != 0) || unpadded.Length % 8 is 1 or 3 or 6) throw new FormatException("Invalid base32.");
        var result = new List<byte>(); int bits = 0, buffer = 0;
        foreach (var c in unpadded) { int n = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567".IndexOf(c); if (n < 0) throw new FormatException("Invalid base32."); buffer = (buffer << 5) | n; bits += 5; if(bits >= 8) { bits -= 8; result.Add((byte)(buffer >> bits)); buffer &= (1 << bits)-1; } }
        if(buffer != 0 || (text.Length != unpadded.Length && text.Length - unpadded.Length != (8-unpadded.Length%8)%8)) throw new FormatException("Invalid base32 padding.");
        return result.ToArray();
    }
    public static string Hotp(byte[] secret, long counter, OtpAlgorithm algorithm = OtpAlgorithm.Sha1, int digits = 6)
    {
        if(secret.Length == 0 || counter < 0 || digits is < 6 or > 8) throw new ArgumentOutOfRangeException();
        Span<byte> moving = stackalloc byte[8]; BinaryPrimitives.WriteInt64BigEndian(moving, counter);
        byte[] hash = algorithm switch { OtpAlgorithm.Sha1 => HMACSHA1.HashData(secret,moving), OtpAlgorithm.Sha256 => HMACSHA256.HashData(secret,moving), OtpAlgorithm.Sha512 => HMACSHA512.HashData(secret,moving), _ => throw new ArgumentOutOfRangeException(nameof(algorithm)) };
        int offset=hash[^1]&15; int binary=BinaryPrimitives.ReadInt32BigEndian(hash.AsSpan(offset,4))&0x7fffffff;
        return (binary % (digits == 6 ? 1000000 : digits == 7 ? 10000000 : 100000000)).ToString(new string('0',digits),System.Globalization.CultureInfo.InvariantCulture);
    }
    public static string Code(byte[] secret, DateTimeOffset now, OtpAlgorithm algorithm = OtpAlgorithm.Sha1, int digits = 6, int period = 30) { if(period <= 0) throw new ArgumentOutOfRangeException(nameof(period)); return Hotp(secret,now.ToUnixTimeSeconds()/period,algorithm,digits); }
    public static bool Verify(byte[] secret,string code,DateTimeOffset now,OtpAlgorithm algorithm=OtpAlgorithm.Sha1,int digits=6,int period=30,int tolerance=1)
    {
        if(tolerance is < 0 or > 2 || period <= 0) throw new ArgumentOutOfRangeException();
        if(code.Length != digits || code.Any(c=>c<'0'||c>'9')) return false;
        bool match=false; for(int i=-tolerance;i<=tolerance;i++) { var at=now.AddSeconds((long)i*period); if(at.ToUnixTimeSeconds()>=0) match |= CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Code(secret,at,algorithm,digits,period)),Encoding.ASCII.GetBytes(code)); } return match;
    }
}

public sealed record VaultDelegates(Func<string,string?> Get, Action<string,string> Set, Action<string> Delete);
public sealed record AuthenticatorEntry(string Id,string Issuer,string Account,string SecretReference,OtpAlgorithm Algorithm,int Digits,int Period);
public sealed class AuthenticatorStore(VaultDelegates vault)
{
    public AuthenticatorEntry Register(string issuer,string account,string base32,string confirmation,DateTimeOffset now,OtpAlgorithm algorithm=OtpAlgorithm.Sha1,int digits=6,int period=30)
    {
        var secret=Totp.DecodeBase32(base32); try { if(!Totp.Verify(secret,confirmation,now,algorithm,digits,period)) throw new ArgumentException("Pairing confirmation did not match."); var id=Guid.NewGuid().ToString("N"); var reference="authenticator/"+id; vault.Set(reference,Convert.ToBase64String(secret)); return new(id,issuer,account,reference,algorithm,digits,period); } finally { CryptographicOperations.ZeroMemory(secret); }
    }
    public string CurrentCode(AuthenticatorEntry entry,DateTimeOffset now) { var secret=Convert.FromBase64String(vault.Get(entry.SecretReference)??throw new InvalidOperationException("Credential unavailable.")); try{return Totp.Code(secret,now,entry.Algorithm,entry.Digits,entry.Period);}finally{CryptographicOperations.ZeroMemory(secret);} }
    public void Remove(AuthenticatorEntry entry)=>vault.Delete(entry.SecretReference);
}
