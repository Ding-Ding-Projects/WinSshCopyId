using System;
using System.Text;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace WinSshCopyId.Services;

public sealed class DeployResult
{
    public bool KeyWasAlreadyPresent { get; init; }
    public string? ServerFingerprint { get; init; }
}

/// <summary>
/// The actual "ssh-copy-id" operation: log in with a password and append the
/// public key to the remote ~/.ssh/authorized_keys, then verify that key-based
/// login works.
/// </summary>
public sealed class SshDeployer
{
    private readonly TimeSpan _timeout = TimeSpan.FromSeconds(25);

    /// <summary>
    /// Connect with a password (or keyboard-interactive) and install the key.
    /// Runs synchronously; call it from a background task.
    /// </summary>
    public DeployResult InstallKey(
        string host, int port, string username, string password,
        string publicKeyLine, Action<string> log)
    {
        ConnectionInfo connInfo = BuildPasswordConnection(host, port, username, password);
        string? fingerprintRaw = null;

        using var client = new SshClient(connInfo);
        client.HostKeyReceived += (_, e) =>
        {
            // Trust-on-first-use, like ssh-copy-id. The fingerprint is captured
            // so the verify step can pin to the same host key and so the user
            // can eyeball it in the log.
            e.CanTrust = true;
            try
            {
                fingerprintRaw = e.FingerPrintSHA256;
            }
            catch
            {
                fingerprintRaw = null;
            }
        };

        log($"Connecting to {username}@{host}:{port} ...");
        client.Connect();
        log("Authenticated with password.");
        if (fingerprintRaw is not null)
        {
            log($"Server host key SHA256:{fingerprintRaw}");
        }

        string script = BuildInstallScript(publicKeyLine);
        SshCommand cmd = client.RunCommand(script);

        string stdout = (cmd.Result ?? string.Empty).Trim();
        string stderr = (cmd.Error ?? string.Empty).Trim();
        if (stderr.Length > 0)
        {
            log($"remote: {stderr}");
        }

        client.Disconnect();

        if (cmd.ExitStatus != 0)
        {
            throw new InvalidOperationException(
                $"Remote command failed (exit {cmd.ExitStatus}). {stderr}");
        }

        bool already = stdout.Contains("ALREADY_PRESENT", StringComparison.Ordinal);
        log(already
            ? "Key was already present in authorized_keys."
            : "Key appended to ~/.ssh/authorized_keys.");

        return new DeployResult
        {
            KeyWasAlreadyPresent = already,
            ServerFingerprint = fingerprintRaw,
        };
    }

    /// <summary>
    /// Confirm the freshly installed key actually logs in without a password.
    /// When <paramref name="expectedFingerprint"/> is supplied, the connection is
    /// pinned to that host key (SHA256) so verification cannot silently succeed
    /// against a different/MITM server than the one the key was installed on.
    /// </summary>
    public bool VerifyKeyLogin(
        string host, int port, string username, string privateKeyPath,
        string? expectedFingerprint, Action<string> log)
    {
        log("Verifying key-based login ...");
        using var keyFile = new PrivateKeyFile(privateKeyPath);
        var auth = new PrivateKeyAuthenticationMethod(username, keyFile);
        var connInfo = new ConnectionInfo(host, port, username, auth) { Timeout = _timeout };

        using var client = new SshClient(connInfo);
        client.HostKeyReceived += (_, e) =>
        {
            if (expectedFingerprint is null)
            {
                e.CanTrust = true; // standalone test: trust-on-first-use
            }
            else if (string.Equals(e.FingerPrintSHA256, expectedFingerprint, StringComparison.Ordinal))
            {
                e.CanTrust = true;
            }
            else
            {
                e.CanTrust = false;
                log($"Host key mismatch! expected SHA256:{expectedFingerprint} but got SHA256:{e.FingerPrintSHA256}");
            }
        };
        client.Connect();
        SshCommand cmd = client.RunCommand("echo WINSSHCOPYID_OK");
        client.Disconnect();

        bool ok = (cmd.Result ?? string.Empty).Contains("WINSSHCOPYID_OK", StringComparison.Ordinal);
        log(ok ? "Key login verified." : "Key login did NOT succeed.");
        return ok;
    }

    private ConnectionInfo BuildPasswordConnection(string host, int port, string username, string password)
    {
        var passwordAuth = new PasswordAuthenticationMethod(username, password);

        // Many sshd configs offer "keyboard-interactive" rather than plain
        // "password"; answer those prompts with the same password.
        var keyboardAuth = new KeyboardInteractiveAuthenticationMethod(username);
        keyboardAuth.AuthenticationPrompt += (_, e) =>
        {
            foreach (AuthenticationPrompt prompt in e.Prompts)
            {
                prompt.Response = password;
            }
        };

        return new ConnectionInfo(host, port, username, passwordAuth, keyboardAuth)
        {
            Timeout = _timeout,
        };
    }

    // Idempotent installer. The key is embedded as a POSIX single-quoted literal.
    // A public key comment is free-form text and CAN contain a single quote, so we
    // escape every ' as '\'' (close-quote, escaped quote, reopen-quote). This is a
    // security boundary: without it a crafted .pub comment could break out of the
    // quoting and inject shell commands on the remote host.
    private static string BuildInstallScript(string publicKeyLine)
    {
        string raw = publicKeyLine.Replace("\r", string.Empty).Replace("\n", string.Empty).Trim();
        string key = raw.Replace("'", "'\\''");
        var sb = new StringBuilder();
        sb.Append("set -e; ");
        sb.Append("umask 077; ");
        sb.Append("mkdir -p ~/.ssh; ");
        sb.Append("chmod 700 ~/.ssh; ");
        sb.Append("touch ~/.ssh/authorized_keys; ");
        sb.Append("chmod 600 ~/.ssh/authorized_keys; ");
        sb.Append($"KEY='{key}'; ");
        sb.Append("if grep -qxF \"$KEY\" ~/.ssh/authorized_keys; then ");
        sb.Append("echo ALREADY_PRESENT; ");
        sb.Append("else ");
        sb.Append("printf '%s\\n' \"$KEY\" >> ~/.ssh/authorized_keys; ");
        sb.Append("echo ADDED; ");
        sb.Append("fi");
        return sb.ToString();
    }
}
