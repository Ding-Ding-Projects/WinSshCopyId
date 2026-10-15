using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace WinSshCopyId.Services;

public sealed record DeploymentRequest(string Host, int Port, string Username, string PublicKey,
    string PrivateKeyPath, bool Verify = true);
public enum DeploymentState { Installed, AlreadyPresent, Failed, Cancelled, NotStarted, Skipped }
public enum InstallationState { NotStarted, Installed, AlreadyPresent, Failed, Uncertain }
public enum VerificationState { NotRequested, NotStarted, Succeeded, Failed, Cancelled }
public enum DeploymentRetryAction { None, Deploy, VerifyOnly, Reconcile }
public sealed record DeploymentOutcome(DeploymentRequest Request, DeploymentState State, bool Verified,
    string? Fingerprint, string? Error)
{
    public InstallationState Installation { get; init; } = InstallationState.NotStarted;
    public VerificationState Verification { get; init; } = VerificationState.NotStarted;
    public bool CancellationRequested { get; init; }
    public DeploymentRetryAction RetryAction { get; init; }
    public HostKeyChangedException? HostKeyChange { get; init; }
    public bool IsRetryableFailure => RetryAction is DeploymentRetryAction.Deploy or DeploymentRetryAction.VerifyOnly;
}
/// <summary>A transport may use this only when it knows installation never reached the server.</summary>
public sealed class DeploymentNotStartedException(string message) : Exception(message);
public sealed record DeploymentProgress(int Completed, int Total, string Host, string Stage);

public interface ISshDeploymentTransport
{
    Task<DeployResult> InstallAsync(DeploymentRequest request, string password, CancellationToken cancellationToken);
    Task<bool> VerifyAsync(DeploymentRequest request, string fingerprint, Func<string?>? passphrase, CancellationToken cancellationToken);
}

public sealed class SshDeploymentTransport(SshDeployer deployer) : ISshDeploymentTransport
{
    public Task<DeployResult> InstallAsync(DeploymentRequest request, string password, CancellationToken cancellationToken) =>
        Task.Run(() => deployer.InstallKey(request.Host, request.Port, request.Username, password,
            request.PublicKey, _ => { }, cancellationToken), cancellationToken);
    public Task<bool> VerifyAsync(DeploymentRequest request, string fingerprint, Func<string?>? passphrase, CancellationToken cancellationToken) =>
        Task.Run(() => deployer.VerifyKeyLogin(request.Host, request.Port, request.Username, request.PrivateKeyPath,
            fingerprint, _ => { }, passphrase, cancellationToken), cancellationToken);
}

/// <summary>Runs one bounded, credential-scoped batch at a time. No credential is retained by this service.</summary>
public sealed class SequentialDeploymentService(ISshDeploymentTransport transport)
{
    private int _running;
    public async Task<IReadOnlyList<DeploymentOutcome>> DeployAsync(IEnumerable<DeploymentRequest> requests,
        Func<CancellationToken, Task<string?>> passwordProvider, Func<string?>? passphrase = null,
        IProgress<DeploymentProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _running, 1) != 0) throw new InvalidOperationException("A deployment batch is already active.");
        try
        {
            var batch = requests.Take(101).ToArray();
            if (batch.Length is 0 or > 100) throw new ArgumentException("A batch requires between 1 and 100 hosts.", nameof(requests));
            foreach (var request in batch)
            {
                SshInput.ValidateEndpoint(request.Host, request.Port); SshInput.ValidateUser(request.Username);
                SshInput.ValidatePublicKey(request.PublicKey);
                if (request.Verify && string.IsNullOrWhiteSpace(request.PrivateKeyPath)) throw new ArgumentException("Verification requires an identity path.");
            }
            var results = new List<DeploymentOutcome>();
            string? password = null;
            bool cancelled = cancellationToken.IsCancellationRequested;
            try
            {
                if (!cancelled)
                {
                    try { password = await passwordProvider(cancellationToken).ConfigureAwait(false); }
                    catch (OperationCanceledException) { cancelled = true; }
                    if (password is null) cancelled = true;
                }
                foreach (var request in batch)
                {
                    if (cancelled || cancellationToken.IsCancellationRequested)
                    {
                        cancelled = true;
                        results.Add(new(request, DeploymentState.NotStarted, false, null, "Not started because the operation was cancelled.")
                        { Installation = InstallationState.NotStarted, Verification = request.Verify ? VerificationState.NotStarted : VerificationState.NotRequested,
                            CancellationRequested = true });
                        continue;
                    }
                    progress?.Report(new(results.Count, batch.Length, request.Host, "Connecting"));
                    DeployResult? installed = null;
                    bool verified = false;
                    try
                    {
                        installed = await transport.InstallAsync(request, password!, cancellationToken).ConfigureAwait(false);
                        cancellationToken.ThrowIfCancellationRequested();
                        if (request.Verify)
                        {
                            if (string.IsNullOrWhiteSpace(installed.ServerFingerprint)) throw new InvalidOperationException("The server did not supply a host fingerprint.");
                            verified = await transport.VerifyAsync(request, installed.ServerFingerprint, passphrase, cancellationToken).ConfigureAwait(false);
                            cancellationToken.ThrowIfCancellationRequested();
                            if (!verified) throw new InvalidOperationException("Key login verification failed.");
                        }
                        results.Add(Outcome(request, installed, verified, false, request.Verify ? VerificationState.Succeeded : VerificationState.NotRequested, null));
                    }
                    catch (Exception ex)
                    {
                        cancelled = ex is OperationCanceledException || cancellationToken.IsCancellationRequested;
                        if (installed is not null)
                        {
                            results.Add(Outcome(request, installed, verified, cancelled,
                                verified ? VerificationState.Succeeded : request.Verify ? (cancelled ? VerificationState.Cancelled : VerificationState.Failed) : VerificationState.NotRequested,
                                verified ? null : cancelled ? "Installation completed; verification was cancelled or not started. Server changes remain in place."
                                    : "Installation completed, but key login verification failed. Retry verification before installing again."));
                        }
                        else
                        {
                            bool knownNotInstalled = ex is DeploymentNotStartedException or HostKeyChangedException;
                            results.Add(new(request, cancelled ? DeploymentState.Cancelled : DeploymentState.Failed, false, null,
                                ex is HostKeyChangedException ? ex.Message : knownNotInstalled ? "Installation did not start. Check the endpoint, authentication and trusted host key."
                                    : "Installation outcome is uncertain. Inspect the server before retrying; cancellation does not undo server changes.")
                            {
                                Installation = knownNotInstalled ? InstallationState.Failed : InstallationState.Uncertain,
                                Verification = request.Verify ? VerificationState.NotStarted : VerificationState.NotRequested,
                                CancellationRequested = cancelled, HostKeyChange = ex as HostKeyChangedException,
                                RetryAction = knownNotInstalled && !cancelled && ex is not HostKeyChangedException ? DeploymentRetryAction.Deploy : DeploymentRetryAction.Reconcile,
                            });
                        }
                    }
                    progress?.Report(new(results.Count, batch.Length, request.Host, "Finished"));
                }
            }
            finally { password = null; }
            return results.AsReadOnly();
        }
        finally { Volatile.Write(ref _running, 0); }
    }
    private static DeploymentOutcome Outcome(DeploymentRequest request, DeployResult installed, bool verified,
        bool cancelled, VerificationState verification, string? error) =>
        new(request, installed.KeyWasAlreadyPresent ? DeploymentState.AlreadyPresent : DeploymentState.Installed,
            verified, installed.ServerFingerprint, error)
        {
            Installation = installed.KeyWasAlreadyPresent ? InstallationState.AlreadyPresent : InstallationState.Installed,
            Verification = verification, CancellationRequested = cancelled,
            RetryAction = verification is VerificationState.Failed or VerificationState.Cancelled ? DeploymentRetryAction.VerifyOnly : DeploymentRetryAction.None,
        };

}
