using System.IO;
using System.Text.Json;

namespace Nudge.Services;

public sealed record AppSettings(
    string BackendMode = "worker",
    string? WorkerBaseUrl = null,
    bool UseDeveloperWorker = false);

public sealed class AppSettingsStore
{
    private static readonly string LegacySettingsFolder = string.Concat("Cli", "cky", "Clone");
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string settingsPath;
    private readonly string? legacySettingsPath;

    public AppSettingsStore()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Nudge",
            "settings.json"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                LegacySettingsFolder,
                "settings.json"))
    {
    }

    public AppSettingsStore(string settingsPath, string? legacySettingsPath = null)
    {
        this.settingsPath = settingsPath;
        this.legacySettingsPath = legacySettingsPath;
        var directory = Path.GetDirectoryName(settingsPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    public AppSettings Load()
    {
        try
        {
            var pathToRead = settingsPath;
            if (!File.Exists(pathToRead))
            {
                pathToRead = legacySettingsPath ?? settingsPath;
            }

            if (!File.Exists(pathToRead))
            {
                return new AppSettings();
            }

            var json = File.ReadAllText(pathToRead);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            if (!string.Equals(pathToRead, settingsPath, StringComparison.OrdinalIgnoreCase))
            {
                Save(settings);
            }

            return settings;
        }
        catch (Exception error)
        {
            AppLogger.Error("Loading app settings failed", error);
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        File.WriteAllText(settingsPath, json);
    }
}
