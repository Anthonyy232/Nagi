using System.Text.Json;
using Microsoft.Extensions.Logging;
using Nagi.Core.Platform;
using Nagi.Core.Playback;
using Nagi.Core.Scrobbling;

namespace Nagi.Core.Settings;

public sealed class SettingsService : ISettingsService
{
    private const string LastFmCredentialResource = "Nagi/LastFm";
    private const string ListenBrainzTokenResource = "Nagi/ListenBrainz";
    private const string ListenBrainzTokenUserName = "token";

    private readonly IAutoLaunchService _autoLaunchService;
    private readonly ICredentialLockerService _credentialLockerService;
    private readonly ILogger<SettingsService> _logger;
    private readonly IPlaybackStateStore _playbackStateStore;
    private readonly IReadOnlyList<Setting> _settings;
    private readonly ISettingsStore _store;

    /// <param name="settings">Every declared setting, which a reset and a backup cover.</param>
    public SettingsService(ISettingsStore store, IEnumerable<Setting> settings, IPlaybackStateStore playbackStateStore,
        ICredentialLockerService credentialLockerService, IAutoLaunchService autoLaunchService,
        ILogger<SettingsService> logger)
    {
        _store = store;
        _settings = settings.ToList();
        _playbackStateStore = playbackStateStore;
        _credentialLockerService = credentialLockerService;
        _autoLaunchService = autoLaunchService;
        _logger = logger;
    }

    public event Action<Setting>? SettingChanged;
    public event Action<ScrobblingAccount>? CredentialsChanged;

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

    public Task<(string? Username, string? SessionKey)?> GetLastFmCredentialsAsync() =>
        Task.FromResult(_credentialLockerService.RetrieveCredential(LastFmCredentialResource));

    public Task<bool> SaveLastFmCredentialsAsync(string username, string sessionKey) =>
        Task.FromResult(SaveCredential(ScrobblingAccount.LastFm, LastFmCredentialResource, username, sessionKey));

    public Task ClearLastFmCredentialsAsync()
    {
        _credentialLockerService.RemoveCredential(LastFmCredentialResource);
        CredentialsChanged?.Invoke(ScrobblingAccount.LastFm);
        return Task.CompletedTask;
    }

    public Task<string?> GetListenBrainzUserTokenAsync() =>
        Task.FromResult(_credentialLockerService.RetrieveCredential(ListenBrainzTokenResource)?.Password);

    public Task<bool> SaveListenBrainzUserTokenAsync(string token) =>
        Task.FromResult(SaveCredential(ScrobblingAccount.ListenBrainz, ListenBrainzTokenResource, ListenBrainzTokenUserName, token));

    public Task ClearListenBrainzUserTokenAsync()
    {
        _credentialLockerService.RemoveCredential(ListenBrainzTokenResource);
        CredentialsChanged?.Invoke(ScrobblingAccount.ListenBrainz);
        return Task.CompletedTask;
    }

    public async Task ResetToDefaultsAsync()
    {
        var previous = _settings.Select(setting => (Setting: setting, Value: StoredFormOf(setting))).ToList();
        _store.Values.Clear();
        foreach (var (setting, value) in previous)
            if (!Equals(value, setting.StoredFormOf(null))) SettingChanged?.Invoke(setting);

        _playbackStateStore.Clear();
        await ClearLastFmCredentialsAsync().ConfigureAwait(false);
        await ClearListenBrainzUserTokenAsync().ConfigureAwait(false);
        await _autoLaunchService.SetEnabledAsync(false).ConfigureAwait(false);
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

    private bool SaveCredential(ScrobblingAccount account, string resource, string userName, string secret)
    {
        if (!_credentialLockerService.SaveCredential(resource, userName, secret))
        {
            _logger.LogWarning("The credential store refused the sign-in for {Resource}.", resource);
            return false;
        }

        CredentialsChanged?.Invoke(account);
        return true;
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
