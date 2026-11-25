using System.IO;
using System.Text.Json;
using AetherBrowser.Models;

namespace AetherBrowser.Services;

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public string DataDirectory { get; }
    public string SettingsPath { get; }
    public BrowserSettings Current { get; private set; }

    public event Action<BrowserSettings>? SettingsChanged;

    public SettingsService()
    {
        DataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AetherBrowser");
        Directory.CreateDirectory(DataDirectory);
        SettingsPath = Path.Combine(DataDirectory, "settings.json");
        Current = Load();
    }

    private BrowserSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var loaded = JsonSerializer.Deserialize<BrowserSettings>(json, JsonOptions);
                if (loaded is not null)
                {
                    var migrated = false;
                    // Migrate v1 violet default accent (#6366F1) to neutral architectural grey (#A1A1AA)
                    if (loaded.SettingsSchemaVersion < 2)
                    {
                        if (string.IsNullOrWhiteSpace(loaded.AccentColor) ||
                            loaded.AccentColor.Equals("#6366F1", StringComparison.OrdinalIgnoreCase))
                        {
                            loaded.AccentColor = "#A1A1AA";
                        }
                        migrated = true;
                    }
                    // Migrate v2 empty GitHub repo to official GTX555/aether-browser
                    if (loaded.SettingsSchemaVersion < 3)
                    {
                        if (string.IsNullOrWhiteSpace(loaded.GitHubRepository))
                            loaded.GitHubRepository = "GTX555/aether-browser";
                        loaded.SettingsSchemaVersion = 3;
                        migrated = true;
                    }
                    // Migrate v3 aggressive LowEndMemoryMode default (which evicted active tab GPU frames on switch)
                    if (loaded.SettingsSchemaVersion < 4)
                    {
                        loaded.LowEndMemoryMode = false;
                        loaded.SettingsSchemaVersion = 4;
                        migrated = true;
                    }
                    if (migrated)
                        SaveInternal(loaded);
                    return loaded;
                }
            }
        }
        catch
        {
            // Fallback to defaults on corrupt settings file
        }
        var defaults = new BrowserSettings();
        SaveInternal(defaults);
        return defaults;
    }

    public void Save()
    {
        SaveInternal(Current);
        SettingsChanged?.Invoke(Current);
    }

    public void Update(Action<BrowserSettings> mutator)
    {
        mutator(Current);
        Save();
    }

    public void Replace(BrowserSettings updated)
    {
        Current = updated;
        Save();
    }

    public void ResetToDefaults()
    {
        Current = new BrowserSettings();
        Save();
    }

    public string ExportJson() => JsonSerializer.Serialize(Current, JsonOptions);

    public bool ImportJson(string json)
    {
        try
        {
            var parsed = JsonSerializer.Deserialize<BrowserSettings>(json, JsonOptions);
            if (parsed is null) return false;
            Current = parsed;
            Save();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void SaveInternal(BrowserSettings settings)
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            var json = JsonSerializer.Serialize(settings, JsonOptions);
            File.WriteAllText(SettingsPath, json);
        }
        catch
        {
            // Best-effort write
        }
    }
}
