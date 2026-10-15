using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WinSshCopyId.Workspace;

public sealed record SettingsOverride(WorkspaceLanguage? Language = null, WorkspaceTheme? Theme = null,
    WorkspaceDensity? Density = null, string? SeedColor = null, string? FontFamily = null,
    double? FontSize = null, bool? ReducedMotion = null, string? DisplayName = null,
    int? EnglishFunnyLevel = null, int? CantoneseFunnyLevel = null, bool? ShowDialogEmojis = null,
    bool? AccessibilityButtonVisible = null, AttentionPreferences? Attention = null, NarratorPreferences? Narrator = null)
{
    public WorkspaceSettings Apply(WorkspaceSettings basis) => (basis with
    {
        Language = Language ?? basis.Language, Theme = Theme ?? basis.Theme, Density = Density ?? basis.Density,
        SeedColor = SeedColor ?? basis.SeedColor, FontFamily = FontFamily ?? basis.FontFamily,
        FontSize = FontSize ?? basis.FontSize, ReducedMotion = ReducedMotion ?? basis.ReducedMotion,
        DisplayName = DisplayName ?? basis.DisplayName, EnglishFunnyLevel = EnglishFunnyLevel ?? basis.EnglishFunnyLevel,
        CantoneseFunnyLevel = CantoneseFunnyLevel ?? basis.CantoneseFunnyLevel, ShowDialogEmojis = ShowDialogEmojis ?? basis.ShowDialogEmojis,
        AccessibilityButtonVisible = AccessibilityButtonVisible ?? basis.AccessibilityButtonVisible,
        Attention = Attention ?? basis.Attention, Narrator = Narrator ?? basis.Narrator
    }).Validate();
}
public enum ScheduleSourceKind { Local, Https, HomeAssistant }
public sealed record ScheduleSource(ScheduleSourceKind Kind, string? Url = null, string? EntityId = null, string? CredentialKey = null);
public sealed record SettingsSchedule(Guid Id, string Label, bool Enabled, TimeOnly StartTime, TimeOnly EndTime,
    HashSet<DayOfWeek> Weekdays, SettingsOverride Values, DateOnly? StartDate = null,
    DateOnly? EndDate = null, ScheduleSource? Source = null)
{
    public bool Matches(DateTimeOffset now, TimeZoneInfo timezone)
    {
        if (!Enabled) return false;
        var local = TimeZoneInfo.ConvertTime(now, timezone);
        var date = DateOnly.FromDateTime(local.DateTime);
        var time = TimeOnly.FromDateTime(local.DateTime);
        // The after-midnight part belongs to the previous starting day.
        if (StartTime > EndTime && time < EndTime) date = date.AddDays(-1);
        if (StartDate is { } start && date < start || EndDate is { } end && date > end ||
            Weekdays.Count > 0 && !Weekdays.Contains(date.DayOfWeek)) return false;
        return StartTime == EndTime || (StartTime < EndTime ? time >= StartTime && time < EndTime : time >= StartTime || time < EndTime);
    }
}
public sealed record ScheduleDocument(int Version, string TimeZoneId, IReadOnlyList<SettingsSchedule> Rules)
{
    public void Validate()
    {
        if (Version != 1 || Rules.Count > 100 || Rules.Select(r => r.Id).Distinct().Count() != Rules.Count) throw new InvalidDataException("Unsupported schedule.");
        _ = TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId);
        foreach (var rule in Rules)
        {
            if (rule.Label.Length is < 1 or > 120 || rule.StartDate > rule.EndDate || rule.Weekdays.Any(d => !Enum.IsDefined(d))) throw new InvalidDataException("Invalid schedule rule.");
            _ = rule.Values.Apply(new());
        }
    }
    public WorkspaceSettings Evaluate(WorkspaceSettings basis, DateTimeOffset now)
    {
        Validate(); var zone = TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId);
        foreach (var rule in Rules.Where(r => r.Matches(now, zone) && (r.Source is null || r.Source.Kind == ScheduleSourceKind.Local))) basis = rule.Values.Apply(basis);
        return basis;
    }
}
public sealed class ScheduleStore(IPrivateStorage storage)
{
    public async Task SaveAsync(ScheduleDocument document, CancellationToken cancellationToken = default)
    { document.Validate(); await storage.WriteAtomicAsync("schedules.json", JsonSerializer.SerializeToUtf8Bytes(document, WorkspaceSettingsStore.JsonOptions), cancellationToken); }
    public async Task<ScheduleDocument?> LoadAsync(CancellationToken cancellationToken = default)
    {
        var bytes = await storage.ReadAsync("schedules.json", cancellationToken);
        if (bytes is null) return null;
        var document = JsonSerializer.Deserialize<ScheduleDocument>(bytes, WorkspaceSettingsStore.JsonOptions) ?? throw new InvalidDataException();
        document.Validate(); return document;
    }
}
public interface ISettingsSourceTransport
{
    Task<byte[]> GetAsync(Uri uri, string? bearerCredential, CancellationToken cancellationToken);
}
public sealed record PrivateSettingsTarget(string Authority, IReadOnlyList<string> Addresses);
public interface ITargetBoundSettingsSourceTransport : ISettingsSourceTransport
{
    Task<byte[]> GetPrivateAsync(Uri uri, PrivateSettingsTarget approvedTarget, string? bearerCredential, CancellationToken cancellationToken);
}
public sealed class HttpsSettingsTransport : ITargetBoundSettingsSourceTransport
{
    public Task<byte[]> GetAsync(Uri uri, string? bearerCredential, CancellationToken cancellationToken) => SendAsync(uri, null, bearerCredential, cancellationToken);
    public Task<byte[]> GetPrivateAsync(Uri uri, PrivateSettingsTarget approvedTarget, string? bearerCredential, CancellationToken cancellationToken) => SendAsync(uri, approvedTarget, bearerCredential, cancellationToken);
    private async Task<byte[]> SendAsync(Uri uri, PrivateSettingsTarget? approvedTarget, string? bearerCredential, CancellationToken cancellationToken)
    {
        ExternalSettingsSource.ValidateUri(uri, approvedTarget);
        // Resolve once and pin the validated address to prevent DNS rebinding between checks and connection.
        var addresses = await Dns.GetHostAddressesAsync(uri.DnsSafeHost, cancellationToken);
        var approvedAddresses = approvedTarget?.Addresses.Select(IPAddress.Parse).ToArray();
        if (addresses.Length == 0 || addresses.Any(a => approvedAddresses is null ? !ExternalSettingsSource.IsPublicAddress(a) : !approvedAddresses.Contains(a)))
            throw new InvalidDataException("The source address is not approved.");
        using var handler = new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, UseProxy = false, Credentials = null };
        handler.ConnectCallback = async (context, cancel) =>
        {
            var socket = new System.Net.Sockets.Socket(System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
            try { await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, cancel); return new System.Net.Sockets.NetworkStream(socket, true); }
            catch { socket.Dispose(); throw; }
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (bearerCredential is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerCredential);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > 65536) throw new InvalidDataException("The response exceeds its size limit.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var memory = new MemoryStream();
        var buffer = new byte[4096]; int count;
        while ((count = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        { if (memory.Length + count > 65536) throw new InvalidDataException("The response exceeds its size limit."); await memory.WriteAsync(buffer.AsMemory(0, count), cancellationToken); }
        return memory.ToArray();
    }
}
public sealed class ExternalSettingsSource(ISettingsSourceTransport transport, Func<string, string?> getCredential, IReadOnlyList<PrivateSettingsTarget>? approvedHomeAssistantTargets = null)
{
    public static void ValidateUri(Uri uri, PrivateSettingsTarget? approvedTarget = null)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme != "https" || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 || uri.Query.Length != 0 || uri.HostNameType == UriHostNameType.Unknown)
            throw new InvalidDataException("A public HTTPS source without embedded credentials is required.");
        if (approvedTarget is not null)
        {
            if (!string.Equals(uri.Authority, approvedTarget.Authority, StringComparison.OrdinalIgnoreCase) || approvedTarget.Addresses.Count is < 1 or > 16 ||
                approvedTarget.Addresses.Any(value => !IPAddress.TryParse(value, out var address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || address.IsIPv6Multicast || address.GetAddressBytes()[0] >= 224 && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork))
                throw new InvalidDataException("The explicit target allowlist is invalid.");
            return;
        }
        if (uri.IsLoopback || IPAddress.TryParse(uri.Host, out var ip) && !IsPublicAddress(ip)) throw new InvalidDataException("Private network sources require an explicit target allowlist.");
    }
    public static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return false;
        var b = address.GetAddressBytes();
        if (b.Length == 16) return (b[0] & 0xe0) == 0x20 && !(b[0] == 0x20 && b[1] == 1 && b[2] == 0x0d && b[3] == 0xb8);
        return b[0] is not (0 or 10 or 127) && b[0] < 224 && !(b[0] == 169 && b[1] == 254) && !(b[0] == 172 && b[1] is >= 16 and <= 31) && !(b[0] == 192 && b[1] == 168) && !(b[0] == 100 && b[1] is >= 64 and <= 127);
    }
    public async Task<SettingsOverride?> FetchAsync(ScheduleSource source, SettingsOverride homeAssistantValues, CancellationToken cancellationToken = default)
    {
        if (source.Kind == ScheduleSourceKind.Local) return homeAssistantValues;
        if (!Enum.IsDefined(source.Kind)) throw new InvalidDataException("Unsupported source kind.");
        if (!Uri.TryCreate(source.Url, UriKind.Absolute, out var uri)) throw new InvalidDataException("Invalid source URL.");
        var approved = source.Kind == ScheduleSourceKind.HomeAssistant ? approvedHomeAssistantTargets?.SingleOrDefault(t => string.Equals(t.Authority, uri.Authority, StringComparison.OrdinalIgnoreCase)) : null;
        ValidateUri(uri, approved);
        string? credential = null;
        if (source.Kind == ScheduleSourceKind.HomeAssistant)
        {
            if (source.EntityId is null || !Regex.IsMatch(source.EntityId, "^(binary_sensor|input_boolean)\\.[a-z0-9_]{1,128}$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)) || string.IsNullOrWhiteSpace(source.CredentialKey))
                throw new InvalidDataException("A boolean entity and credential reference are required.");
            credential = getCredential(source.CredentialKey) ?? throw new InvalidOperationException("The source credential is unavailable.");
            uri = new Uri(uri.GetLeftPart(UriPartial.Authority) + "/api/states/" + source.EntityId);
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var bytes = approved is not null
            ? await (transport as ITargetBoundSettingsSourceTransport ?? throw new InvalidOperationException("The transport cannot enforce the approved target.")).GetPrivateAsync(uri, approved, credential, timeout.Token)
            : await transport.GetAsync(uri, credential, timeout.Token);
        if (bytes.Length > 65536) throw new InvalidDataException("The response exceeds its size limit.");
        if (source.Kind == ScheduleSourceKind.HomeAssistant)
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
            var state = document.RootElement.GetProperty("state").GetString();
            return state switch { "on" => homeAssistantValues, "off" => null, _ => throw new InvalidDataException("The boolean source state is unavailable.") };
        }
        var response = JsonSerializer.Deserialize<ExternalSettingsResponse>(bytes, WorkspaceSettingsStore.JsonOptions) ?? throw new InvalidDataException();
        if (response.Version != 1) throw new InvalidDataException("Unsupported source schema.");
        _ = response.Values.Apply(new()); return response.Values;
    }
    private sealed record ExternalSettingsResponse(int Version, SettingsOverride Values);
}

