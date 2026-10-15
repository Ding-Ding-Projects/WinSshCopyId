using System.Collections.Immutable;

namespace WinSshCopyId.Tools.Servers;

public sealed record ApprovedServerHost(string Host, int Port, string User, string HostKeySha256);
public sealed record ServerRemoteRequest(Guid RequestId, Guid InstanceId, ServerOperation Operation,
    ServerTargetKind TargetKind, string InstanceDirectory, ServerDistribution Distribution,
    int Port, int MemoryMiB, bool Adopted, bool EulaAccepted, string? OwnedSessionId);
public sealed record ServerRemoteReceipt(Guid RequestId, Guid InstanceId, string VerifiedHostKeySha256,
    string? OwnedSessionId, ServerObservedState State, bool Completed, string Detail,
    ImmutableArray<string> Evidence);

/// <summary>
/// Implemented by the application's approved SSH service and a versioned remote management agent.
/// The transport must pin the host key before authentication and transmit this record as data, not shell text.
/// </summary>
public interface IApprovedServerSshTransport
{
    Task<ServerRemoteReceipt> ExecuteAsync(ApprovedServerHost approvedHost, ServerRemoteRequest request,
        CancellationToken cancellationToken);
}

/// <summary>Typed remote-process and remote-container adapter. No passwords, shell commands or guessed process IDs.</summary>
public sealed class ServerSshAdapter(IApprovedServerSshTransport transport)
{
    private readonly Dictionary<Guid, string> sessions = [];
    private readonly SemaphoreSlim mutation = new(1, 1);
    public async Task<ServerRemoteReceipt> ExecuteAsync(ServerInstance instance, ServerOperation operation,
        CancellationToken token = default)
    {
        ServerLifecyclePlanner.ValidateInstance(instance);
        if (instance.Target.Kind is not (ServerTargetKind.SshProcess or ServerTargetKind.SshDocker))
            throw new ArgumentException("An SSH process or SSH Docker target is required.");
        if (string.IsNullOrWhiteSpace(instance.Target.ApprovedHostKeySha256)) throw new InvalidOperationException("Approve the exact host-key identity before connecting.");
        if (operation != ServerOperation.Discover && !instance.Adopted) throw new InvalidOperationException("Discovery is read-only until explicit adoption.");
        if (operation == ServerOperation.Start && !instance.EulaAccepted) throw new InvalidOperationException("Explicit distribution EULA consent is required.");
        await mutation.WaitAsync(token);
        try
        {
            sessions.TryGetValue(instance.Id, out var session);
            if (operation is ServerOperation.Stop or ServerOperation.Restart && session is null)
                throw new InvalidOperationException("A previously verified owned remote session is required.");
            var request = new ServerRemoteRequest(Guid.NewGuid(), instance.Id, operation, instance.Target.Kind,
                instance.Target.WorkingDirectory, instance.Distribution, instance.Port, instance.MemoryMiB,
                instance.Adopted, instance.EulaAccepted, session);
            var host = new ApprovedServerHost(instance.Target.Host!, instance.Target.SshPort, instance.Target.User!, instance.Target.ApprovedHostKeySha256);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromMinutes(5));
            var receipt = await transport.ExecuteAsync(host, request, deadline.Token);
            if (receipt.RequestId != request.RequestId || receipt.InstanceId != instance.Id ||
                receipt.VerifiedHostKeySha256 != host.HostKeySha256 || receipt.Evidence.IsDefault)
                throw new InvalidDataException("Remote response is not bound to the approved host, request and instance.");
            if (operation == ServerOperation.Discover && receipt.OwnedSessionId is not null)
                throw new InvalidDataException("Read-only discovery cannot adopt or create a remote session.");
            if (receipt.Completed && operation == ServerOperation.Start)
            {
                if (string.IsNullOrWhiteSpace(receipt.OwnedSessionId)) throw new InvalidDataException("Remote start omitted its owned session receipt.");
                sessions[instance.Id] = receipt.OwnedSessionId;
            }
            if (session is not null && receipt.OwnedSessionId is not null && session != receipt.OwnedSessionId)
                throw new InvalidDataException("Remote owned session identity changed unexpectedly.");
            return receipt;
        }
        finally { mutation.Release(); }
    }
}
