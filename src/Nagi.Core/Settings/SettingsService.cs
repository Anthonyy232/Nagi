using System.Text.Json;
using Microsoft.Extensions.Logging;
using Nagi.Core.Platform;

namespace Nagi.Core.Settings;

public sealed class SettingsService : ISettingsService
{
    private readonly ILogger<SettingsService> _logger;
    private readonly IReadOnlyList<Setting> _settings;
    private readonly ISettingsStore _store;

    /// <param name="settings">Every declared setting, which a reset and a backup cover.</param>
    public SettingsService(ISettingsStore store, IEnumerable<Setting> settings, ILogger<SettingsService> logger)
    {
        _store = store;
        _settings = settings.ToList();
        _logger = logger;
    }

    public event Action<Setting>? SettingChanged;

    public T Get<T>(Setting<T> setting)
    {
        try
        {
            return setting.ReadFrom(_store);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Ignoring the malformed saved value of setting {Key}.", setting.Key);
            return setting.Default;
        }
    }

    public void Set<T>(Setting<T> setting, T value)
    {
        var previous = StoredFormOf(setting);
        var stored = setting.Write(value);
        if (stored is null) _store.Values.Remove(setting.Key);
        else _store.Values[setting.Key] = stored;
        if (!Equals(previous, stored)) SettingChanged?.Invoke(setting);
    }

    public void ResetToDefaults()
    {
        var previous = _settings.Select(setting => (Setting: setting, Value: StoredFormOf(setting))).ToList();
        _store.Values.Clear();
        foreach (var (setting, value) in previous)
            if (!Equals(value, setting.StoredFormOf(null))) SettingChanged?.Invoke(setting);

        _logger.LogInformation("All application settings have been reset to their default values.");
    }

    public Task<string> ExportAsync()
    {
        var snapshot = new Dictionary<string, StoredSetting>();
        // The Last.fm auth token is only valid during one sign-in.
        foreach (var setting in _settings.Where(setting => setting != CoreSettings.LastFmAuthToken))
            if (_store.Values.TryGetValue(setting.Key, out var value))
                snapshot[setting.Key] = new StoredSetting(value.GetType().Name, JsonSerializer.SerializeToElement(value));
        return Task.FromResult(JsonSerializer.Serialize(snapshot));
    }

    public Task ImportAsync(string snapshot)
    {
        Dictionary<string, object> values;
        try
        {
            var entries = JsonSerializer.Deserialize<Dictionary<string, StoredSetting>>(snapshot)
                          ?? throw new JsonException("The settings snapshot is empty.");
            values = entries.ToDictionary(entry => entry.Key, entry => ToStoredValue(entry.Value));
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            throw new JsonException("The settings snapshot has a value of the wrong type.", ex);
        }

        _store.Values.Clear();
        foreach (var (key, value) in values) _store.Values[key] = value;
        _logger.LogInformation("Imported {Count} settings.", values.Count);
        return Task.CompletedTask;
    }

    private object? StoredFormOf(Setting setting)
    {
        _store.Values.TryGetValue(setting.Key, out var stored);
        try
        {
            return setting.StoredFormOf(stored);
        }
        catch (JsonException)
        {
            return setting.StoredFormOf(null);
        }
    }

    // Settings read stored values by type, so the snapshot records it rather than relying on JSON number kinds.
    private static object ToStoredValue(StoredSetting setting) => setting.Type switch
    {
        nameof(Boolean) => setting.Value.GetBoolean(),
        nameof(Int32) => setting.Value.GetInt32(),
        nameof(Int64) => setting.Value.GetInt64(),
        nameof(Double) => setting.Value.GetDouble(),
        nameof(String) => setting.Value.GetString()!,
        _ => throw new JsonException($"Unsupported setting type '{setting.Type}'.")
    };

    private sealed record StoredSetting(string Type, JsonElement Value);
}
