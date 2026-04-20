using System.Text;

namespace Nagi.Core.Helpers;

/// <summary>
///     Normalizes filesystem paths so that semantically identical paths compare equal as strings.
///     Handles separator direction, trailing separators, Unicode normalization, and drive-letter casing.
///     Does NOT resolve mapped drives to UNC (platform-specific, handled by IFileSystemService).
/// </summary>
public static class PathCanonicalizer
{
    /// <summary>
    ///     Applies deterministic textual normalization. Network/UNC resolution must happen
    ///     in the platform-specific <see cref="Services.Abstractions.IFileSystemService"/> first.
    /// </summary>
    public static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;

        // Unicode NFC — collapses multi-byte variants (e.g., é as NFD vs NFC).
        // NOCASE collation handles ASCII case but not Unicode equivalence.
        var normalized = path.Normalize(NormalizationForm.FormC);

        // Collapse alt separators to canonical ones. Preserve UNC prefix "\\" by checking first.
        var isUnc = normalized.StartsWith(@"\\") || normalized.StartsWith("//");
        normalized = normalized.Replace('/', '\\');

        if (isUnc)
        {
            // Keep the "\\" prefix; only collapse duplicate separators in the remainder.
            var rest = normalized.Substring(2);
            while (rest.Contains(@"\\"))
                rest = rest.Replace(@"\\", @"\");
            normalized = @"\\" + rest;
        }
        else
        {
            while (normalized.Contains(@"\\"))
                normalized = normalized.Replace(@"\\", @"\");
        }

        // Trim trailing separators (but never strip the lone root "\\server\share" separator between tokens).
        normalized = normalized.TrimEnd('\\', '/');

        // Uppercase the drive letter for stability ("c:\..." -> "C:\..."). NOCASE collation would
        // compare equal anyway, but this avoids noise in UI and logs.
        if (normalized.Length >= 2 && normalized[1] == ':' && char.IsLetter(normalized[0]))
        {
            normalized = char.ToUpperInvariant(normalized[0]) + normalized.Substring(1);
        }

        return normalized;
    }

    /// <summary>
    ///     Returns true if the path is a UNC path (starts with "\\" after normalization).
    /// </summary>
    public static bool IsUncPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        return path.StartsWith(@"\\") || path.StartsWith("//");
    }
}
