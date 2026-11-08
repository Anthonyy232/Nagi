namespace Nagi.Core.Settings;

public interface ISettingsService
{
    /// <summary>
    ///     Raised on the thread that changed the setting, only when its effective value changed.
    /// </summary>
    event Action<Setting>? SettingChanged;

    T Get<T>(Setting<T> setting);

    void Set<T>(Setting<T> setting, T value);

    /// <summary>
    ///     Returns every setting to its default. <see cref="Backup.IAppDataReset" /> resets the rest of the app.
    /// </summary>
    void ResetToDefaults();

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
