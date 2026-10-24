using System.Text.Json;
using TranssionLab.Core.Interfaces;
using TranssionLab.Core.Models;

namespace TranssionLab.Core.Services;

public sealed class AppSettingsService : IAppSettingsService
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private readonly string _settingsFilePath;

    public AppSettingsModel Current { get; private set; }

    public AppSettingsService(string? customSettingsDirectory = null)
    {
        var baseDir = customSettingsDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TranssionLab");
        Directory.CreateDirectory(baseDir);
        _settingsFilePath = Path.Combine(baseDir, "settings.json");
        Current = LoadOrCreateDefault(baseDir);
    }

    private AppSettingsModel LoadOrCreateDefault(string baseDir)
    {
        try
        {
            if (File.Exists(_settingsFilePath))
            {
                var json = File.ReadAllText(_settingsFilePath);
                var loaded = JsonSerializer.Deserialize<AppSettingsModel>(json, JsonOpts);
                if (loaded is not null)
                {
                    return loaded;
                }
            }
        }
        catch
        {
            // Fall back to clean defaults if settings file was corrupted
        }

        return new AppSettingsModel
        {
            WorkspaceDataDirectory = baseDir
        };
    }

    public async Task SaveAsync(AppSettingsModel updated, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(updated);
        Directory.CreateDirectory(Path.GetDirectoryName(_settingsFilePath)!);
        var json = JsonSerializer.Serialize(updated, JsonOpts);
        await File.WriteAllTextAsync(_settingsFilePath, json, ct).ConfigureAwait(false);
        Current = updated;
    }
}
