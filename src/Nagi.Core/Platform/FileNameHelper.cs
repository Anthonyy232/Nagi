namespace Nagi.Core.Platform;

internal static class FileNameHelper
{
    /// <summary>
    ///     Sanitizes a string for use as a file name by removing characters that are
    ///     invalid on the file system (e.g., \ / : * ? " &lt; &gt; |).
    /// </summary>
    /// <param name="fallback">The fallback name if the sanitized result is empty.</param>
    public static string SanitizeFileName(string name, string? fallback = null)
    {
        fallback ??= Resources.Strings.UnknownFilename;
        if (string.IsNullOrWhiteSpace(name))
            return fallback;

        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = new string(name.Where(c => !invalidChars.Contains(c)).ToArray());
        return TextNormalizer.Normalize(sanitized) ?? fallback;
    }
}
