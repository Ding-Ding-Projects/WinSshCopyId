using System.Collections.Immutable;
using System.Text.RegularExpressions;

namespace WinSshCopyId.Tools.Ollama;

public enum OllamaLaunchProfileKind { RuntimeServer, LocalModelSession }
public sealed record OllamaLaunchProfile(string Id, OllamaLaunchProfileKind Kind, string ExecutablePath, string WorkingDirectory, string? Model);
public sealed record OllamaLaunchSnapshot(Guid Id, DateTimeOffset CreatedAt, OllamaLaunchProfile Profile, string? PreviousProfileId);
public sealed record OllamaLaunchPreview(bool CanLaunch, string Executable, ImmutableArray<string> Arguments, string WorkingDirectory,
    ImmutableDictionary<string, string> Environment, ImmutableArray<string> Blockers, OllamaHardwareFit Fit);
public sealed record OllamaInstallerPlan(string State, string Architecture, string Destination, ImmutableArray<string> RequiredEvidence, string NextAction);

/// <summary>Typed plans only. This component never executes processes, changes environment, or installs software.</summary>
public static class OllamaManagedPlans
{
    public static ImmutableArray<string> AvailableProfileKinds => ["Verified local runtime server", "Verified local model session"];
    public static OllamaLaunchPreview Preview(OllamaLaunchProfile profile, string admittedExecutablePath, bool executableIdentityVerified, bool listenerOwnershipVerified)
    {
        var blockers = ImmutableArray.CreateBuilder<string>();
        bool validPaths = Path.IsPathFullyQualified(profile.ExecutablePath) && Path.IsPathFullyQualified(profile.WorkingDirectory) && Path.IsPathFullyQualified(admittedExecutablePath);
        if (!validPaths) blockers.Add("Select absolute executable and working-directory paths through the file and folder pickers.");
        if (!executableIdentityVerified || !validPaths || !string.Equals(Path.GetFullPath(profile.ExecutablePath), Path.GetFullPath(admittedExecutablePath), StringComparison.OrdinalIgnoreCase))
            blockers.Add("Executable identity, publisher and digest must match the admitted managed installation record.");
        if (!File.Exists(profile.ExecutablePath) || !Directory.Exists(profile.WorkingDirectory)) blockers.Add("The selected executable or working directory is unavailable.");
        if (!listenerOwnershipVerified) blockers.Add("Verify that the fixed loopback port is free or owned by the admitted runtime before launch.");
        ImmutableArray<string> arguments = [];
        if (profile.Kind == OllamaLaunchProfileKind.RuntimeServer) arguments = ["serve"];
        else if (profile.Kind == OllamaLaunchProfileKind.LocalModelSession && profile.Model is not null) arguments = ["run", OllamaClient.ValidateModel(profile.Model)];
        else blockers.Add("Choose a registered profile kind and an installed exact model tag.");
        // No caller-supplied arguments, script text, environment values or shell expansion are admitted.
        var environment = ImmutableDictionary<string, string>.Empty.Add("OLLAMA_HOST", "127.0.0.1:11434");
        blockers.Add("Process execution and automatic rollback are not implemented. This is a reviewable plan only.");
        return new(false, profile.ExecutablePath, arguments, profile.WorkingDirectory, environment, blockers.ToImmutable(), OllamaHardwareFit.Unknown);
    }
    public static OllamaLaunchSnapshot Snapshot(OllamaLaunchProfile profile, string? previousProfileId) => new(Guid.NewGuid(), DateTimeOffset.UtcNow, profile, previousProfileId);
    public static OllamaLaunchProfile Restore(OllamaLaunchSnapshot snapshot) => snapshot.Profile;
    public static OllamaInstallerPlan PrepareInstallation(string architecture)
    {
        if (architecture is not ("x64" or "arm64")) throw new ArgumentException("No managed payload admitted for this architecture.", nameof(architecture));
        string destination = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinSshCopyId", "Tools", "Ollama", architecture);
        return new("Unavailable: verified payload and privileged installation boundary not implemented", architecture, destination,
            ["Pinned official version and source", "Expected byte count and SHA-256", "Publisher requirements and complete archive member inventory", "Existing-installation and model-store preservation snapshot", "Storage budget, cancellation and safe extraction verification", "Atomic versioned promotion and independent version verification"],
            "Keep the existing installation intact. Installation remains disabled until the complete managed payload and native verification boundary are available.");
    }
}
