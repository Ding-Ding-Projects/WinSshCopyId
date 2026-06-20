using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace WinSshCopyId.Services;

/// <summary>
/// Finds existing SSH keys and creates new ones. Prefers the real OpenSSH
/// "ssh-keygen" (producing a modern ed25519 key); falls back to a managed
/// RSA generator when ssh-keygen is not on the machine.
/// </summary>
public sealed class KeyManager
{
    public static string SshDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");

    /// <summary>Scan ~/.ssh for *.pub files and return them as selectable identities.</summary>
    public IReadOnlyList<KeyEntry> FindExistingKeys()
    {
        var result = new List<KeyEntry>();
        if (!Directory.Exists(SshDir))
        {
            return result;
        }

        foreach (string pub in Directory.EnumerateFiles(SshDir, "*.pub"))
        {
            string line;
            try
            {
                line = File.ReadAllText(pub).Trim();
            }
            catch
            {
                continue;
            }
            if (line.Length == 0 || !IsValidPublicKey(line, out _))
            {
                continue;
            }

            string privateGuess = pub[..^4]; // drop ".pub"
            result.Add(new KeyEntry
            {
                PublicKeyPath = pub,
                PublicKeyLine = line,
                PrivateKeyPath = File.Exists(privateGuess) ? privateGuess : null,
            });
        }

        result.Sort((a, b) => string.CompareOrdinal(a.FileName, b.FileName));
        return result;
    }

    /// <summary>Import a public key the user pointed at with the file picker.</summary>
    public KeyEntry ImportPublicKey(string pubPath)
    {
        string line = File.ReadAllText(pubPath).Trim();
        if (!IsValidPublicKey(line, out string error))
        {
            throw new InvalidDataException($"That file is not a valid OpenSSH public key: {error}.");
        }

        string privateGuess = pubPath.EndsWith(".pub", StringComparison.OrdinalIgnoreCase)
            ? pubPath[..^4]
            : pubPath + ".key";

        return new KeyEntry
        {
            PublicKeyPath = pubPath,
            PublicKeyLine = line,
            PrivateKeyPath = File.Exists(privateGuess) ? privateGuess : null,
        };
    }

    /// <summary>
    /// Create a new key pair in ~/.ssh. The base file name is chosen so an
    /// existing key is never overwritten. Returns the new identity.
    /// </summary>
    public KeyEntry GenerateKey(string comment, Action<string> log)
    {
        Directory.CreateDirectory(SshDir);

        string keygen = FindSshKeygen();
        if (keygen.Length > 0)
        {
            string target = NextFreePath("id_ed25519");
            log($"Generating ed25519 key with ssh-keygen -> {target}");
            GenerateWithSshKeygen(keygen, target, comment, log);
            string pubPath = target + ".pub";
            return new KeyEntry
            {
                PublicKeyPath = pubPath,
                PublicKeyLine = File.ReadAllText(pubPath).Trim(),
                PrivateKeyPath = target,
            };
        }

        // Managed fallback: no OpenSSH tools available.
        string rsaTarget = NextFreePath("id_rsa");
        log($"ssh-keygen not found; generating RSA-3072 key in-process -> {rsaTarget}");
        (string privatePem, string publicLine) = OpenSshKey.GenerateRsa(3072, comment);
        File.WriteAllText(rsaTarget, privatePem, new UTF8Encoding(false));
        File.WriteAllText(rsaTarget + ".pub", publicLine + "\n", new UTF8Encoding(false));
        TryRestrictPermissions(rsaTarget);
        return new KeyEntry
        {
            PublicKeyPath = rsaTarget + ".pub",
            PublicKeyLine = publicLine,
            PrivateKeyPath = rsaTarget,
        };
    }

    private static void GenerateWithSshKeygen(string keygen, string target, string comment, Action<string> log)
    {
        var psi = new ProcessStartInfo
        {
            FileName = keygen,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-t");
        psi.ArgumentList.Add("ed25519");
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add(target);
        psi.ArgumentList.Add("-N");
        psi.ArgumentList.Add(string.Empty); // empty passphrase
        psi.ArgumentList.Add("-C");
        psi.ArgumentList.Add(comment);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start ssh-keygen.");
        string stdout = proc.StandardOutput.ReadToEnd();
        string stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();

        if (proc.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"ssh-keygen exited with code {proc.ExitCode}. {stderr.Trim()}");
        }
        if (stdout.Trim().Length > 0)
        {
            log(stdout.Trim());
        }
    }

    private static string NextFreePath(string baseName)
    {
        string candidate = Path.Combine(SshDir, baseName);
        if (!File.Exists(candidate) && !File.Exists(candidate + ".pub"))
        {
            return candidate;
        }

        candidate = Path.Combine(SshDir, baseName + "_winsshcopyid");
        if (!File.Exists(candidate) && !File.Exists(candidate + ".pub"))
        {
            return candidate;
        }

        for (int i = 1; i < 1000; i++)
        {
            string c = Path.Combine(SshDir, $"{baseName}_winsshcopyid_{i}");
            if (!File.Exists(c) && !File.Exists(c + ".pub"))
            {
                return c;
            }
        }
        throw new IOException("Could not find a free key file name in ~/.ssh.");
    }

    private static string FindSshKeygen()
    {
        string sys = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "OpenSSH", "ssh-keygen.exe");
        if (File.Exists(sys))
        {
            return sys;
        }

        string? path = Environment.GetEnvironmentVariable("PATH");
        if (path is not null)
        {
            foreach (string dir in path.Split(Path.PathSeparator))
            {
                if (dir.Length == 0)
                {
                    continue;
                }
                try
                {
                    string candidate = Path.Combine(dir.Trim(), "ssh-keygen.exe");
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
                catch
                {
                    // Ignore malformed PATH segments.
                }
            }
        }
        return string.Empty;
    }

    private static readonly HashSet<string> KnownKeyTypes = new(StringComparer.Ordinal)
    {
        "ssh-ed25519",
        "ssh-rsa",
        "ssh-dss",
        "ecdsa-sha2-nistp256",
        "ecdsa-sha2-nistp384",
        "ecdsa-sha2-nistp521",
        "sk-ssh-ed25519@openssh.com",
        "sk-ecdsa-sha2-nistp256@openssh.com",
    };

    /// <summary>
    /// Strict validation of an OpenSSH public key line ("type base64 [comment]").
    /// The type must be known, the body must be base64 whose embedded wire type
    /// matches the declared type, and the whole line must contain no control
    /// characters. This is a security gate: the line is later sent to a remote
    /// shell, so anything that does not parse cleanly is rejected up front.
    /// </summary>
    public static bool IsValidPublicKey(string line, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(line))
        {
            error = "the line is empty";
            return false;
        }
        foreach (char c in line)
        {
            if (char.IsControl(c))
            {
                error = "it contains control characters";
                return false;
            }
        }

        string[] parts = line.Split((char[]?)null, 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            error = "expected '<type> <base64> [comment]'";
            return false;
        }
        if (!KnownKeyTypes.Contains(parts[0]))
        {
            error = $"unknown key type '{parts[0]}'";
            return false;
        }

        byte[] blob;
        try
        {
            blob = Convert.FromBase64String(parts[1]);
        }
        catch
        {
            error = "the key body is not valid base64";
            return false;
        }
        if (ReadFirstSshString(blob) != parts[0])
        {
            error = "the key body does not match its declared type";
            return false;
        }
        return true;
    }

    // Read the first length-prefixed string from an SSH wire-format blob
    // (4-byte big-endian length, then that many ASCII bytes).
    private static string? ReadFirstSshString(byte[] blob)
    {
        if (blob.Length < 4)
        {
            return null;
        }
        long len = ((long)blob[0] << 24) | ((long)blob[1] << 16) | ((long)blob[2] << 8) | blob[3];
        if (len < 0 || 4 + len > blob.Length)
        {
            return null;
        }
        return Encoding.ASCII.GetString(blob, 4, (int)len);
    }

    // Best-effort lock-down of a managed-generated private key so OpenSSH does
    // not refuse it for being world-readable. ssh-keygen already does this itself.
    private static void TryRestrictPermissions(string path)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "icacls.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add(path);
            psi.ArgumentList.Add("/inheritance:r");
            psi.ArgumentList.Add("/grant:r");
            psi.ArgumentList.Add($"{Environment.UserName}:F");
            using var proc = Process.Start(psi);
            proc?.WaitForExit();
        }
        catch
        {
            // Non-fatal: the key still works, ssh may just warn about permissions.
        }
    }
}
