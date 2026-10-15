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
    public IReadOnlyList<string> GetSupportedAlgorithms() => FindSshKeygen().Length > 0
        ? new[] { "ed25519", "rsa" } : new[] { "rsa" };

    public KeyEntry GenerateKey(string comment, Action<string> log)
    {
        string algorithm = GetSupportedAlgorithms()[0];
        for (int i = 0; i < 1000; i++)
        {
            string name = "id_" + algorithm + (i == 0 ? "" : "_winsshcopyid_" + i);
            string target = Path.Combine(SshDir, name);
            if (!File.Exists(target) && !File.Exists(target + ".pub"))
                return GenerateKey(target, algorithm, comment, log);
        }
        throw new IOException("No free identity name is available.");
    }

    /// <summary>Stages and verifies a pair before no-overwrite publication. A failed publication rolls back owned files.</summary>
    public KeyEntry GenerateKey(string destinationBasePath, string algorithm, string comment, Action<string> log,
        System.Threading.CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationBasePath);
        if (comment.Any(char.IsControl)) throw new ArgumentException("Key comments cannot contain control characters.", nameof(comment));
        if (!GetSupportedAlgorithms().Contains(algorithm, StringComparer.Ordinal)) throw new ArgumentException("Unsupported key generation algorithm.", nameof(algorithm));
        string target = Path.GetFullPath(destinationBasePath);
        if (target.EndsWith(".pub", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Choose an identity basename, without .pub.");
        string parent = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(parent);
        if (File.Exists(target) || File.Exists(target + ".pub") || Directory.Exists(target) || Directory.Exists(target + ".pub"))
            throw new IOException("An identity already exists at this destination.");
        cancellationToken.ThrowIfCancellationRequested();
        string staging = Path.Combine(parent, ".keygen-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        string stagedKey = Path.Combine(staging, "identity");
        bool publishedPrivate = false;
        bool publishedPublic = false;
        byte[]? privateHash = null;
        byte[]? publicHash = null;
        try
        {
            RestrictAndVerifyDirectory(staging);
            if (algorithm == "ed25519") GenerateWithSshKeygen(FindSshKeygen(), stagedKey, comment, cancellationToken);
            else
            {
                var pair = OpenSshKey.GenerateRsa(3072, comment);
                cancellationToken.ThrowIfCancellationRequested();
                File.WriteAllText(stagedKey, pair.PrivatePem, new UTF8Encoding(false));
                File.WriteAllText(stagedKey + ".pub", pair.PublicLine + "\n", new UTF8Encoding(false));
            }
            RestrictAndVerifyPrivateKey(stagedKey);
            string publicLine = SshInput.ValidatePublicKey(File.ReadAllText(stagedKey + ".pub").Trim());
            using (var privateFile = new Renci.SshNet.PrivateKeyFile(stagedKey))
            {
                if (!privateFile.HostKeyAlgorithms.First().Data.AsSpan().SequenceEqual(Convert.FromBase64String(publicLine.Split(' ')[1])))
                    throw new InvalidDataException("Generated public and private keys do not match.");
            }
            privateHash = System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(stagedKey));
            publicHash = System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(stagedKey + ".pub"));
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(stagedKey, target, false);
            publishedPrivate = true;
            File.Move(stagedKey + ".pub", target + ".pub", false);
            publishedPublic = true;
            VerifyPrivateKeyPermissions(target);
            log("Identity pair generated and private-key permissions verified.");
            return new KeyEntry { PrivateKeyPath = target, PublicKeyPath = target + ".pub", PublicKeyLine = publicLine };
        }
        catch
        {
            DeleteOwned(target + ".pub", publishedPublic, publicHash);
            DeleteOwned(target, publishedPrivate, privateHash);
            throw;
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
    }

    private static void DeleteOwned(string path, bool owned, byte[]? expected)
    {
        if (owned && expected is not null && File.Exists(path) &&
            System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)).AsSpan().SequenceEqual(expected)) File.Delete(path);
    }

    private static void GenerateWithSshKeygen(string keygen, string target, string comment, System.Threading.CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo(keygen) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (string arg in new[] { "-q", "-t", "ed25519", "-f", target, "-N", "", "-C", comment }) psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start ssh-keygen.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try
        {
            using var timeout = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var linked = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            process.WaitForExitAsync(linked.Token).GetAwaiter().GetResult();
            System.Threading.Tasks.Task.WhenAll(output, error).GetAwaiter().GetResult();
            if (process.ExitCode != 0) throw new InvalidOperationException("ssh-keygen did not generate an identity pair.");
        }
        catch { if (!process.HasExited) { process.Kill(true); process.WaitForExit(); } throw; }
    }

    private static void RestrictAndVerifyDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            var user = System.Security.Principal.WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("Current user identity is unavailable.");
            var security = new System.Security.AccessControl.DirectorySecurity();
            security.SetOwner(user); security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new(user, System.Security.AccessControl.FileSystemRights.FullControl,
                System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit,
                System.Security.AccessControl.PropagationFlags.None, System.Security.AccessControl.AccessControlType.Allow));
            System.IO.FileSystemAclExtensions.SetAccessControl(new DirectoryInfo(path), security);
        }
        else File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static void RestrictAndVerifyPrivateKey(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            var user = System.Security.Principal.WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("Current user identity is unavailable.");
            var current = System.IO.FileSystemAclExtensions.GetAccessControl(new FileInfo(path));
            if (!user.Equals(current.GetOwner(typeof(System.Security.Principal.SecurityIdentifier))))
                throw new IOException("Generated private key is not owned by the current user.");
            var security = new System.Security.AccessControl.FileSecurity();
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new(user, System.Security.AccessControl.FileSystemRights.FullControl, System.Security.AccessControl.AccessControlType.Allow));
            System.IO.FileSystemAclExtensions.SetAccessControl(new FileInfo(path), security);
        }
        else File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        VerifyPrivateKeyPermissions(path);
    }

    public static void VerifyPrivateKeyPermissions(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            var user = System.Security.Principal.WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("Current user identity is unavailable.");
            var security = System.IO.FileSystemAclExtensions.GetAccessControl(new FileInfo(path));
            if (!security.AreAccessRulesProtected || !user.Equals(security.GetOwner(typeof(System.Security.Principal.SecurityIdentifier))))
                throw new IOException("Private-key ownership or inheritance is unsafe.");
            foreach (System.Security.AccessControl.FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier)))
                if (rule.AccessControlType == System.Security.AccessControl.AccessControlType.Allow && !user.Equals(rule.IdentityReference))
                    throw new IOException("Private key is accessible to another identity.");
        }
        else if ((File.GetUnixFileMode(path) & ~(UnixFileMode.UserRead | UnixFileMode.UserWrite)) != 0)
            throw new IOException("Private key permissions are unsafe.");
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

    public static bool IsValidPublicKey(string line, out string error)
    {
        try { SshInput.ValidatePublicKey(line); error = string.Empty; return true; }
        catch (ArgumentException ex) { error = ex.Message; return false; }
    }
}

