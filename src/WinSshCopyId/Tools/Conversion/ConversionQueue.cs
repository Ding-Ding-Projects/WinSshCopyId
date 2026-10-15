using System.Text.Json;

namespace WinSshCopyId.Tools.Conversion;

public sealed record ConversionJob(string Id, string Source, string Destination, string AdapterId,
    string State, string Message, DateTimeOffset UpdatedAt);

/// <summary>One durable record per file. Discovery and processing never collect the complete queue.</summary>
public sealed class ConversionQueue
{
    private readonly string directory;
    private readonly SemaphoreSlim writer = new(1, 1);
    public ConversionQueue(string privateStorageDirectory)
    {
        directory = Path.GetFullPath(privateStorageDirectory);
        Directory.CreateDirectory(directory);
    }

    public async Task<ConversionJob> EnqueueAsync(string source, string destination, string adapterId, CancellationToken token = default)
    {
        var job = new ConversionJob(Guid.NewGuid().ToString("N"), Path.GetFullPath(source), Path.GetFullPath(destination),
            adapterId, "Queued", "Waiting for an explicit run action.", DateTimeOffset.UtcNow);
        await writer.WaitAsync(token);
        try { await SaveAsync(job, token); }
        finally { writer.Release(); }
        return job;
    }

    public IEnumerable<ConversionJob> ReadPage(int offset, int pageSize = 50)
    {
        if (offset < 0 || pageSize is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(pageSize));
        foreach (var path in Directory.EnumerateFiles(directory, "*.json").Skip(offset).Take(pageSize))
        {
            var info = new FileInfo(path);
            if (info.Length > 65536) throw new InvalidDataException("Queue record exceeds its safety limit.");
            yield return JsonSerializer.Deserialize<ConversionJob>(File.ReadAllText(path))
                ?? throw new InvalidDataException("Invalid queue record.");
        }
    }

    public async Task SetPausedAsync(bool paused, CancellationToken token = default)
    {
        if (paused) await File.WriteAllTextAsync(Path.Combine(directory, "paused"), "Paused", token);
        else File.Delete(Path.Combine(directory, "paused"));
    }

    public async Task CancelAsync(string id, CancellationToken token = default)
    {
        ValidateId(id);
        await writer.WaitAsync(token);
        try
        {
            var job = JsonSerializer.Deserialize<ConversionJob>(await File.ReadAllTextAsync(Path.Combine(directory, id + ".json"), token))
                ?? throw new InvalidDataException("Invalid queue record.");
            if (job.State == "Running") throw new InvalidOperationException("Cancel the active run through its cancellation token.");
            await SaveAsync(job with { State = "Cancelled", Message = "Cancelled before processing.", UpdatedAt = DateTimeOffset.UtcNow }, token);
        }
        finally { writer.Release(); }
    }

    public Task ProcessAsync(LocalConverter converter, IProgress<ConversionJob>? progress = null, CancellationToken token = default)
        => ProcessCoreAsync(converter.ConvertAsync, progress, token);
    public Task ProcessAsync(ConfinedLocalConverter converter, IProgress<ConversionJob>? progress = null, CancellationToken token = default)
        => ProcessCoreAsync(converter.ConvertAsync, progress, token);
    private async Task ProcessCoreAsync(Func<string, string, string, CancellationToken, Task<ConversionResult>> convert,
        IProgress<ConversionJob>? progress, CancellationToken token)
    {
        await writer.WaitAsync(token);
        try
        {
            foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
            {
                token.ThrowIfCancellationRequested();
                if (File.Exists(Path.Combine(directory, "paused"))) return;
                if (new FileInfo(path).Length > 65536) throw new InvalidDataException("Queue record exceeds its safety limit.");
                var job = JsonSerializer.Deserialize<ConversionJob>(await File.ReadAllTextAsync(path, token))
                    ?? throw new InvalidDataException("Invalid queue record.");
                if (job.State == "Running")
                {
                    job = job with { State = "Interrupted", Message = "Previous run was interrupted. Review the source and destination before re-enqueueing.", UpdatedAt = DateTimeOffset.UtcNow };
                    await SaveAsync(job, token); progress?.Report(job); continue;
                }
                if (job.State != "Queued") continue;
                job = job with { State = "Running", UpdatedAt = DateTimeOffset.UtcNow };
                await SaveAsync(job, token); progress?.Report(job);
                try
                {
                    var result = await convert(job.Source, job.Destination, job.AdapterId, token);
                    job = job with { State = result.Status, Message = result.Message, UpdatedAt = DateTimeOffset.UtcNow };
                }
                catch (OperationCanceledException) { job = job with { State = "Cancelled", Message = "Cancelled during processing.", UpdatedAt = DateTimeOffset.UtcNow }; }
                catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or FormatException or JsonException or System.Text.DecoderFallbackException or UnauthorizedAccessException)
                { job = job with { State = "Failed", Message = "Conversion did not complete: " + ex.GetType().Name, UpdatedAt = DateTimeOffset.UtcNow }; }
                await SaveAsync(job, CancellationToken.None); progress?.Report(job);
                token.ThrowIfCancellationRequested();
            }
        }
        finally { writer.Release(); }
    }

    private async Task SaveAsync(ConversionJob job, CancellationToken token)
    {
        ValidateId(job.Id);
        var temporary = Path.Combine(directory, job.Id + ".tmp");
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(job);
            if (bytes.Length > 65536) throw new InvalidDataException("Queue record exceeds its safety limit.");
            await File.WriteAllBytesAsync(temporary, bytes, token);
            File.Move(temporary, Path.Combine(directory, job.Id + ".json"), true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static void ValidateId(string id)
    { if (!Guid.TryParseExact(id, "N", out _)) throw new ArgumentException("Invalid queue identifier.", nameof(id)); }
}
