using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinSshCopyId.Services;

/// <summary>
/// Persisted form values. The password, when remembered, is stored only as a
/// DPAPI-encrypted blob (see <see cref="Dpapi"/>) and never in plain text.
/// </summary>
public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 2;
    public string Hosts { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public int Port { get; set; } = 22;
    public string? KeyPath { get; set; }
    public bool AddAliases { get; set; }
    public bool RememberPassword { get; set; }

    /// <summary>"System", "Light" or "Dark".</summary>
    public string Theme { get; set; } = "System";

    /// <summary>Whether to auto-save settings when the window closes.</summary>
    public bool AutoSave { get; set; } = true;

    /// <summary>DPAPI-protected password (base64). Machine + user bound; local only.</summary>
    public string? ProtectedPassword { get; set; }

    /// <summary>Format marker, set on save/export and checked on import.</summary>
    public string? App { get; set; }
}

/// <summary>
/// Loads/saves <see cref="AppSettings"/> in %LocalAppData% and handles the
/// portable export/import format (which deliberately omits any password).
/// </summary>
public static class SettingsStore
{
    private const string AppMarker = "WinSshCopyId";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Directory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinSshCopyId");

    public static string SettingsPath => Path.Combine(Directory, "settings.json");

    /// <summary>Applies connection fields while retaining the user's local appearance and save preference.</summary>
    public static AppSettings ApplyProfile(AppSettings current, AppSettings profile) => new()
    {
        Hosts = profile.Hosts, Username = profile.Username, Port = profile.Port,
        KeyPath = profile.KeyPath, AddAliases = profile.AddAliases,
        Theme = current.Theme, AutoSave = current.AutoSave,
        RememberPassword = false, ProtectedPassword = null, App = AppMarker, SchemaVersion = 2,
    };

    public static void MigrateLegacyCredential(AppSettings settings, ICredentialStore credentials)
    {
        if (string.IsNullOrEmpty(settings.ProtectedPassword)) return;
        string legacy = settings.ProtectedPassword;
        string value = Dpapi.Unprotect(legacy) ?? throw new InvalidDataException("Legacy credential cannot be decrypted.");
        credentials.Set("ssh-password", value);
        if (!string.Equals(credentials.Get("ssh-password"), value, StringComparison.Ordinal))
            throw new IOException("Credential migration could not be verified.");
        settings.ProtectedPassword = null;
        try { Save(settings); }
        catch { settings.ProtectedPassword = legacy; throw; }
    }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                string json = File.ReadAllText(SettingsPath);
                AppSettings? loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                if (loaded is not null)
                {
                    if (loaded.SchemaVersion > 2) throw new InvalidDataException("Unsupported settings version.");
                    return loaded;
                }
            }
        }
        catch
        {
            // Corrupt or unreadable settings: start fresh rather than crash.
        }
        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        System.IO.Directory.CreateDirectory(Directory);
        if (!string.IsNullOrEmpty(settings.ProtectedPassword))
        {
            var credentials = new WinCredentialStore();
            string value = Dpapi.Unprotect(settings.ProtectedPassword) ?? throw new InvalidDataException("Legacy credential cannot be decrypted.");
            credentials.Set("ssh-password", value);
            if (!string.Equals(credentials.Get("ssh-password"), value, StringComparison.Ordinal))
                throw new IOException("Credential storage verification failed.");
        }
        settings.SchemaVersion = 2;
        settings.App = AppMarker;
        string? legacyCredential = settings.ProtectedPassword;
        settings.ProtectedPassword = null;
        string json;
        try { json = JsonSerializer.Serialize(settings, JsonOptions); }
        finally { settings.ProtectedPassword = legacyCredential; }
        AtomicFile.Write(SettingsPath, json);
    }

    /// <summary>Write a portable profile to <paramref name="path"/> with no password.</summary>
    public static void Export(string path, AppSettings settings)
    {
        var portable = new
        {
            SchemaVersion = 2,
            settings.Hosts, settings.Username, settings.Port, settings.KeyPath, settings.AddAliases,
            App = AppMarker,
        };
        string json = JsonSerializer.Serialize(portable, JsonOptions);
        AtomicFile.Write(path, json);
    }

    public static AppSettings Import(string path)
    {
        string json = File.ReadAllText(path);
        AppSettings? loaded;
        try
        {
            loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("That file is not valid JSON.", ex);
        }
        if (loaded is null || !string.Equals(loaded.App, AppMarker, StringComparison.Ordinal))
        {
            throw new InvalidDataException("That file is not a WinSshCopyId profile.");
        }
        if (loaded.SchemaVersion is < 1 or > 2) throw new InvalidDataException("Unsupported profile version.");
        _ = HostTarget.ParseList(loaded.Hosts, loaded.Port);
        SshInput.ValidateUser(loaded.Username);
        foreach (var target in HostTarget.ParseList(loaded.Hosts, loaded.Port)) SshInput.ValidateEndpoint(target.Host, target.Port);
        // Imported profiles never carry credentials or appearance preferences.
        loaded.Theme = "System";
        loaded.AutoSave = true;
        loaded.ProtectedPassword = null;
        loaded.RememberPassword = false;
        return loaded;
    }
}
