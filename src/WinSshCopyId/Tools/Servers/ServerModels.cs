using System.Collections.Immutable;

namespace WinSshCopyId.Tools.Servers;

public enum ServerEdition { Java, Bedrock }
public enum ServerTargetKind { LocalProcess, LocalDocker, SshProcess, SshDocker }
public enum ServerOperation { Discover, Start, Stop, Restart, Backup, Restore, Upgrade, Rollback }
public enum ServerObservedState { Unknown, Stopped, Running, Disconnected }
public sealed record ServerTarget(ServerTargetKind Kind, string WorkingDirectory, string? Host = null,
    int SshPort = 22, string? User = null, string? ApprovedHostKeySha256 = null);
public sealed record ServerDistribution(string Id, ServerEdition Edition, string Version, string Flavor,
    Uri Source, string? ExecutablePath, string? Sha256, string? ContainerImageDigest,
    string? RuntimePath, string? UnavailableReason);
public sealed record ServerInstance(Guid Id, string Name, ServerTarget Target, ServerDistribution Distribution,
    bool Adopted, bool EulaAccepted, int Port, int MemoryMiB, DateTimeOffset CreatedAt,
    ImmutableDictionary<string, string>? Extensions = null);
public sealed record ServerProcessIdentity(int ProcessId, DateTimeOffset StartedAt, string ExecutablePath,
    string ExecutableSha256, string? ContainerId = null);
public sealed record ServerObservation(Guid InstanceId, ServerObservedState State, DateTimeOffset ObservedAt,
    ServerProcessIdentity? Identity, string Detail);
public sealed record ServerCommand(string Executable, ImmutableArray<string> Arguments, string WorkingDirectory,
    TimeSpan Timeout, int MaximumOutputBytes = 1024 * 1024);
public sealed record ServerPlan(Guid InstanceId, ServerOperation Operation, ImmutableArray<ServerCommand> Commands,
    ImmutableArray<string> Blockers, string Explanation)
{
    public bool CanExecute => Blockers.IsEmpty;
}
public sealed record ServerCommandResult(int ExitCode, string Output, string Error, bool Cancelled);
public interface IServerCommandExecutor
{
    Task<ServerCommandResult> ExecuteAsync(ServerCommand command, CancellationToken cancellationToken);
}
public sealed record ServerProgress(Guid InstanceId, string Phase, long CompletedBytes, long? TotalBytes);
