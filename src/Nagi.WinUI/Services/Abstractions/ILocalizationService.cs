namespace Nagi.WinUI.Services.Abstractions;

/// <summary>
///     Provides methods for resolving localized strings throughout the application.
/// </summary>
public interface ILocalizationService
{
    /// <summary>
    ///     Retrieves the localized string for the specified resource key.
    ///     Returns the key itself if not found.
    /// </summary>
    /// <param name="key">The resource key to resolve.</param>
    /// <returns>The localized string, or the key itself if not found.</returns>
    string GetString(string key);

    /// <summary>
    ///     Retrieves the localized string for the specified resource key, or a fallback if not found.
    /// </summary>
    /// <param name="key">The resource key to resolve.</param>
    /// <param name="fallback">The fallback string to return if the key is not found.</param>
    /// <returns>The localized string or fallback.</returns>
    string GetString(string key, string fallback);

    /// <summary>
    ///     Retrieves a localized format string and applies the provided arguments.
    ///     Useful for strings like "You have {0} songs in {1} albums".
    /// </summary>
    /// <param name="key">The resource key for the format string.</param>
    /// <param name="args">The arguments to format into the string.</param>
    /// <returns>The formatted localized string.</returns>
    string GetFormattedString(string key, params object[] args);
}
