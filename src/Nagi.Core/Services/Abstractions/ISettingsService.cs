using Nagi.Core.Settings;

namespace Nagi.Core.Services.Abstractions;

/// <summary>
///     Reads and writes the app's settings, and keeps the scrobbling accounts' credentials in the operating
///     system's credential store.
/// </summary>
public interface ISettingsService
{
    /// <summary>
    ///     Raised on the thread that changed the setting, only when its effective value changed.
    /// </summary>
    event Action<Setting>? SettingChanged;

    /// <summary>
    ///     Raised when a Last.fm or ListenBrainz credential is saved or removed.
    /// </summary>
    event Action? CredentialsChanged;

    T Get<T>(Setting<T> setting);

    void Set<T>(Setting<T> setting, T value);

    /// <returns>The username and session key, or null if not signed in.</returns>
    Task<(string? Username, string? SessionKey)?> GetLastFmCredentialsAsync();

    /// <returns>False if the credential store refused them, for example because it is full.</returns>
    Task<bool> SaveLastFmCredentialsAsync(string username, string sessionKey);

    /// <summary>
    ///     Removes the Last.fm credentials. <see cref="IScrobblingAccounts.DisconnectLastFmAsync" /> also turns its
    ///     submissions off.
    /// </summary>
    Task ClearLastFmCredentialsAsync();

    Task<string?> GetListenBrainzUserTokenAsync();

    /// <returns>False if the credential store refused it, for example because it is full.</returns>
    Task<bool> SaveListenBrainzUserTokenAsync(string token);

    Task ClearListenBrainzUserTokenAsync();

    /// <summary>
    ///     Resets every setting, signs out of both scrobbling accounts, clears the saved playback session and turns
    ///     launching at sign-in off.
    /// </summary>
    Task ResetToDefaultsAsync();

    /// <summary>
    ///     Captures every stored setting, for a backup. Account credentials live in the operating system's
    ///     credential store and are not included.
    /// </summary>
    Task<string> ExportAsync();

    /// <summary>
    ///     Replaces every stored setting with a snapshot from <see cref="ExportAsync" />, without raising
    ///     <see cref="SettingChanged" />: it takes full effect after a restart. Throws
    ///     <see cref="System.Text.Json.JsonException" /> without changing anything if the snapshot isn't valid.
    /// </summary>
    Task ImportAsync(string snapshot);
}
