using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace WinSshCopyId.Services;

public sealed record HostKeyApproval(string Host, int Port, string Fingerprint, string Algorithm = "unknown");
public sealed class HostKeyChangedException(string host, int port, string expectedFingerprint, string observedFingerprint, string algorithm)
    : Exception($"Host key changed for {host}:{port}. Expected SHA256:{expectedFingerprint}; observed {algorithm} SHA256:{observedFingerprint}. Connection stopped.")
{
    public string Host { get; } = host;
    public int Port { get; } = port;
    public string ExpectedFingerprint { get; } = expectedFingerprint;
    public string ObservedFingerprint { get; } = observedFingerprint;
    public string Algorithm { get; } = algorithm;
}

/// <summary>Host keys are scoped by endpoint. Changed keys are never approved implicitly.</summary>
public sealed class HostTrustStore
{
    private readonly string _path;
    private static readonly object Sync = new();
    public HostTrustStore(string? path = null) => _path = path ?? Path.Combine(SettingsStore.Directory, "trusted-hosts.json");

    public bool Check(string host, int port, string fingerprint, Func<HostKeyApproval, bool>? approve, string algorithm = "unknown")
    {
        SshInput.ValidateEndpoint(host, port);
        if (string.IsNullOrWhiteSpace(fingerprint)) return false;
        lock (Sync)
        {
            var keys = File.Exists(_path)
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_path))
                    ?? throw new InvalidDataException("Invalid host trust store.")
                : new Dictionary<string, string>();
            string endpoint = $"{host.ToLowerInvariant()}:{port}";
            if (keys.TryGetValue(endpoint, out string? known))
            {
                if (!string.Equals(known, fingerprint, StringComparison.Ordinal))
                    throw new HostKeyChangedException(host, port, known, fingerprint, algorithm);
                return true;
            }
            if (approve?.Invoke(new HostKeyApproval(host, port, fingerprint, algorithm)) != true) return false;
            keys.Add(endpoint, fingerprint);
            AtomicFile.Write(_path, JsonSerializer.Serialize(keys));
            return true;
        }
    }
}

internal static class AtomicFile
{
    public static void Write(string path, string content)
    {
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false)))
            {
                writer.Write(content);
                writer.Flush();
                stream.Flush(true);
            }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
