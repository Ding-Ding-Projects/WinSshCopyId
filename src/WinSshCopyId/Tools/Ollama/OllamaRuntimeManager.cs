using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace WinSshCopyId.Tools.Ollama;

public sealed record OllamaRuntimeReceipt(int ProcessId, DateTimeOffset StartedAt, string ExecutableSha256,
    string Version, bool ListenerOwnershipVerified, bool HealthVerified);

/// <summary>Starts only an admitted managed runtime. Existing listeners and user-managed services are preserved.</summary>
public sealed class OllamaRuntimeManager
{
    private readonly SemaphoreSlim mutation = new(1, 1);
    private Process? owned;
    private OllamaRuntimeReceipt? receipt;
    private Task? output;
    private Task? error;
    public async Task<OllamaRuntimeReceipt> StartAsync(OllamaAdmittedPayload payload, OllamaInstallationReceipt installation,
        OllamaInstaller installer, CancellationToken token = default)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Managed runtime listener ownership is implemented for Windows only.");
        await mutation.WaitAsync(token);
        try
        {
            if (owned is not null) throw new InvalidOperationException("An owned runtime session already exists.");
            if (payload.Manifest.Version != installation.Version || payload.Manifest.Architecture != installation.Architecture ||
                !payload.Manifest.ArchiveSha256.Equals(installation.ArchiveSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Installation is not bound to the admitted payload.");
            if (LoopbackListenerOwner() is not null) throw new InvalidOperationException("The local runtime port is already owned. Existing services are preserved; use health verification instead of launching another instance.");
            var executable = Path.Combine(installation.Directory, "ollama.exe");
            var member = payload.Manifest.Members.Single(x => x.RelativePath == "ollama.exe");
            await using var lease = new FileStream(executable, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (lease.Length != member.Length || !Convert.ToHexString(await SHA256.HashDataAsync(lease, token)).Equals(member.Sha256, StringComparison.OrdinalIgnoreCase) ||
                !await payload.VerifyPublisherAsync(executable, payload.Manifest.PublisherRequirement, token))
                throw new InvalidDataException("Managed executable digest or publisher changed.");
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
                WorkingDirectory = installation.Directory, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("serve");
            start.Environment.Clear();
            foreach (var key in new[] { "SystemRoot", "WINDIR", "ProgramData", "USERPROFILE", "APPDATA", "LOCALAPPDATA", "TEMP", "TMP", "HOMEDRIVE", "HOMEPATH" })
                if (Environment.GetEnvironmentVariable(key) is { } value) start.Environment[key] = value;
            start.Environment["PATH"] = Environment.SystemDirectory;
            start.Environment["OLLAMA_HOST"] = "127.0.0.1:11434";
            // No global PATH, startup entry, model-store location or user service is changed.
            var process = new Process { StartInfo = start };
            try
            {
                token.ThrowIfCancellationRequested();
                if (!process.Start()) throw new IOException("Managed runtime did not start.");
                owned = process;
                output = DrainAsync(process.StandardOutput); error = DrainAsync(process.StandardError);
                var started = new DateTimeOffset(process.StartTime.ToUniversalTime());
                using var healthDeadline = CancellationTokenSource.CreateLinkedTokenSource(token); healthDeadline.CancelAfter(TimeSpan.FromSeconds(30));
                using var api = new OllamaClient();
                while (true)
                {
                    healthDeadline.Token.ThrowIfCancellationRequested();
                    if (process.HasExited) throw new IOException("Managed runtime exited before readiness.");
                    var listener = LoopbackListenerOwner();
                    if (listener is not null && listener != process.Id) throw new IOException("The runtime port was acquired by another process; that process is preserved.");
                    if (listener == process.Id)
                    {
                        var health = await api.GetHealthAsync(healthDeadline.Token);
                        if (health.Reachable)
                        {
                            if (health.Version != installation.Version) throw new InvalidDataException("Runtime API version does not match the admitted installation.");
                            receipt = new(process.Id, started, member.Sha256, installation.Version, true, true);
                            return receipt;
                        }
                    }
                    await Task.Delay(250, healthDeadline.Token);
                }
            }
            catch
            {
                // Only the exact process started above may be terminated. Restore the managed selection, never a user installation.
                try { if (owned is not null && !owned.HasExited) { owned.Kill(entireProcessTree: true); await owned.WaitForExitAsync(CancellationToken.None); } }
                finally
                {
                    owned?.Dispose(); owned = null; receipt = null;
                    await installer.RestoreRecordAsync(installation, CancellationToken.None);
                }
                throw;
            }
        }
        finally { mutation.Release(); }
    }
    private static async Task DrainAsync(StreamReader reader)
    {
        var buffer = new char[4096];
        try { while (await reader.ReadAsync(buffer) != 0) { } }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }
    public async Task StopOwnedAsync(bool explicitlyConfirmed, CancellationToken token = default)
    {
        if (!explicitlyConfirmed) throw new InvalidOperationException("Confirm termination of the owned runtime session. Existing model operations may be interrupted.");
        await mutation.WaitAsync(token);
        try
        {
            if (owned is null || receipt is null) return;
            if (!owned.HasExited)
            {
                if (owned.Id != receipt.ProcessId || new DateTimeOffset(owned.StartTime.ToUniversalTime()) != receipt.StartedAt)
                    throw new InvalidDataException("Owned runtime process identity changed.");
                // Ollama exposes no documented shutdown HTTP operation. This is explicit owned-process termination.
                owned.Kill(entireProcessTree: true);
                await owned.WaitForExitAsync(token);
            }
            try { await Task.WhenAll(output!, error!).WaitAsync(TimeSpan.FromSeconds(5), token); }
            catch (TimeoutException) { owned.StandardOutput.Dispose(); owned.StandardError.Dispose(); }
            owned.Dispose(); owned = null; receipt = null;
        }
        finally { mutation.Release(); }
    }
    public static int? LoopbackListenerOwner()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        uint size = 0;
        var status = GetExtendedTcpTable(IntPtr.Zero, ref size, true, 2, 3, 0);
        if (status != 122 || size > 16 * 1024 * 1024) throw new IOException("TCP ownership inventory is unavailable.");
        var pointer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (GetExtendedTcpTable(pointer, ref size, true, 2, 3, 0) != 0) throw new IOException("TCP ownership inventory changed or is unavailable.");
            var count = Marshal.ReadInt32(pointer);
            if (count < 0 || 4L + count * 24L > size) throw new InvalidDataException("Invalid TCP ownership inventory.");
            for (var i = 0; i < count; i++)
            {
                var row = IntPtr.Add(pointer, 4 + i * 24);
                var state = Marshal.ReadInt32(row);
                var address = unchecked((uint)Marshal.ReadInt32(row, 4));
                var portBytes = unchecked((uint)Marshal.ReadInt32(row, 8));
                var port = (int)(((portBytes & 0xff) << 8) | ((portBytes >> 8) & 0xff));
                if (state == 2 && port == 11434 && (address == 0x0100007f || address == 0)) return Marshal.ReadInt32(row, 20);
            }
            return null;
        }
        finally { Marshal.FreeHGlobal(pointer); }
    }
    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref uint size, [MarshalAs(UnmanagedType.Bool)] bool order,
        int family, int tableClass, uint reserved);
}
