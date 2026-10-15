using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace WinSshCopyId.Services;

public sealed record DeploymentRequest(string Host, int Port, string Username, string PublicKey,
    string PrivateKeyPath, bool Verify = true);
public enum DeploymentState { Installed, AlreadyPresent, Failed, Cancelled }
public sealed record DeploymentOutcome(DeploymentRequest Request, DeploymentState State, bool Verified,
    string? Fingerprint, string? Error);
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
            cancellationToken.ThrowIfCancellationRequested();
            string? password = await passwordProvider(cancellationToken).ConfigureAwait(false);
            if (password is null) throw new OperationCanceledException("Authentication was cancelled.", cancellationToken);
            var results = new List<DeploymentOutcome>();
            try
            {
                foreach (var request in batch)
                {
                    if (cancellationToken.IsCancellationRequested) break;
                    progress?.Report(new(results.Count, batch.Length, request.Host, "Connecting"));
                    try
                    {
                        var installed = await transport.InstallAsync(request, password, cancellationToken).ConfigureAwait(false);
                        cancellationToken.ThrowIfCancellationRequested();
                        bool verified = false;
                        if (request.Verify)
                        {
                            if (string.IsNullOrWhiteSpace(installed.ServerFingerprint)) throw new InvalidOperationException("The server did not supply a host fingerprint.");
                            verified = await transport.VerifyAsync(request, installed.ServerFingerprint, passphrase, cancellationToken).ConfigureAwait(false);
                            if (!verified) throw new InvalidOperationException("Key login verification failed.");
                        }
                        results.Add(new(request, installed.KeyWasAlreadyPresent ? DeploymentState.AlreadyPresent : DeploymentState.Installed,
                            verified, installed.ServerFingerprint, null));
                    }
                    catch (OperationCanceledException) { results.Add(new(request, DeploymentState.Cancelled, false, null, null)); break; }
                    catch (Exception) when (cancellationToken.IsCancellationRequested) { results.Add(new(request, DeploymentState.Cancelled, false, null, null)); break; }
                    catch (Exception) { results.Add(new(request, DeploymentState.Failed, false, null, "Deployment failed. Check the endpoint, authentication and trusted host key.")); }
                    progress?.Report(new(results.Count, batch.Length, request.Host, "Finished"));
                }
            }
            finally { password = null; }
            return results.AsReadOnly();
        }
        finally { Volatile.Write(ref _running, 0); }
    }
}
