using System.Collections.Immutable;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace WinSshCopyId.Tools.Ollama;

public sealed record OllamaCatalogRefresh(OllamaCatalogSnapshot Snapshot, bool Online, ImmutableArray<string> Warnings);
/// <summary>Traverses official HTML links only. HTML discovery is reported partial until official inventory counts can be independently verified.</summary>
public sealed class OllamaCatalogClient : IDisposable
{
    private readonly HttpClient http = new(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
    private static readonly Regex Links = new("(?:href|hx-get)=[\"'](?<url>[^\"']+)[\"']", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    public async Task<OllamaCatalogRefresh> RefreshAsync(OllamaCatalogSnapshot previous, CancellationToken cancellationToken = default)
    {
        var pending = new Queue<Uri>(); pending.Enqueue(new("https://ollama.com/library"));
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var families = new HashSet<string>(StringComparer.Ordinal);
        var variants = new Dictionary<string, OllamaModel>(StringComparer.Ordinal);
        var warnings = ImmutableArray.CreateBuilder<string>();
        using var identity = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        try
        {
            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var uri = pending.Dequeue();
                if (!visited.Add(uri.AbsoluteUri)) continue;
                if (visited.Count > 5000) throw new InvalidDataException("Official catalog exceeded the 5000-page acquisition limit.");
                using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();
                using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var bytes = new MemoryStream();
                var buffer = new byte[8192]; int read;
                while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    if (bytes.Length + read > 8 * 1024 * 1024) throw new InvalidDataException("Official catalog page exceeds local limit.");
                    bytes.Write(buffer, 0, read);
                }
                var raw = bytes.ToArray(); identity.AppendData(Encoding.UTF8.GetBytes(uri.AbsoluteUri)); identity.AppendData(raw);
                string html = Encoding.UTF8.GetString(raw);
                int recognized = 0;
                foreach (Match match in Links.Matches(html))
                {
                    string link = WebUtility.HtmlDecode(match.Groups["url"].Value);
                    if (!Uri.TryCreate(uri, link, out var target) || target.Scheme != "https" || target.Host != "ollama.com" || !target.IsDefaultPort || target.Fragment.Length > 0) continue;
                    string path = Uri.UnescapeDataString(target.AbsolutePath);
                    var family = Regex.Match(path, @"\A/library/(?<family>[a-zA-Z0-9._-]+)\z", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
                    var tag = Regex.Match(path, @"\A/library/(?<tag>[a-zA-Z0-9._-]+:[a-zA-Z0-9._-]+)\z", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
                    if (family.Success)
                    {
                        recognized++;
                        if (families.Add(family.Groups["family"].Value)) pending.Enqueue(new("https://ollama.com" + path + "/tags"));
                    }
                    else if (tag.Success)
                    {
                        recognized++;
                        string name = OllamaClient.ValidateModel(tag.Groups["tag"].Value);
                        variants.TryAdd(name, new(name, null, null, null, null, null));
                    }
                    else if ((path == "/library" || (path.StartsWith("/library/", StringComparison.Ordinal) && path.EndsWith("/tags", StringComparison.Ordinal))) && target.Query.Length > 0)
                    {
                        // Follow advertised pagination and continuation links rather than guessing page numbers.
                        if (!visited.Contains(target.AbsoluteUri)) pending.Enqueue(target);
                    }
                }
                if (recognized == 0) warnings.Add("No family or exact tag links recognized at an official catalog page; completeness cannot be verified.");
            }
            if (families.Count == 0 || variants.Count == 0) throw new InvalidDataException("Official catalog markup did not expose a usable inventory.");
            var now = DateTimeOffset.UtcNow;
            warnings.Add("Official HTML has no verified total-count/revision contract. All discovered continuation links were followed; exhaustive coverage remains unverified.");
            return new(new(now, now, Convert.ToHexString(identity.GetHashAndReset()), visited.Count, OllamaCatalogCompleteness.Partial, variants.Values.OrderBy(m => m.Name, StringComparer.Ordinal).ToImmutableArray(), "Official library and tag links acquired. Completeness remains partial because the source does not provide a verified total inventory contract."), true, warnings.ToImmutable());
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidDataException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return new(previous with { Detail = "Catalog refresh unavailable. Retaining the last successful inventory without inventing entries." }, false, ["Connectivity, source markup or acquisition limits prevented a verified refresh."]);
        }
    }
    public void Dispose() => http.Dispose();
}

public sealed record OllamaHardwareEvidence(long? AvailableRamBytes, long? UsableVramBytes, bool? BackendSupported, long? FreeDiskBytes, string Architecture, string? Gpu, DateTimeOffset ObservedAt);
public sealed record OllamaModelRequirements(long? BlobBytes, long? ContextOverheadBytes, long? ParameterCount, string? Quantization, int? DeclaredContext, int RequestedContext);
public static class OllamaFitEvaluator
{
    public static OllamaHardwareFit Evaluate(OllamaHardwareEvidence hardware, OllamaModelRequirements model)
    {
        var evidence = ImmutableArray.CreateBuilder<string>(); var missing = ImmutableArray.CreateBuilder<string>();
        if (hardware.AvailableRamBytes is null or <= 0) missing.Add("Available RAM");
        if (hardware.UsableVramBytes is null or < 0) missing.Add("Usable VRAM");
        if (hardware.BackendSupported is null) missing.Add("Driver/backend support");
        if (hardware.FreeDiskBytes is null or < 0) missing.Add("Free destination disk");
        if (model.BlobBytes is null or <= 0 || model.ContextOverheadBytes is null or < 0 || model.ParameterCount is null or <= 0 || string.IsNullOrWhiteSpace(model.Quantization) || model.DeclaredContext is null or <= 0) missing.Add("Exact blob, parameter, quantization and context metadata");
        evidence.Add($"Hardware observed at {hardware.ObservedAt:O}; architecture {hardware.Architecture}; GPU {hardware.Gpu ?? "unavailable"}.");
        if (missing.Count > 0) return new("Unknown", DateTimeOffset.UtcNow, evidence.ToImmutable(), missing.ToImmutable());
        if (model.RequestedContext <= 0) throw new ArgumentOutOfRangeException(nameof(model));
        long required = checked(model.BlobBytes!.Value + model.ContextOverheadBytes!.Value);
        evidence.Add($"Estimated working bytes: {required}; model bytes: {model.BlobBytes}; context overhead: {model.ContextOverheadBytes}.");
        evidence.Add($"Available RAM: {hardware.AvailableRamBytes}; usable VRAM: {hardware.UsableVramBytes}; free disk: {hardware.FreeDiskBytes}; backend support: {hardware.BackendSupported}.");
        string verdict = hardware.FreeDiskBytes < model.BlobBytes || model.RequestedContext > model.DeclaredContext || required > hardware.AvailableRamBytes
            ? "Unlikely" : hardware.BackendSupported == true && required <= hardware.UsableVramBytes && required <= hardware.AvailableRamBytes!.Value * 0.75 ? "Runs well" : "Runs with limits";
        evidence.Add("Estimates do not guarantee runtime success; CPU fallback may be slower and context overhead must be supplied from verified metadata.");
        return new(verdict, DateTimeOffset.UtcNow, evidence.ToImmutable(), []);
    }
}
