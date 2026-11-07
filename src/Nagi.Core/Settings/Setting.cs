using System.Reflection;
using System.Text.Json;
using Nagi.Core.Services.Abstractions;

namespace Nagi.Core.Settings;

/// <summary>
///     A value kept in the <see cref="ISettingsStore" /> under <see cref="Key" />. Each layer declares its
///     settings as public static fields of one class (Core's are in <see cref="CoreSettings" />), which is how the
///     settings service knows every setting it resets and backs up.
/// </summary>
public abstract class Setting
{
    private protected Setting(string key) => Key = key;

    /// <summary>
    ///     The store key. Saved settings are found by it, so it must never change.
    /// </summary>
    public string Key { get; }

    // The effective value in its stored form, so the service can compare values without knowing their type.
    internal abstract object? StoredFormOf(object? stored);

    /// <summary>
    ///     A setting stored as the value itself: a boolean, number or string.
    /// </summary>
    /// <param name="normalize">Brings every read and written value into range.</param>
    public static Setting<T> Value<T>(string key, T defaultValue, Func<T, T>? normalize = null) =>
        new(key, () => defaultValue, stored => stored is T value ? value : defaultValue, value => value, normalize);

    /// <summary>
    ///     An enum setting stored as the member's name. An unknown name reads as the default.
    /// </summary>
    public static Setting<T> EnumName<T>(string key, T defaultValue) where T : struct, Enum =>
        new(key, () => defaultValue,
            stored => stored is string name && Enum.TryParse(name, out T value) && Enum.IsDefined(value)
                ? value
                : defaultValue,
            value => value.ToString());

    /// <summary>
    ///     A setting stored as a JSON string. Malformed JSON reads as the default.
    /// </summary>
    /// <param name="defaultValue">Called for every read, so mutable defaults are never shared.</param>
    /// <param name="normalize">Reconciles every read and written value, for example with the current defaults.</param>
    public static Setting<T> Json<T>(string key, Func<T> defaultValue, Func<T, T>? normalize = null) where T : class? =>
        new(key, defaultValue,
            stored => stored is string json ? JsonSerializer.Deserialize<T>(json) ?? defaultValue() : defaultValue(),
            value => value is null ? null : JsonSerializer.Serialize(value), normalize);

    /// <summary>
    ///     The settings declared as public static fields of <paramref name="type" />.
    /// </summary>
    public static IEnumerable<Setting> DeclaredIn(Type type) =>
        type.GetFields(BindingFlags.Public | BindingFlags.Static).Select(field => field.GetValue(null)).OfType<Setting>();
}

public sealed class Setting<T> : Setting
{
    private readonly Func<T> _defaultValue;
    private readonly Func<T, T> _normalize;
    private readonly Func<object, T> _read;
    private readonly Func<T, object?> _write;

    /// <param name="read">Converts a stored value back, returning the default for one this setting didn't write.</param>
    /// <param name="write">A value's stored form: a boolean, number or string, or null to remove the key.</param>
    /// <param name="normalize">Brings every read and written value into its canonical form.</param>
    public Setting(string key, Func<T> defaultValue, Func<object, T> read, Func<T, object?> write,
        Func<T, T>? normalize = null) : base(key)
    {
        _defaultValue = defaultValue;
        _read = read;
        _write = write;
        _normalize = normalize ?? (value => value);
    }

    public T Default => _normalize(_defaultValue());

    /// <summary>
    ///     Reads the setting straight from <paramref name="store" />, for code that runs before the settings
    ///     service exists.
    /// </summary>
    /// <exception cref="JsonException">The stored JSON is malformed.</exception>
    public T ReadFrom(ISettingsStore store) => Read(store.Values.TryGetValue(Key, out var stored) ? stored : null);

    internal T Read(object? stored) => _normalize(stored is null ? _defaultValue() : _read(stored));

    internal object? Write(T value) => _write(_normalize(value));

    internal override object? StoredFormOf(object? stored) => Write(Read(stored));
}
