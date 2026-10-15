using System.Collections.Immutable;
using System.Text.Json;

namespace WinSshCopyId.Tools.Ollama;

public enum OllamaPullState { Queued, Pulling, Pulled, Skipped, Cancelled, Failed, Interrupted }
public enum OllamaCatalogCompleteness { Unavailable, Partial, Complete }
public sealed record OllamaPullItem(Guid Id, string Model, OllamaPullState State, long? CompletedBytes = null, long? TotalBytes = null, string? Detail = null);
public sealed record OllamaConversation(Guid Id, string Title, string Model, DateTimeOffset UpdatedAt, ImmutableArray<OllamaMessage> Messages);
public sealed record OllamaCatalogSnapshot(DateTimeOffset? RefreshedAt, DateTimeOffset? LastSuccessfulRefresh, string? SourceIdentity, int PageCount,
    OllamaCatalogCompleteness Completeness, ImmutableArray<OllamaModel> Variants, string Detail)
{
    public static OllamaCatalogSnapshot Unavailable => new(null, null, null, 0, OllamaCatalogCompleteness.Unavailable, [], "No verified official catalog has been acquired. Installed tags are not the full catalog.");
    public TimeSpan? Age(DateTimeOffset now) => RefreshedAt is { } date ? now - date : null;
}
public sealed record OllamaHardwareFit(string Verdict, DateTimeOffset EvaluatedAt, ImmutableArray<string> Evidence, ImmutableArray<string> MissingEvidence)
{
    public static OllamaHardwareFit Unknown => new("Unknown", DateTimeOffset.UtcNow, [], ["RAM, usable VRAM, driver/backend, storage and exact model context metadata have not been verified."]);
}
public sealed record OllamaLocalState(int SchemaVersion, ImmutableArray<OllamaPullItem> Pulls, ImmutableArray<OllamaConversation> Conversations, OllamaCatalogSnapshot Catalog)
{
    public static OllamaLocalState Empty => new(1, [], [], OllamaCatalogSnapshot.Unavailable);
}

/// <summary>Explicit local persistence only. Never logs prompts or includes them in ordinary exports.</summary>
public sealed class OllamaStateStore
{
    public string StatePath { get; }
    private readonly SemaphoreSlim access = new(1, 1);
    private const int MaximumBytes = 16 * 1024 * 1024;
    public OllamaStateStore(string? directory = null)
    {
        directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinSshCopyId", "Ollama");
        StatePath = Path.Combine(Path.GetFullPath(directory), "local-state.json");
    }
    public async Task<OllamaLocalState> LoadAsync(CancellationToken cancellationToken = default)
    {
        await access.WaitAsync(cancellationToken);
        try { return await ReadAsync(cancellationToken); }
        finally { access.Release(); }
    }
    public async Task<OllamaLocalState> UpdateAsync(Func<OllamaLocalState, OllamaLocalState> update, CancellationToken cancellationToken = default)
    {
        await access.WaitAsync(cancellationToken);
        try
        {
            var state = update(await ReadAsync(cancellationToken));
            Validate(state);
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(state);
            if (bytes.Length > MaximumBytes) throw new InvalidDataException("Local history exceeds storage limit. Export or remove a conversation first.");
            Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
            string temporary = StatePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { await File.WriteAllBytesAsync(temporary, bytes, cancellationToken); File.Move(temporary, StatePath, true); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            return state;
        }
        finally { access.Release(); }
    }
    private async Task<OllamaLocalState> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(StatePath)) return OllamaLocalState.Empty;
        using var input = new FileStream(StatePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > MaximumBytes) throw new InvalidDataException("Local state exceeds size limit; the original file was preserved.");
        var state = await JsonSerializer.DeserializeAsync<OllamaLocalState>(input, cancellationToken: cancellationToken) ?? throw new InvalidDataException("Local state is empty.");
        Validate(state);
        return state;
    }
    private static void Validate(OllamaLocalState state)
    {
        if (state.SchemaVersion != 1 || state.Pulls.IsDefault || state.Conversations.IsDefault || state.Catalog is null || state.Pulls.Length > 1000 || state.Conversations.Length > 500)
            throw new InvalidDataException("Unsupported or excessive local state; the original file was preserved.");
        foreach (var item in state.Pulls) OllamaClient.ValidateModel(item.Model);
        foreach (var chat in state.Conversations)
        {
            OllamaClient.ValidateModel(chat.Model);
            if (chat.Title is null || chat.Title.Length > 200 || chat.Messages.IsDefault || chat.Messages.Length > 256 || chat.Messages.Any(m => m.Content is null || m.Content.Length > 65536 || m.Role is not ("user" or "assistant" or "system")))
                throw new InvalidDataException("Saved conversation exceeds local limits.");
        }
        if (state.Catalog.Variants.IsDefault || state.Catalog.Variants.Length > 100000 || state.Catalog.PageCount < 0 ||
            (state.Catalog.Completeness == OllamaCatalogCompleteness.Complete && (state.Catalog.RefreshedAt is null || state.Catalog.LastSuccessfulRefresh is null || string.IsNullOrWhiteSpace(state.Catalog.SourceIdentity) || state.Catalog.PageCount == 0)))
            throw new InvalidDataException("Catalog completeness is missing provenance.");
    }
    public Task<OllamaLocalState> SaveConversationAsync(OllamaConversation conversation, CancellationToken cancellationToken = default) =>
        UpdateAsync(state => state with { Conversations = state.Conversations.RemoveAll(c => c.Id == conversation.Id).Add(conversation) }, cancellationToken);
    public Task<OllamaLocalState> DeleteConversationAsync(Guid id, bool confirmed, CancellationToken cancellationToken = default)
    {
        if (!confirmed) throw new InvalidOperationException("Confirm permanent local conversation deletion first.");
        return UpdateAsync(state => state with { Conversations = state.Conversations.RemoveAll(c => c.Id == id) }, cancellationToken);
    }
    public static string ExportRedacted(OllamaConversation conversation) => JsonSerializer.Serialize(new { conversation.Id, conversation.UpdatedAt, MessageCount = conversation.Messages.Length, Content = "Omitted: local model content requires explicit review before sharing." });
}

/// <summary>Serial durable pulls. Interrupted requests are reconciled against installed tags on the next run.</summary>
public sealed class OllamaPullQueue(OllamaClient client, OllamaStateStore store)
{
    private readonly SemaphoreSlim runner = new(1, 1);
    public Task<OllamaLocalState> EnqueueAsync(string model, CancellationToken cancellationToken = default)
    {
        model = OllamaClient.ValidateModel(model);
        return store.UpdateAsync(state => state.Pulls.Any(p => p.Model == model && p.State is OllamaPullState.Queued or OllamaPullState.Pulling)
            ? state : state with { Pulls = state.Pulls.Add(new(Guid.NewGuid(), model, OllamaPullState.Queued)) }, cancellationToken);
    }
    public Task<OllamaLocalState> RetryAsync(Guid id, CancellationToken cancellationToken = default) => store.UpdateAsync(state => state with
    { Pulls = state.Pulls.Select(p => p.Id == id && p.State is OllamaPullState.Failed or OllamaPullState.Cancelled or OllamaPullState.Interrupted ? p with { State = OllamaPullState.Queued, Detail = null } : p).ToImmutableArray() }, cancellationToken);
    public Task RunAsync(IProgress<OllamaPullItem>? progress = null, CancellationToken cancellationToken = default) => RunAsync(1, progress, cancellationToken);
    public async Task RunAsync(int maximumParallelism, IProgress<OllamaPullItem>? progress = null, CancellationToken cancellationToken = default)
    {
        if (maximumParallelism is < 1 or > 4) throw new ArgumentOutOfRangeException(nameof(maximumParallelism));
        await runner.WaitAsync(cancellationToken);
        try
        {
            var models = await client.GetModelsAsync(cancellationToken);
            await store.UpdateAsync(state => state with { Pulls = state.Pulls.Select(p => p.State == OllamaPullState.Pulling ? p with { State = OllamaPullState.Interrupted, Detail = "Previous operation ended before its outcome was recorded. Retry to reconcile." } : p).ToImmutableArray() }, cancellationToken);
            var state = await store.LoadAsync(cancellationToken);
            await Parallel.ForEachAsync(state.Pulls.Where(p => p.State == OllamaPullState.Queued), new ParallelOptions { MaxDegreeOfParallelism = maximumParallelism, CancellationToken = cancellationToken }, async (pending, itemToken) => {
                cancellationToken.ThrowIfCancellationRequested();
                var item = pending;
                try
                {
                    if (models.Any(m => m.Name == item.Model)) item = item with { State = OllamaPullState.Skipped, Detail = "Exact tag is already installed; no network request made." };
                    else
                    {
                        item = item with { State = OllamaPullState.Pulling };
                        await SaveAsync(item, cancellationToken);
                        await foreach (var update in client.PullAsync(item.Model, cancellationToken))
                        {
                            item = item with { CompletedBytes = update.CompletedBytes, TotalBytes = update.TotalBytes, Detail = update.Status };
                            progress?.Report(item);
                        }
                        // A completed stream alone is insufficient: verify the exact installed tag.
                        var verified = await client.GetModelsAsync(itemToken);
                        item = item with { State = verified.Any(m => m.Name == item.Model) ? OllamaPullState.Pulled : OllamaPullState.Failed, Detail = verified.Any(m => m.Name == item.Model) ? "Exact installed tag verified." : "Pull ended but exact installed tag was not found." };
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    item = item with { State = OllamaPullState.Cancelled, Detail = "Request cancelled. Existing models were retained; retry reconciles installed state." };
                    await SaveAsync(item, CancellationToken.None); progress?.Report(item); throw;
                }
                catch (Exception exception) when (exception is HttpRequestException or InvalidDataException or OperationCanceledException)
                { item = item with { State = OllamaPullState.Failed, Detail = "Local pull failed or timed out. Inspect runtime health, storage and connectivity before retrying." }; }
                await SaveAsync(item, cancellationToken);
                progress?.Report(item);
            });
        }
        finally { runner.Release(); }
    }
    private Task<OllamaLocalState> SaveAsync(OllamaPullItem item, CancellationToken cancellationToken) => store.UpdateAsync(state => state with { Pulls = state.Pulls.Select(p => p.Id == item.Id ? item : p).ToImmutableArray() }, cancellationToken);
}
