using System.Collections.Immutable;

namespace WinSshCopyId.Tools.Ollama;

/// <summary>Offline checks. No networking, installation or process launch occurs.</summary>
public static class OllamaTests
{
    public static Task<int> RunAsync()
    {
        int count = 0;
        void Check(bool value) { if (!value) throw new InvalidOperationException("Ollama offline contract check failed."); count++; }
        var known = new OllamaHardwareEvidence(32_000_000_000, 16_000_000_000, true, 100_000_000_000, "x64", "Measured GPU", DateTimeOffset.UtcNow);
        var model = new OllamaModelRequirements(4_000_000_000, 1_000_000_000, 7_000_000_000, "verified Q4", 8192, 4096);
        Check(OllamaFitEvaluator.Evaluate(known, model).Verdict == "Runs well");
        Check(OllamaFitEvaluator.Evaluate(known with { UsableVramBytes = 0 }, model).Verdict == "Runs with limits");
        Check(OllamaFitEvaluator.Evaluate(known with { AvailableRamBytes = 1_000_000_000 }, model).Verdict == "Unlikely");
        Check(OllamaFitEvaluator.Evaluate(known with { BackendSupported = null }, model).Verdict == "Unknown");
        Check(OllamaFitEvaluator.Evaluate(known, model with { BlobBytes = null }).Verdict == "Unknown");
        Check(OllamaFitEvaluator.Evaluate(known, model with { RequestedContext = 16384 }).Verdict == "Unlikely");
        bool rejected = false;
        try { OllamaClient.ValidateModel("../../invalid"); } catch (ArgumentException) { rejected = true; }
        Check(rejected);
        var invalidManifest = new OllamaPayloadManifest("1.0.0", "x64", new("https://example.com/payload.zip"), 10, new('a', 64), [new("ollama.exe", 10, new('b', 64))], "Required publisher");
        rejected = false;
        try { OllamaInstaller.ValidateManifest(invalidManifest); } catch (InvalidDataException) { rejected = true; }
        Check(rejected);
        rejected = false;
        try { OllamaInstaller.ValidateManifest(invalidManifest with { Source = new("https://github.com/ollama/ollama/releases/download/v1.0.0/ollama-windows-amd64.zip"), Members = [new("../ollama.exe", 10, new('b', 64))] }); } catch (InvalidDataException) { rejected = true; }
        Check(rejected);
        Check(OllamaCatalogSnapshot.Unavailable.Completeness == OllamaCatalogCompleteness.Unavailable);
        return Task.FromResult(count);
    }
}
