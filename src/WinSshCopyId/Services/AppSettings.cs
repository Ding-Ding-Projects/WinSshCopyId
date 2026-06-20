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
        settings.App = AppMarker;
        string json = JsonSerializer.Serialize(settings, JsonOptions);
        File.WriteAllText(SettingsPath, json, new UTF8Encoding(false));
    }

    /// <summary>Write a portable profile to <paramref name="path"/> with no password.</summary>
    public static void Export(string path, AppSettings settings)
    {
        var portable = new AppSettings
        {
            Hosts = settings.Hosts,
            Username = settings.Username,
            Port = settings.Port,
            KeyPath = settings.KeyPath,
            AddAliases = settings.AddAliases,
            Theme = settings.Theme,
            AutoSave = settings.AutoSave,
            RememberPassword = false,
            ProtectedPassword = null,
            App = AppMarker,
        };
        string json = JsonSerializer.Serialize(portable, JsonOptions);
        File.WriteAllText(path, json, new UTF8Encoding(false));
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
        // Imported profiles never carry a password.
        loaded.ProtectedPassword = null;
        loaded.RememberPassword = false;
        return loaded;
    }
}
