namespace Nagi.Core.Platform;

/// <summary>
///     The platform's persistent key-value store for settings. Values are strings, booleans and numbers.
/// </summary>
public interface ISettingsStore
{
    IDictionary<string, object> Values { get; }
}
