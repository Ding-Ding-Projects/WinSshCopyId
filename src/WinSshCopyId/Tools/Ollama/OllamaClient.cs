using System.Collections.Immutable;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace WinSshCopyId.Tools.Ollama;

public sealed record OllamaHealth(bool Reachable, string? Version, string Status, string NextAction);
public sealed record OllamaModel(string Name, long? Size, string? Digest, string? Family, string? ParameterSize, string? Quantization);
public sealed record OllamaMessage(string Role, string Content);
public sealed record OllamaGenerationOptions(double Temperature = 0.7, int ContextLength = 4096, int MaximumTokens = 512)
{
    internal object Validated()
    {
        if (!double.IsFinite(Temperature) || Temperature is < 0 or > 2 || ContextLength is < 256 or > 131072 || MaximumTokens is < 1 or > 32768)
            throw new ArgumentOutOfRangeException(nameof(Temperature), "Generation settings exceed local limits.");
        return new { temperature = Temperature, num_ctx = ContextLength, num_predict = MaximumTokens };
    }
}
public sealed record OllamaStreamUpdate(string Status, string Text, bool Done, long? CompletedBytes, long? TotalBytes, string? Digest);

/// <summary>Fixed local API boundary. Never follows redirects or uses a cloud endpoint.</summary>
public sealed class OllamaClient : IDisposable
{
    public static readonly Uri Endpoint = new("http://127.0.0.1:11434/");
    private const int MaximumResponseBytes = 8 * 1024 * 1024;
    private const int MaximumLineBytes = 1024 * 1024;
    private readonly HttpClient client;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public OllamaClient()
    {
        client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(3) })
        { BaseAddress = Endpoint, Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task<OllamaHealth> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await RequestAsync(HttpMethod.Get, "api/version", null, cancellationToken);
            var version = GetString(result, "version");
            return version is null
                ? new(false, null, "Unhealthy API", "Retry health verification; the local response did not contain a version.")
                : new(true, version, "Healthy", "Select an installed model or review the pull queue.");
        }
        catch (HttpRequestException) { return new(false, null, "Local API unavailable", "Check the installation record, start the verified local runtime, then retry. Missing and stopped cannot be distinguished from HTTP alone."); }
        catch (InvalidDataException) { return new(false, null, "Unhealthy API", "The local API returned invalid or oversized data. Stop this operation and inspect the runtime."); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return new(false, null, "Health timeout", "Inspect the local runtime and retry health verification."); }
    }

    public Task<ImmutableArray<OllamaModel>> GetModelsAsync(CancellationToken cancellationToken = default) => ModelsAsync("api/tags", cancellationToken);
    public Task<ImmutableArray<OllamaModel>> GetRunningModelsAsync(CancellationToken cancellationToken = default) => ModelsAsync("api/ps", cancellationToken);
    private async Task<ImmutableArray<OllamaModel>> ModelsAsync(string path, CancellationToken cancellationToken)
    {
        var root = await RequestAsync(HttpMethod.Get, path, null, cancellationToken);
        if (!root.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Missing model inventory.");
        if (models.GetArrayLength() > 10000) throw new InvalidDataException("Model inventory exceeds local limit.");
        return models.EnumerateArray().Select(model =>
        {
            var details = model.TryGetProperty("details", out var d) && d.ValueKind == JsonValueKind.Object ? d : default;
            return new OllamaModel(GetString(model, "name") ?? GetString(model, "model") ?? throw new InvalidDataException("Missing model name."),
                GetLong(model, "size"), GetString(model, "digest"), GetString(details, "family"), GetString(details, "parameter_size"), GetString(details, "quantization_level"));
        }).ToImmutableArray();
    }

    public Task<JsonElement> ShowAsync(string model, CancellationToken cancellationToken = default) => RequestAsync(HttpMethod.Post, "api/show", new { model = ValidateModel(model) }, cancellationToken);
    public async Task DeleteAsync(string model, CancellationToken cancellationToken = default) => _ = await RequestAsync(HttpMethod.Delete, "api/delete", new { model = ValidateModel(model) }, cancellationToken);
    public async Task CopyAsync(string source, string destination, CancellationToken cancellationToken = default) => _ = await RequestAsync(HttpMethod.Post, "api/copy", new { source = ValidateModel(source), destination = ValidateModel(destination) }, cancellationToken);
    public IAsyncEnumerable<OllamaStreamUpdate> PullAsync(string model, CancellationToken cancellationToken = default) => StreamAsync("api/pull", new { model = ValidateModel(model), stream = true }, TimeSpan.FromHours(1), cancellationToken);
    public IAsyncEnumerable<OllamaStreamUpdate> GenerateAsync(string model, string prompt, OllamaGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        ValidateText(prompt, 65536);
        return StreamAsync("api/generate", new { model = ValidateModel(model), prompt, stream = true, options = (options ?? new()).Validated() }, TimeSpan.FromMinutes(10), cancellationToken);
    }
    public IAsyncEnumerable<OllamaStreamUpdate> ChatAsync(string model, IEnumerable<OllamaMessage> messages, OllamaGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        var bounded = messages.Take(257).ToImmutableArray();
        if (bounded.Length is 0 or > 256 || bounded.Sum(m => (long)m.Content.Length) > 262144) throw new ArgumentException("Chat history exceeds local limits.");
        foreach (var message in bounded)
        {
            if (message.Role is not ("system" or "user" or "assistant")) throw new ArgumentException("Unsupported chat role.");
            ValidateText(message.Content, 65536);
        }
        return StreamAsync("api/chat", new { model = ValidateModel(model), messages = bounded, stream = true, options = (options ?? new()).Validated() }, TimeSpan.FromMinutes(10), cancellationToken);
    }
    private async Task<JsonElement> RequestAsync(HttpMethod method, string path, object? payload, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var request = new HttpRequestMessage(method, path) { Content = payload is null ? null : JsonContent.Create(payload, options: JsonOptions) };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        EnsureSuccess(response);
        using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await input.ReadAsync(buffer, timeout.Token)) != 0)
        {
            if (output.Length + read > MaximumResponseBytes) throw new InvalidDataException("Local API response exceeds size limit.");
            output.Write(buffer, 0, read);
        }
        if (output.Length == 0) return JsonSerializer.SerializeToElement(new { });
        try { using var document = JsonDocument.Parse(output.ToArray()); return document.RootElement.Clone(); }
        catch (JsonException exception) { throw new InvalidDataException("Local API response is not valid JSON.", exception); }
    }
    private async IAsyncEnumerable<OllamaStreamUpdate> StreamAsync(string path, object payload, TimeSpan limit, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(limit);
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(payload, options: JsonOptions) };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        EnsureSuccess(response);
        using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
        var chunk = new byte[8192];
        using var line = new MemoryStream();
        long total = 0;
        bool completed = false;
        int read;
        while ((read = await input.ReadAsync(chunk, timeout.Token)) > 0)
        {
            total += read;
            if (total > 64 * 1024 * 1024) throw new InvalidDataException("Streaming response exceeds local limit.");
            for (int index = 0; index < read; index++)
            {
                if (chunk[index] == (byte)'\n')
                {
                    if (line.Length == 0) continue;
                    var update = ParseUpdate(line.ToArray());
                    line.SetLength(0);
                    completed |= update.Done;
                    yield return update;
                }
                else
                {
                    if (line.Length >= MaximumLineBytes) throw new InvalidDataException("Streaming record exceeds local limit.");
                    line.WriteByte(chunk[index]);
                }
            }
        }
        if (line.Length > 0) { var update = ParseUpdate(line.ToArray()); completed |= update.Done; yield return update; }
        if (!completed) throw new InvalidDataException("Local stream ended without a completion record.");
    }
    private static OllamaStreamUpdate ParseUpdate(byte[] bytes)
    {
        JsonElement root;
        try { using var document = JsonDocument.Parse(bytes); root = document.RootElement.Clone(); }
        catch (JsonException exception) { throw new InvalidDataException("Invalid streaming JSON.", exception); }
        if (root.TryGetProperty("error", out _)) throw new InvalidDataException("The local model operation reported an error. Inspect the runtime and selected model.");
        string status = GetString(root, "status") ?? "Generating";
        string text = GetString(root, "response") ?? (root.TryGetProperty("message", out var message) ? GetString(message, "content") : null) ?? "";
        return new(status, text, (root.TryGetProperty("done", out var done) && done.ValueKind == JsonValueKind.True) || status == "success", GetLong(root, "completed"), GetLong(root, "total"), GetString(root, "digest"));
    }
    private static void EnsureSuccess(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"Local Ollama API returned HTTP {(int)response.StatusCode}.", null, response.StatusCode);
    }
    public static string ValidateModel(string model)
    {
        if (string.IsNullOrWhiteSpace(model) || model.Length > 200 || !Regex.IsMatch(model, @"\A[a-zA-Z0-9][a-zA-Z0-9._:/-]*\z", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)) || model.Contains("..", StringComparison.Ordinal))
            throw new ArgumentException("Select a valid exact model tag.", nameof(model));
        return model;
    }
    private static void ValidateText(string text, int maximum) { if (text is null || text.Length > maximum) throw new ArgumentException("Text exceeds local limit."); }
    internal static string? GetString(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    internal static long? GetLong(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number) && number >= 0 ? number : null;
    public void Dispose() => client.Dispose();
}

