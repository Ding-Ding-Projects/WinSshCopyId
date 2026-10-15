using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace WinSshCopyId.Tools.Conversion;

public sealed record PackagedWorkerReceipt(string ExecutablePath, string Sha256);
public sealed record WorkerVerificationResult(int ExitCode, string Output, string Diagnostics);

/// <summary>Verifies exact worker bytes. This is not a sandbox or production availability receipt.</summary>
public static class PackagedWorkerVerifier
{
    public static async Task<PackagedWorkerReceipt> InspectAsync(string packageDirectory, string expectedSha256,
        CancellationToken token = default)
    {
        if (expectedSha256.Length != 64 || !expectedSha256.All(Uri.IsHexDigit))
            throw new ArgumentException("A pinned SHA-256 from the reviewed package manifest is required.", nameof(expectedSha256));
        var directory = Path.GetFullPath(packageDirectory);
        var path = Path.Combine(directory, "WinSshCopyId.ToolWorker.exe");
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0 || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Worker package reparse points are not supported.");
        await using var file = File.OpenRead(path);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(file, token));
        if (!hash.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Packaged worker hash does not match the reviewed manifest.");
        return new(path, hash);
    }

    public static async Task<WorkerVerificationResult> RunForVerificationOnlyAsync(PackagedWorkerReceipt receipt,
        string adapterId, string source, string destination, CancellationToken token = default)
    {
        var adapter = ConversionCatalog.Adapters.SingleOrDefault(a => a.Id == adapterId);
        if (adapter is null || adapter.Sources.IsEmpty) throw new NotSupportedException("Only implemented managed adapters can be verified.");
        var verified = await InspectAsync(Path.GetDirectoryName(receipt.ExecutablePath)!, receipt.Sha256, token);
        if (!string.Equals(verified.ExecutablePath, receipt.ExecutablePath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Worker executable path is not the exact packaged member.");
        var start = new ProcessStartInfo(verified.ExecutablePath)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(verified.ExecutablePath)!
        };
        start.ArgumentList.Add("--verify-managed"); start.ArgumentList.Add(adapterId);
        start.ArgumentList.Add(Path.GetFullPath(source)); start.ArgumentList.Add(Path.GetFullPath(destination));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(35));
        using var process = Process.Start(start) ?? throw new IOException("The packaged worker could not start.");
        try
        {
            var outputTask = ReadBoundedAsync(process.StandardOutput, deadline.Token);
            var diagnosticTask = ReadBoundedAsync(process.StandardError, deadline.Token);
            await Task.WhenAll(outputTask, diagnosticTask, process.WaitForExitAsync(deadline.Token));
            return new(process.ExitCode, await outputTask, await diagnosticTask);
        }
        finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(CancellationToken.None); } }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken token)
    {
        var text = new StringBuilder(); var buffer = new char[512]; int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), token)) != 0)
        {
            if (text.Length + count > 4096) throw new InvalidDataException("Worker diagnostic output exceeded its bound.");
            text.Append(buffer, 0, count);
        }
        return text.ToString();
    }
}
