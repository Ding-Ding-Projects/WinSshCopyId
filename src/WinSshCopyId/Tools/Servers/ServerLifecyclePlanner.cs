using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace WinSshCopyId.Tools.Servers;

/// <summary>Produces reviewable lifecycle plans. No operation launches from construction or discovery.</summary>
public static partial class ServerLifecyclePlanner
{
    [GeneratedRegex("^[a-zA-Z0-9][a-zA-Z0-9_.-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeName();
    [GeneratedRegex("^sha256:[a-fA-F0-9]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex ImageDigest();
    public static void ValidateInstance(ServerInstance instance)
    {
        if (instance.Id == Guid.Empty || !SafeName().IsMatch(instance.Name)) throw new ArgumentException("Invalid server identity or name.");
        if (instance.Port is < 1 or > 65535 || instance.MemoryMiB is < 256 or > 1048576)
            throw new ArgumentOutOfRangeException(nameof(instance), "Invalid port or memory allocation.");
        if (!Path.IsPathFullyQualified(instance.Target.WorkingDirectory)) throw new ArgumentException("Absolute working directory required.");
        if (instance.Target.Kind is ServerTargetKind.SshProcess or ServerTargetKind.SshDocker)
        {
            if (string.IsNullOrWhiteSpace(instance.Target.Host) || instance.Target.Host.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or ':')) ||
                string.IsNullOrWhiteSpace(instance.Target.User) || !SafeName().IsMatch(instance.Target.User) ||
                instance.Target.SshPort is < 1 or > 65535)
                throw new ArgumentException("Invalid SSH endpoint.");
        }
    }
    public static ServerPlan Create(ServerInstance instance, ServerOperation operation,
        string dockerExecutable, string sshExecutable, ServerObservation? observation = null)
    {
        ValidateInstance(instance);
        var blockers = ImmutableArray.CreateBuilder<string>();
        var commands = ImmutableArray.CreateBuilder<ServerCommand>();
        if (operation != ServerOperation.Discover && !instance.Adopted) blockers.Add("Explicit instance adoption is required.");
        if (operation == ServerOperation.Start && !instance.EulaAccepted) blockers.Add("Explicit distribution EULA consent is required.");
        if (instance.Distribution.UnavailableReason is { Length: > 0 } reason) blockers.Add(reason);
        var remote = instance.Target.Kind is ServerTargetKind.SshProcess or ServerTargetKind.SshDocker;
        if (remote && string.IsNullOrWhiteSpace(instance.Target.ApprovedHostKeySha256)) blockers.Add("Approved SSH host-key identity is required.");
        if (operation is ServerOperation.Backup or ServerOperation.Restore or ServerOperation.Upgrade or ServerOperation.Rollback)
            blockers.Add("Use the transactional file operation service with a stopped, adopted local instance; remote transactions require an explicitly configured transport.");
        if (operation is ServerOperation.Stop or ServerOperation.Restart)
        {
            if (observation?.InstanceId != instance.Id || observation.State != ServerObservedState.Running || observation.Identity is null)
                blockers.Add("A current owned running-process or container observation is required.");
            // A PID alone never authorizes termination. The executor must revalidate identity and ownership.
            blockers.Add("Graceful shutdown transport has not been negotiated for this exact distribution.");
        }
        if (remote)
        {
            // Never construct a remotely interpreted shell command from user paths or arguments.
            blockers.Add("SSH execution requires a registered structured-agent transport; arbitrary remote shell execution is disabled.");
            if (!Path.IsPathFullyQualified(sshExecutable)) blockers.Add("Verified absolute SSH executable required.");
        }
        else if (instance.Target.Kind == ServerTargetKind.LocalDocker)
        {
            if (!Path.IsPathFullyQualified(dockerExecutable)) blockers.Add("Verified absolute Docker executable required.");
            if (operation == ServerOperation.Discover)
                commands.Add(new(dockerExecutable, ["container", "ls", "--all", "--filter", "label=winsshcopyid.instance=" + instance.Id.ToString("D"), "--format", "{{json .}}"], instance.Target.WorkingDirectory, TimeSpan.FromSeconds(15)));
            if (operation == ServerOperation.Start)
            {
                var image = instance.Distribution.ContainerImageDigest;
                if (image is null || !image.Contains('@') || !ImageDigest().IsMatch(image[(image.LastIndexOf('@') + 1)..]))
                    blockers.Add("An explicitly approved immutable container image digest is required.");
                else blockers.Add("Container mount, entrypoint, image provenance and daemon capability acceptance remain unverified.");
            }
        }
        else if (operation == ServerOperation.Start)
        {
            var distribution = instance.Distribution;
            if (distribution.ExecutablePath is null || !Path.IsPathFullyQualified(distribution.ExecutablePath) ||
                distribution.Sha256 is null || distribution.Sha256.Length != 64)
                blockers.Add("Verified absolute distribution path and SHA-256 are required.");
            else if (distribution.Edition == ServerEdition.Java)
            {
                if (distribution.RuntimePath is null || !Path.IsPathFullyQualified(distribution.RuntimePath)) blockers.Add("Verified version-compatible Java runtime path is required.");
                else commands.Add(new(distribution.RuntimePath, ["-Xmx" + instance.MemoryMiB + "M", "-jar", distribution.ExecutablePath, "nogui"], instance.Target.WorkingDirectory, TimeSpan.FromSeconds(30)));
            }
            else commands.Add(new(distribution.ExecutablePath, [], instance.Target.WorkingDirectory, TimeSpan.FromSeconds(30)));
        }
        return new(instance.Id, operation, commands.ToImmutable(), blockers.ToImmutable(),
            "A plan is not execution or runtime verification. Discovery does not adopt an existing server.");
    }
    public static async Task VerifyDistributionAsync(ServerDistribution distribution, CancellationToken token = default)
    {
        if (distribution.ExecutablePath is null || distribution.Sha256 is null) throw new InvalidOperationException("Distribution receipt missing.");
        await using var file = File.OpenRead(distribution.ExecutablePath);
        var digest = Convert.ToHexString(await SHA256.HashDataAsync(file, token));
        if (!string.Equals(digest, distribution.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Distribution digest mismatch.");
    }
}
