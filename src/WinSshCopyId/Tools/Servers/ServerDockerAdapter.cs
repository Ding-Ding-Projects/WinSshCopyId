using System.Collections.Immutable;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WinSshCopyId.Tools.Servers;

public sealed record ServerContainerProfile(string ImageWithDigest, string Entrypoint,
    ImmutableArray<string> Arguments, bool ImageProvenanceVerified);
public sealed record ServerContainerReceipt(Guid InstanceId, string ContainerId, string ImageWithDigest,
    DateTimeOffset CreatedAt, string State);

/// <summary>Creates labeled containers from already present approved immutable images. Never pulls an image.</summary>
public sealed class ServerDockerAdapter(IServerCommandExecutor executor, string dockerExecutable)
{
    private const string Label = "winsshcopyid.instance";
    private readonly SemaphoreSlim mutation = new(1, 1);
    private readonly Dictionary<Guid, ServerContainerReceipt> owned = [];
    private Task<ServerCommandResult> RunAsync(ServerInstance instance, IEnumerable<string> arguments, CancellationToken token)
        => executor.ExecuteAsync(new(dockerExecutable, arguments.ToImmutableArray(), instance.Target.WorkingDirectory, TimeSpan.FromSeconds(60)), token);
    private static void Success(ServerCommandResult result)
    {
        if (result.Cancelled || result.ExitCode != 0) throw new IOException("Docker operation did not succeed: " + result.Error);
    }
    public async Task<ServerContainerReceipt> StartAsync(ServerInstance instance, ServerContainerProfile profile, CancellationToken token = default)
    {
        ServerLifecyclePlanner.ValidateInstance(instance);
        if (instance.Target.Kind != ServerTargetKind.LocalDocker || !instance.Adopted || !instance.EulaAccepted)
            throw new InvalidOperationException("An adopted local Docker target and explicit EULA consent are required.");
        if (!profile.ImageProvenanceVerified || !Regex.IsMatch(profile.ImageWithDigest, "^[a-zA-Z0-9./:_-]+@sha256:[a-fA-F0-9]{64}$") ||
            profile.ImageWithDigest != instance.Distribution.ContainerImageDigest)
            throw new InvalidDataException("Approved immutable image provenance is required.");
        if (!profile.Entrypoint.StartsWith('/') || profile.Entrypoint.Contains('\0') || profile.Arguments.IsDefault ||
            profile.Arguments.Any(x => x.Contains('\0') || x.Length > 32768)) throw new ArgumentException("Invalid typed container profile.");
        if (instance.Target.WorkingDirectory.Contains(',') || !Directory.Exists(instance.Target.WorkingDirectory) ||
            (File.GetAttributes(instance.Target.WorkingDirectory) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("An existing non-linked mount directory without commas is required.");
        await mutation.WaitAsync(token);
        try
        {
            if (owned.ContainsKey(instance.Id)) throw new InvalidOperationException("Instance already owns a container.");
            ServerProcessAdapter.EnsurePortAvailable(instance.Port);
            var image = await RunAsync(instance, ["image", "inspect", profile.ImageWithDigest, "--format", "{{json .RepoDigests}}"], token); Success(image);
            var digests = JsonSerializer.Deserialize<string[]>(image.Output) ?? [];
            if (!digests.Contains(profile.ImageWithDigest, StringComparer.Ordinal)) throw new InvalidDataException("Daemon image digest does not match approval.");
            var args = new List<string> { "container", "create", "--pull", "never", "--name", "winsshcopyid-" + instance.Id.ToString("N"),
                "--label", Label + "=" + instance.Id.ToString("D"), "--memory", instance.MemoryMiB + "m", "--pids-limit", "256",
                "--cap-drop", "ALL", "--security-opt", "no-new-privileges", "--init", "--workdir", "/data",
                "--mount", "type=bind,source=" + instance.Target.WorkingDirectory + ",target=/data", "--publish", "127.0.0.1:" + instance.Port + ":" + instance.Port + "/tcp",
                "--publish", "127.0.0.1:" + instance.Port + ":" + instance.Port + "/udp", "--entrypoint", profile.Entrypoint, profile.ImageWithDigest };
            args.AddRange(profile.Arguments);
            var create = await RunAsync(instance, args, token); Success(create);
            var id = create.Output.Trim();
            if (!Regex.IsMatch(id, "^[a-fA-F0-9]{64}$")) throw new InvalidDataException("Daemon returned an invalid container identity.");
            var receipt = new ServerContainerReceipt(instance.Id, id, profile.ImageWithDigest, DateTimeOffset.UtcNow, "Created");
            owned.Add(instance.Id, receipt);
            try
            {
                await VerifyOwnershipAsync(instance, receipt, token);
                var start = await RunAsync(instance, ["container", "start", id], token); Success(start);
                receipt = receipt with { State = "Started; readiness not yet verified" }; owned[instance.Id] = receipt; return receipt;
            }
            catch
            {
                // The created container remains labeled and recorded for explicit retry/removal; do not delete an uncertain result.
                throw;
            }
        }
        finally { mutation.Release(); }
    }
    private async Task<JsonElement> VerifyOwnershipAsync(ServerInstance instance, ServerContainerReceipt receipt, CancellationToken token)
    {
        var inspect = await RunAsync(instance, ["container", "inspect", receipt.ContainerId], token); Success(inspect);
        using var json = JsonDocument.Parse(inspect.Output);
        var container = json.RootElement[0];
        if (container.GetProperty("Id").GetString() != receipt.ContainerId ||
            container.GetProperty("Config").GetProperty("Labels").GetProperty(Label).GetString() != instance.Id.ToString("D") ||
            container.GetProperty("Config").GetProperty("Image").GetString() != receipt.ImageWithDigest)
            throw new InvalidDataException("Container ownership or image identity changed.");
        return container.Clone();
    }
    public async Task<ServerObservation> ObserveAsync(ServerInstance instance, CancellationToken token = default)
    {
        await mutation.WaitAsync(token);
        try
        {
            if (!owned.TryGetValue(instance.Id, out var receipt)) return new(instance.Id, ServerObservedState.Unknown, DateTimeOffset.UtcNow, null, "No container owned by this adapter.");
            var container = await VerifyOwnershipAsync(instance, receipt, token);
            var running = container.GetProperty("State").GetProperty("Running").GetBoolean();
            return new(instance.Id, running ? ServerObservedState.Running : ServerObservedState.Stopped, DateTimeOffset.UtcNow,
                new(0, receipt.CreatedAt, receipt.ImageWithDigest, receipt.ImageWithDigest.Split('@')[1], receipt.ContainerId), "Verified container label and immutable image; protocol readiness is separate.");
        }
        catch (HttpRequestException) { return new(instance.Id, ServerObservedState.Disconnected, DateTimeOffset.UtcNow, null, "Docker transport disconnected; stopped state is unknown."); }
        finally { mutation.Release(); }
    }
    public async Task<ServerStopReceipt> StopAsync(ServerInstance instance, CancellationToken token = default)
    {
        await mutation.WaitAsync(token);
        try
        {
            if (!owned.TryGetValue(instance.Id, out var receipt)) throw new InvalidOperationException("No owned container session exists.");
            await VerifyOwnershipAsync(instance, receipt, token);
            // Infinite daemon grace avoids Docker escalating to SIGKILL. The CLI itself remains deadline bounded.
            var stop = await RunAsync(instance, ["container", "stop", "--timeout", "-1", receipt.ContainerId], token); Success(stop);
            var state = await VerifyOwnershipAsync(instance, receipt, token);
            if (state.GetProperty("State").GetProperty("Running").GetBoolean()) return new(instance.Id, false, null, "Container remains running.");
            owned[instance.Id] = receipt with { State = "Stopped; retained" };
            return new(instance.Id, true, state.GetProperty("State").GetProperty("ExitCode").GetInt32(), "Owned container stopped and retained.");
        }
        finally { mutation.Release(); }
    }
}
