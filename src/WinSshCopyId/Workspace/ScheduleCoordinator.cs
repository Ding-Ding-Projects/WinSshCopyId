namespace WinSshCopyId.Workspace;

public sealed record ScheduledSettingsState(WorkspaceSettings Effective, long Generation, DateTimeOffset EvaluatedAt,
    IReadOnlyList<Guid> AppliedRules, IReadOnlyList<Guid> UnavailableRules);

/// <summary>Owns temporary effective settings. It never persists external overrides as base settings.</summary>
public sealed class ScheduleCoordinator : IAsyncDisposable
{
    private readonly ExternalSettingsSource sources;
    private readonly IClock clock;
    private readonly object sync = new();
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? refresh;
    private Task? periodic;
    private Task? current;
    private long generation;
    private bool disposed;
    public ScheduledSettingsState? Current { get; private set; }
    public event EventHandler<ScheduledSettingsState>? Changed;
    public ScheduleCoordinator(ExternalSettingsSource sources, IClock clock) { this.sources = sources; this.clock = clock; }
    public Task<ScheduledSettingsState?> RefreshAsync(WorkspaceSettings basis, ScheduleDocument document, CancellationToken cancellationToken = default)
    {
        document.Validate(); basis.Validate();
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            refresh?.Cancel();
            var tokenSource = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, cancellationToken);
            tokenSource.CancelAfter(TimeSpan.FromSeconds(15));
            refresh = tokenSource;
            var task = EvaluateAsync(basis, document, ++generation, tokenSource);
            current = task; return task;
        }
    }
    private async Task<ScheduledSettingsState?> EvaluateAsync(WorkspaceSettings basis, ScheduleDocument document, long ticket, CancellationTokenSource cancellation)
    {
        var effective = basis; var applied = new List<Guid>(); var unavailable = new List<Guid>();
        var now = clock.UtcNow; var zone = TimeZoneInfo.FindSystemTimeZoneById(document.TimeZoneId);
        try
        {
            foreach (var rule in document.Rules)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                if (!rule.Matches(now, zone)) continue;
                try
                {
                    var values = rule.Source is null || rule.Source.Kind == ScheduleSourceKind.Local ? rule.Values : await sources.FetchAsync(rule.Source, rule.Values, cancellation.Token);
                    cancellation.Token.ThrowIfCancellationRequested();
                    if (values is null) continue;
                    effective = values.Apply(effective); applied.Add(rule.Id);
                }
                catch (Exception e) when (e is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException or System.Collections.Generic.KeyNotFoundException)
                { unavailable.Add(rule.Id); }
            }
            lock (sync)
            {
                if (disposed || ticket != generation || cancellation.IsCancellationRequested) return null;
                Current = new(effective, ticket, now, applied, unavailable);
                Changed?.Invoke(this, Current); return Current;
            }
        }
        catch (OperationCanceledException)
        {
            lock (sync)
            {
                if (disposed || ticket != generation || lifetime.IsCancellationRequested) return null;
                // A current refresh that times out exposes base settings, never an old external override.
                Current = new(basis, ticket, now, [], document.Rules.Where(r => r.Matches(now, zone) && r.Source is { Kind: not ScheduleSourceKind.Local }).Select(r => r.Id).ToArray());
                Changed?.Invoke(this, Current); return Current;
            }
        }
        finally { lock(sync) { if (ReferenceEquals(refresh,cancellation)) refresh=null; } cancellation.Dispose(); }
    }
    public void Start(Func<WorkspaceSettings> baseSettings, Func<ScheduleDocument> schedules, TimeSpan? interval = null)
    {
        var period = interval ?? TimeSpan.FromMinutes(5);
        if (period < TimeSpan.FromSeconds(30) || period > TimeSpan.FromHours(1)) throw new ArgumentOutOfRangeException(nameof(interval));
        lock(sync)
        {
            ObjectDisposedException.ThrowIf(disposed,this);
            if (periodic is not null) throw new InvalidOperationException("Scheduled refresh is already active.");
            periodic = RunAsync(baseSettings,schedules,period);
        }
    }
    private async Task RunAsync(Func<WorkspaceSettings> basis, Func<ScheduleDocument> document, TimeSpan period)
    {
        using var timer = new PeriodicTimer(period);
        try
        {
            do { await RefreshAsync(basis(),document(),lifetime.Token); } while(await timer.WaitForNextTickAsync(lifetime.Token));
        }
        catch(OperationCanceledException) { }
    }
    public async ValueTask DisposeAsync()
    {
        Task? running; Task? loop;
        lock(sync) { if(disposed)return; disposed=true; lifetime.Cancel();refresh?.Cancel();running=current;loop=periodic; }
        if(loop is not null) await loop;
        if(running is not null) await running;
        lifetime.Dispose();
    }
}
