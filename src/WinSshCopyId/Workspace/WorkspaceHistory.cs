using System.Diagnostics;
using System.Text.Json;

namespace WinSshCopyId.Workspace;

public sealed record HistoryRecord(Guid Id, DateTimeOffset At, Guid RecordId, string Action,
    string RecordKind, Guid? RestoredFrom = null);
public sealed record HistoryWriteResult(bool Recorded, string? UnavailableReason = null);
public interface IProcessExecutor
{
    Task<int> RunAsync(string executable, string workingDirectory, IReadOnlyList<string> arguments, CancellationToken cancellationToken);
}
public sealed class ProcessExecutor : IProcessExecutor
{
    public async Task<int> RunAsync(string executable, string workingDirectory, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo(executable) { WorkingDirectory = workingDirectory, CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new IOException("The history process could not start.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var error = process.StandardError.ReadToEndAsync(timeout.Token);
        try { await process.WaitForExitAsync(timeout.Token); await Task.WhenAll(output, error); return process.ExitCode; }
        catch { try { process.Kill(true); } catch (InvalidOperationException) { } throw; }
    }
}
public static class HistoryGitResolver
{
    public static string? Resolve(string applicationDirectory, string? path = null)
    {
        var candidates = new List<string> { Path.Combine(applicationDirectory, "tools", "git", "cmd", "git.exe"), Path.Combine(applicationDirectory, "tools", "git", "bin", "git.exe") };
        foreach (var directory in (path ?? Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            candidates.Add(Path.Combine(directory.Trim('"'), OperatingSystem.IsWindows() ? "git.exe" : "git"));
        return candidates.FirstOrDefault(File.Exists);
    }
}
public sealed class WorkspaceHistory(string isolatedHistoryDirectory, string? gitExecutable, IProcessExecutor executor, IClock clock)
{
    private readonly SemaphoreSlim mutex = new(1);
    private static readonly HashSet<string> Actions = ["created", "updated", "deleted", "restored", "undone", "imported", "settings-changed", "discarded", "renamed", "reset"];
    private static readonly HashSet<string> Kinds = ["profile", "settings", "display-name", "schedule", "tab", "document", "credential"];
    public async Task<HistoryWriteResult> RecordAsync(Guid recordId, string action, string recordKind, Guid? restoredFrom = null, CancellationToken cancellationToken = default)
    {
        if (!Actions.Contains(action) || !Kinds.Contains(recordKind) || recordId == Guid.Empty) throw new ArgumentException("Unsupported history metadata.");
        if (gitExecutable is null || !File.Exists(gitExecutable)) return new(false, "A genuine Git executable is unavailable; local history was not recorded.");
        await mutex.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(isolatedHistoryDirectory);
            var commands = new List<string[]>();
            if (!Directory.Exists(Path.Combine(isolatedHistoryDirectory, ".git"))) commands.Add(["init", "--quiet"]);
            foreach (var command in commands)
                if (await executor.RunAsync(gitExecutable, isolatedHistoryDirectory, command, cancellationToken) != 0) return new(false, "Local history initialization failed.");
            var record = new HistoryRecord(Guid.NewGuid(), clock.UtcNow, recordId, action, recordKind, restoredFrom);
            var relative = record.Id.ToString("N") + ".json";
            await File.WriteAllBytesAsync(Path.Combine(isolatedHistoryDirectory, relative), JsonSerializer.SerializeToUtf8Bytes(record), cancellationToken);
            if (await executor.RunAsync(gitExecutable, isolatedHistoryDirectory, ["add", "--", relative], cancellationToken) != 0) return new(false, "Local history staging failed.");
            if (await executor.RunAsync(gitExecutable, isolatedHistoryDirectory,
                ["-c", "user.name=WinSshCopyId Local History", "-c", "user.email=local-history@localhost", "-c", "commit.gpgsign=false", "commit", "--quiet", "-m", recordKind + ": " + action, "--", relative], cancellationToken) != 0)
                return new(false, "Local history commit failed; the metadata record is retained for recovery.");
            return new(true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or OperationCanceledException or System.ComponentModel.Win32Exception)
        { return new(false, "Local history is unavailable; the requested operation can continue."); }
        finally { mutex.Release(); }
    }
    public async Task<IReadOnlyList<HistoryRecord>> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(isolatedHistoryDirectory)) return [];
        var result = new List<HistoryRecord>();
        foreach (var file in Directory.EnumerateFiles(isolatedHistoryDirectory, "*.json").Take(10000))
        {
            if (new FileInfo(file).Length > 4096) continue;
            try { var item = JsonSerializer.Deserialize<HistoryRecord>(await File.ReadAllBytesAsync(file, cancellationToken)); if (item is not null) result.Add(item); }
            catch (JsonException) { }
        }
        return result.OrderByDescending(r => r.At).ToArray();
    }
}

public enum OperationState { Queued, Running, Completed, Failed, Cancelled }
public sealed record WorkspaceOperation(Guid Id, string Kind, OperationState State, int Completed, int Total, string? SafeMessage = null);
public sealed record WorkspaceNotification(Guid Id, DateTimeOffset At, string Category, string SafeMessage, bool RequiresAttention = false, bool Dismissed = false);
public sealed class BoundedQueue<T>(int capacity)
{
    private readonly Queue<T> queue = new();
    public void Enqueue(T item) { lock (queue) { if (capacity is < 1 or > 10000) throw new ArgumentOutOfRangeException(nameof(capacity)); while (queue.Count >= capacity) queue.Dequeue(); queue.Enqueue(item); } }
    public IReadOnlyList<T> Snapshot() { lock (queue) return queue.ToArray(); }
    public void Clear() { lock (queue) queue.Clear(); }
}
