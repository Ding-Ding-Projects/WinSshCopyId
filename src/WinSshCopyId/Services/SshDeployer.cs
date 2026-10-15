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
    private readonly HostTrustStore _trust;
    private readonly Func<HostKeyApproval, bool>? _approve;
    public SshDeployer(HostTrustStore? trust = null, Func<HostKeyApproval, bool>? approve = null)
    {
        _trust = trust ?? new HostTrustStore();
        _approve = approve;
    }

    /// <summary>
    /// Connect with a password (or keyboard-interactive) and install the key.
    /// Runs synchronously; call it from a background task.
    /// </summary>
    public DeployResult InstallKey(
        string host, int port, string username, string password,
        string publicKeyLine, Action<string> log, System.Threading.CancellationToken cancellationToken = default)
    {
        SshInput.ValidateEndpoint(host, port);
        SshInput.ValidateUser(username);
        publicKeyLine = SshInput.ValidatePublicKey(publicKeyLine);
        ConnectionInfo connInfo = BuildPasswordConnection(host, port, username, password);
        string? fingerprintRaw = null;

        cancellationToken.ThrowIfCancellationRequested();
        using var client = new SshClient(connInfo);
        using var cancellation = cancellationToken.Register(() => client.Dispose());
        client.HostKeyReceived += (_, e) =>
        {
            fingerprintRaw = e.FingerPrintSHA256;
            e.CanTrust = _trust.Check(host, port, fingerprintRaw, _approve);
        };

        log($"Connecting to {username}@{host}:{port} ...");
        client.Connect();
        log("Authenticated with password.");
        if (fingerprintRaw is not null)
        {
            log($"Server host key SHA256:{fingerprintRaw}");
        }

        string script = BuildInstallScript(publicKeyLine);
        using SshCommand cmd = client.CreateCommand(script);
        cmd.CommandTimeout = _timeout;
        cmd.Execute();

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
        string? expectedFingerprint, Action<string> log, Func<string?>? passphrase = null, System.Threading.CancellationToken cancellationToken = default)
    {
        log("Verifying key-based login ...");
        SshInput.ValidateEndpoint(host, port);
        SshInput.ValidateUser(username);
        using var keyFile = OpenPrivateKey(privateKeyPath, passphrase);
        var auth = new PrivateKeyAuthenticationMethod(username, keyFile);
        var connInfo = new ConnectionInfo(host, port, username, auth) { Timeout = _timeout };

        cancellationToken.ThrowIfCancellationRequested();
        using var client = new SshClient(connInfo);
        using var cancellation = cancellationToken.Register(() => client.Dispose());
        client.HostKeyReceived += (_, e) =>
        {
            if (expectedFingerprint is null)
            {
                e.CanTrust = _trust.Check(host, port, e.FingerPrintSHA256, _approve);
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
        using SshCommand cmd = client.CreateCommand("echo WINSSHCOPYID_OK");
        cmd.CommandTimeout = _timeout;
        cmd.Execute();
        client.Disconnect();

        bool ok = cmd.ExitStatus == 0 && string.Equals((cmd.Result ?? string.Empty).Trim(), "WINSSHCOPYID_OK", StringComparison.Ordinal);
        log(ok ? "Key login verified." : "Key login did NOT succeed.");
        return ok;
    }

    private static PrivateKeyFile OpenPrivateKey(string path, Func<string?>? passphrase)
    {
        try { return new PrivateKeyFile(path); }
        catch (SshPassPhraseNullOrEmptyException)
        {
            string? value = passphrase?.Invoke();
            if (string.IsNullOrEmpty(value)) throw new OperationCanceledException("Private key passphrase was not provided.");
            return new PrivateKeyFile(path, value);
        }
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
        string raw = SshInput.ValidatePublicKey(publicKeyLine);
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
