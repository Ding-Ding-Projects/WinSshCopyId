using System.Text.Json;

namespace WinSshCopyId.Tools.Ollama;

public sealed record OllamaAdmittedPayload(OllamaPayloadManifest Manifest, string SignerSubject, string SignerThumbprint)
{
    public Task<bool> VerifyPublisherAsync(string path, string requirement, CancellationToken token)
        => requirement == Manifest.PublisherRequirement
            ? OllamaPublisherVerifier.VerifyAsync(path, SignerSubject, SignerThumbprint, token)
            : Task.FromResult(false);
}
public static class OllamaPinnedPayload
{
    public static async Task<OllamaAdmittedPayload> LoadAsync(string packagedManifestPath, CancellationToken token = default)
    {
        if (new FileInfo(packagedManifestPath).Length > 4 * 1024 * 1024) throw new InvalidDataException("Payload manifest exceeds bound.");
        await using var stream = File.OpenRead(packagedManifestPath);
        using var json = await JsonDocument.ParseAsync(stream, new JsonDocumentOptions { MaxDepth = 16 }, token);
        var manifest = json.RootElement.Deserialize<OllamaPayloadManifest>() ?? throw new InvalidDataException("Invalid payload manifest.");
        OllamaInstaller.ValidateManifest(manifest);
        var signature = json.RootElement.GetProperty("SignatureObservation");
        var subject = signature.GetProperty("Subject").GetString();
        var thumbprint = signature.GetProperty("Thumbprint").GetString();
        if (signature.GetProperty("Status").GetString() != "Valid" || subject is null || !subject.StartsWith("CN=Ollama Inc., O=Ollama Inc.,", StringComparison.Ordinal) ||
            thumbprint is null || thumbprint.Length != 40 || !thumbprint.All(Uri.IsHexDigit)) throw new InvalidDataException("Payload publisher pin is invalid.");
        return new(manifest, subject, thumbprint);
    }
}
