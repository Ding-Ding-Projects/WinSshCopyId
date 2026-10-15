using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace WinSshCopyId.Tools.Servers;

public sealed record ExecutableReceipt(string Path, string Sha256);
public sealed record ServerStartReceipt(Guid InstanceId, int ProcessId, DateTimeOffset StartedAt,
    string ExecutablePath, string ExecutableSha256, string DistributionSha256, string State);
public sealed record ServerStopReceipt(Guid InstanceId, bool Exited, int? ExitCode, string Detail);

/// <summary>Owns only processes started by this adapter. Existing processes are never adopted by PID.</summary>
public sealed class ServerProcessAdapter : IAsyncDisposable
{
    private sealed record Owned(Process Process, ServerStartReceipt Receipt, Task Stdout, Task Stderr);
    private readonly ConcurrentDictionary<Guid, Owned> processes = new();
    private readonly SemaphoreSlim mutation = new(1, 1);
    public event EventHandler<(Guid InstanceId, string Stream, string Text)>? Output;

    public static async Task VerifyExecutableAsync(ExecutableReceipt receipt, CancellationToken token)
    {
        if (!System.IO.Path.IsPathFullyQualified(receipt.Path) || receipt.Sha256.Length != 64 || !receipt.Sha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("An absolute executable path and SHA-256 receipt are required.");
        if ((File.GetAttributes(receipt.Path) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Executable links are not admitted.");
        await using var input = File.OpenRead(receipt.Path);
        if (!Convert.ToHexString(await SHA256.HashDataAsync(input, token)).Equals(receipt.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Executable receipt mismatch.");
    }
    public async Task<ServerStartReceipt> StartAsync(ServerInstance instance, ExecutableReceipt runtime,
        CancellationToken token = default)
    {
        ServerLifecyclePlanner.ValidateInstance(instance);
        if (instance.Target.Kind != ServerTargetKind.LocalProcess || !instance.Adopted || !instance.EulaAccepted)
            throw new InvalidOperationException("An adopted local-process instance with explicit EULA consent is required.");
        if (instance.Distribution.UnavailableReason is { Length: > 0 } reason) throw new InvalidOperationException(reason);
        await mutation.WaitAsync(token);
        try
        {
            if (processes.ContainsKey(instance.Id)) throw new InvalidOperationException("This instance already has an owned session.");
            await ServerLifecyclePlanner.VerifyDistributionAsync(instance.Distribution, token);
            await VerifyExecutableAsync(runtime, token);
            // Retain read leases through creation so an admitted file cannot be opened for mutation during launch.
            await using var runtimeLease = new FileStream(runtime.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
            await using var distributionLease = new FileStream(instance.Distribution.ExecutablePath!, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (!Convert.ToHexString(await SHA256.HashDataAsync(runtimeLease, token)).Equals(runtime.Sha256, StringComparison.OrdinalIgnoreCase) ||
                !Convert.ToHexString(await SHA256.HashDataAsync(distributionLease, token)).Equals(instance.Distribution.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Admitted file identity changed before launch.");
            var executable = instance.Distribution.Edition == ServerEdition.Java ? runtime.Path : instance.Distribution.ExecutablePath!;
            if (instance.Distribution.Edition == ServerEdition.Java && !string.Equals(instance.Distribution.RuntimePath, runtime.Path, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Runtime path does not match the selected distribution.");
            if (instance.Distribution.Edition == ServerEdition.Bedrock && !string.Equals(instance.Distribution.ExecutablePath, runtime.Path, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Bedrock executable does not match the admitted runtime.");
            if (!Directory.Exists(instance.Target.WorkingDirectory)) throw new DirectoryNotFoundException("Instance directory is missing.");
            if ((File.GetAttributes(instance.Target.WorkingDirectory) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Instance directory links are not admitted.");
            EnsurePortAvailable(instance.Port);
            var start = new ProcessStartInfo(executable)
            {
                WorkingDirectory = instance.Target.WorkingDirectory, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            if (instance.Distribution.Edition == ServerEdition.Java)
            {
                start.ArgumentList.Add("-Xmx" + instance.MemoryMiB.ToString(System.Globalization.CultureInfo.InvariantCulture) + "M");
                start.ArgumentList.Add("-jar"); start.ArgumentList.Add(instance.Distribution.ExecutablePath!); start.ArgumentList.Add("nogui");
            }
            token.ThrowIfCancellationRequested();
            var process = new Process { StartInfo = start, EnableRaisingEvents = true };
            try
            {
                if (!process.Start()) throw new IOException("Server process did not start.");
                var receipt = new ServerStartReceipt(instance.Id, process.Id, new DateTimeOffset(process.StartTime.ToUniversalTime()),
                    executable, runtime.Sha256, instance.Distribution.Sha256!, "Started; readiness not yet verified");
                var owned = new Owned(process, receipt, DrainAsync(instance.Id, "stdout", process.StandardOutput), DrainAsync(instance.Id, "stderr", process.StandardError));
                if (!processes.TryAdd(instance.Id, owned)) throw new InvalidOperationException("Concurrent instance session collision.");
                return receipt;
            }
            catch
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                process.Dispose(); throw;
            }
        }
        finally { mutation.Release(); }
    }
    private async Task DrainAsync(Guid id, string streamName, StreamReader reader)
    {
        var buffer = new char[4096];
        try
        {
            int count;
            while ((count = await reader.ReadAsync(buffer)) != 0)
            {
                // No accumulating log and no unbounded line allocation. Subscribers decide whether to retain redacted text.
                try { Output?.Invoke(this, (id, streamName, new string(buffer, 0, count))); } catch { /* A view subscriber cannot stop process drainage. */ }
            }
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }
    public ServerObservation Observe(Guid instanceId)
    {
        if (!processes.TryGetValue(instanceId, out var owned)) return new(instanceId, ServerObservedState.Unknown, DateTimeOffset.UtcNow, null, "No process owned by this adapter.");
        if (owned.Process.HasExited) return new(instanceId, ServerObservedState.Stopped, DateTimeOffset.UtcNow, null, "Owned process exited with code " + owned.Process.ExitCode);
        var identity = new ServerProcessIdentity(owned.Receipt.ProcessId, owned.Receipt.StartedAt, owned.Receipt.ExecutablePath, owned.Receipt.ExecutableSha256);
        return new(instanceId, ServerObservedState.Running, DateTimeOffset.UtcNow, identity, "Owned process is alive; protocol readiness is separate.");
    }
    public async Task<ServerStopReceipt> StopAsync(Guid instanceId, TimeSpan timeout, bool terminateOwnedTreeAfterTimeout = false,
        CancellationToken token = default)
    {
        if (timeout < TimeSpan.FromSeconds(1) || timeout > TimeSpan.FromMinutes(5)) throw new ArgumentOutOfRangeException(nameof(timeout));
        await mutation.WaitAsync(token);
        try
        {
            if (!processes.TryGetValue(instanceId, out var owned)) throw new InvalidOperationException("No owned process session exists.");
            var process = owned.Process;
            if (!process.HasExited)
            {
                if (process.Id != owned.Receipt.ProcessId || new DateTimeOffset(process.StartTime.ToUniversalTime()) != owned.Receipt.StartedAt)
                    throw new InvalidOperationException("Owned process identity changed.");
                await process.StandardInput.WriteLineAsync("stop".AsMemory(), token);
                await process.StandardInput.FlushAsync(token);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(timeout);
                try { await process.WaitForExitAsync(deadline.Token); }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    if (!terminateOwnedTreeAfterTimeout) return new(instanceId, false, null, "Graceful stop timed out; process retained. Explicit owned-tree termination is available.");
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(token);
                }
            }
            try { await Task.WhenAll(owned.Stdout, owned.Stderr).WaitAsync(TimeSpan.FromSeconds(5), token); }
            catch (TimeoutException) { process.StandardOutput.Dispose(); process.StandardError.Dispose(); }
            var result = new ServerStopReceipt(instanceId, true, process.ExitCode, "Owned process exited; no host power operation occurred.");
            processes.TryRemove(instanceId, out _); process.Dispose(); return result;
        }
        finally { mutation.Release(); }
    }
    public static void EnsurePortAvailable(int port)
    {
        var tcp = new TcpListener(IPAddress.Loopback, port);
        try { tcp.Start(); } finally { tcp.Stop(); }
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, port));
        // This is a preflight, not a reservation or proof that the child eventually bound the port.
    }
    public ValueTask DisposeAsync()
    {
        // Disposal intentionally does not terminate a server. Call StopAsync for each owned instance first.
        if (!processes.IsEmpty) throw new InvalidOperationException("Stop owned sessions before disposing the adapter.");
        mutation.Dispose(); return ValueTask.CompletedTask;
    }
}
