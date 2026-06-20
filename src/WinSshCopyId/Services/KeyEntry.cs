using System.IO;

namespace WinSshCopyId.Services;

/// <summary>
/// A usable SSH identity: a public key (what we deploy) plus, when known,
/// the matching private key file (what "ssh" will authenticate with).
/// </summary>
public sealed class KeyEntry
{
    public required string PublicKeyPath { get; init; }
    public required string PublicKeyLine { get; init; }

    /// <summary>Path to the private key, or null if only the .pub was found/imported.</summary>
    public string? PrivateKeyPath { get; init; }

    public string FileName => Path.GetFileName(PublicKeyPath);

    public string KeyType
    {
        get
        {
            int space = PublicKeyLine.IndexOf(' ');
            return space > 0 ? PublicKeyLine[..space] : "ssh-key";
        }
    }

    /// <summary>Short label for the dropdown, e.g. "id_ed25519.pub  (ssh-ed25519)".</summary>
    public string Display => $"{FileName}  ({KeyType})";

    public override string ToString() => Display;
}
