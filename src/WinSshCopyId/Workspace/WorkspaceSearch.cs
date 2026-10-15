using System.Text.RegularExpressions;

namespace WinSshCopyId.Workspace;

public sealed record SearchQuery(string Text = "", bool UseRegex = false, bool IgnoreCase = true,
    bool Multiline = false, bool Singleline = false, bool IgnorePatternWhitespace = false);
public sealed record SearchCapture(string Group, string Value, int Index, int Length);
public sealed record SearchMatch(string Value, int Index, int Length, IReadOnlyList<SearchCapture> Captures);
public sealed record SearchResult(IReadOnlyList<SearchMatch> Matches, string? ValidationError,
    bool Limited = false, string? ReplacementPreview = null);
public sealed record SavedSearchSnippet(Guid Id, string Name, SearchQuery Query, string Replacement = "");
public sealed class WorkspaceSearch
{
    public const int MaxInputLength = 262144, MaxPatternLength = 4096, MaxMatches = 1000;
    public static string EngineDescription => ".NET regular expressions with a 100 ms execution timeout. Supports groups, captures, lookarounds, backreferences, Unicode classes, anchors, quantifiers and replacement groups.";
    public SearchResult Evaluate(string input, SearchQuery query, string? replacement = null)
    {
        if (input.Length > MaxInputLength || query.Text.Length > MaxPatternLength || replacement?.Length > MaxPatternLength)
            return new([], "Input, pattern or replacement exceeds its supported limit.");
        if (query.Text.Length == 0) return new([], null, ReplacementPreview: replacement is null ? null : input);
        try
        {
            var options = RegexOptions.CultureInvariant;
            if (query.IgnoreCase) options |= RegexOptions.IgnoreCase;
            if (query.Multiline) options |= RegexOptions.Multiline;
            if (query.Singleline) options |= RegexOptions.Singleline;
            if (query.IgnorePatternWhitespace && query.UseRegex) options |= RegexOptions.IgnorePatternWhitespace;
            var regex = new Regex(query.UseRegex ? query.Text : Regex.Escape(query.Text), options, TimeSpan.FromMilliseconds(100));
            var result = new List<SearchMatch>();
            var engineMatches = new List<Match>();
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            var limited = false;
            foreach (Match match in regex.Matches(input))
            {
                if (elapsed.ElapsedMilliseconds > 250) return new([], "The search exceeded the total work limit.");
                if (result.Count == MaxMatches) { limited = true; break; }
                var captures = new List<SearchCapture>();
                foreach (var name in regex.GetGroupNames())
                    foreach (Capture capture in match.Groups[name].Captures)
                        captures.Add(new(name, capture.Value, capture.Index, capture.Length));
                result.Add(new(match.Value, match.Index, match.Length, captures));
                engineMatches.Add(match);
            }
            string? preview = null;
            if (replacement is not null)
            {
                // A match evaluator limits the replacement output before a large expansion can accumulate.
                var output = new System.Text.StringBuilder();
                var cursor = 0;
                foreach (var match in engineMatches)
                {
                    if (elapsed.ElapsedMilliseconds > 250) return new([], "The search exceeded the total work limit.");
                    output.Append(input, cursor, match.Index - cursor);
                    output.Append(query.UseRegex ? match.Result(replacement) : replacement);
                    cursor = match.Index + match.Length;
                    if (output.Length > MaxInputLength) return new(result, "Replacement exceeds the output limit.", limited);
                }
                output.Append(input, cursor, input.Length - cursor);
                if (output.Length > MaxInputLength) return new(result, "Replacement exceeds the output limit.", limited);
                preview = output.ToString();
            }
            return new(result, null, limited, preview);
        }
        catch (RegexMatchTimeoutException) { return new([], "The expression exceeded the 100 ms execution limit."); }
        catch (ArgumentException) { return new([], "The expression or replacement is invalid."); }
    }
    public IEnumerable<T> Filter<T>(IEnumerable<T> source, Func<T, string> text, SearchQuery query)
        => source.Where(item => query.Text.Length == 0 || Evaluate(text(item), query).Matches.Count > 0);
}

public sealed record WorkspaceTab(Guid Id, string Title, string Route, Guid? GroupId = null, bool Locked = false);
public sealed record WorkspaceTabGroup(Guid Id, string Name, string Color = "#0061A4", bool Collapsed = false);
public sealed record WorkspaceSession
{
    public int Version { get; init; } = 1;
    public List<WorkspaceTab> Tabs { get; init; } = [];
    public List<WorkspaceTabGroup> Groups { get; init; } = [];
    public Guid? ActiveTabId { get; init; }
    public List<SavedSearchSnippet> Snippets { get; init; } = [];
    public WorkspaceSession Validate()
    {
        if (Version != 1 || Tabs.Count > 100 || Groups.Count > 100 || Snippets.Count > 100 ||
            Tabs.Select(t => t.Id).Distinct().Count() != Tabs.Count || Groups.Select(g => g.Id).Distinct().Count() != Groups.Count ||
            Tabs.Any(t => t.Title.Length is < 1 or > 120 || t.Route.Length is < 1 or > 200 || t.GroupId is { } id && !Groups.Any(g => g.Id == id)) ||
            ActiveTabId is { } active && !Tabs.Any(t => t.Id == active)) throw new InvalidDataException("Workspace session is invalid.");
        return this;
    }
    public WorkspaceSession Open(string title, string route, Guid? groupId = null)
    {
        var tab = new WorkspaceTab(Guid.NewGuid(), title, route, groupId);
        return (this with { Tabs = [.. Tabs, tab], ActiveTabId = tab.Id }).Validate();
    }
    public WorkspaceSession Close(Guid id)
    {
        if (Tabs.Any(t => t.Id == id && t.Locked)) throw new InvalidOperationException("Unlock the tab before closing it.");
        var remaining = Tabs.Where(t => t.Id != id).ToList();
        return this with { Tabs = remaining, ActiveTabId = ActiveTabId == id ? remaining.LastOrDefault()?.Id : ActiveTabId };
    }
    public WorkspaceSession Move(Guid id, int index, Guid? groupId)
    {
        var tab = Tabs.Single(t => t.Id == id);
        var list = Tabs.Where(t => t.Id != id).ToList();
        if (index < 0 || index > list.Count) throw new ArgumentOutOfRangeException(nameof(index));
        list.Insert(index, tab with { GroupId = groupId });
        return (this with { Tabs = list }).Validate();
    }
}

public sealed class WorkspaceSessionStore(IPrivateStorage storage)
{
    public async Task SaveAsync(WorkspaceSession session, CancellationToken cancellationToken = default) =>
        await storage.WriteAtomicAsync("workspace.json", System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(session.Validate(), WorkspaceSettingsStore.JsonOptions), cancellationToken);
    public async Task<WorkspaceSession> LoadAsync(CancellationToken cancellationToken = default)
    {
        var bytes = await storage.ReadAsync("workspace.json", cancellationToken);
        if (bytes is null) return new();
        return (System.Text.Json.JsonSerializer.Deserialize<WorkspaceSession>(bytes, WorkspaceSettingsStore.JsonOptions) ?? throw new InvalidDataException()).Validate();
    }
}
