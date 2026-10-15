using System.Diagnostics;
using System.Text;

namespace WinSshCopyId.Tools.Servers;

/// <summary>Runs one admitted CLI without a shell, with bounded output, duration and owned-tree cancellation.</summary>
public sealed class ServerCommandExecutor(ExecutableReceipt receipt) : IServerCommandExecutor
{
    public async Task<ServerCommandResult> ExecuteAsync(ServerCommand command, CancellationToken cancellationToken)
    {
        if (!string.Equals(Path.GetFullPath(command.Executable), Path.GetFullPath(receipt.Path), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Command executable is not the admitted CLI.");
        if (command.Timeout < TimeSpan.FromSeconds(1) || command.Timeout > TimeSpan.FromMinutes(10) || command.MaximumOutputBytes is < 1024 or > 16 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(command));
        await ServerProcessAdapter.VerifyExecutableAsync(receipt, cancellationToken);
        var start = new ProcessStartInfo(receipt.Path) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = command.WorkingDirectory };
        foreach (var argument in command.Arguments)
        {
            if (argument.Length > 32768 || argument.Contains('\0')) throw new ArgumentException("Invalid command argument.");
            start.ArgumentList.Add(argument);
        }
        using var process = new Process { StartInfo = start };
        if (!process.Start()) throw new IOException("CLI process did not start.");
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); limit.CancelAfter(command.Timeout);
        var stdout = ReadAsync(process.StandardOutput, command.MaximumOutputBytes, limit.Token);
        var stderr = ReadAsync(process.StandardError, command.MaximumOutputBytes, limit.Token);
        try
        {
            await Task.WhenAll(process.WaitForExitAsync(limit.Token), stdout, stderr);
            return new(process.ExitCode, await stdout, await stderr, false);
        }
        catch
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            try { await Task.WhenAll(stdout, stderr); } catch { }
            throw;
        }
    }
    private static async Task<string> ReadAsync(StreamReader reader, int maximum, CancellationToken token)
    {
        var output = new StringBuilder(); var buffer = new char[4096]; int count; long bytes = 0;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), token)) != 0)
        {
            bytes += Encoding.UTF8.GetByteCount(buffer.AsSpan(0, count));
            if (bytes > maximum) throw new InvalidDataException("CLI output exceeded its bound.");
            output.Append(buffer, 0, count);
        }
        return output.ToString();
    }
}
