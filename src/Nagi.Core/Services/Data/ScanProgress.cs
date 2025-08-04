namespace Nagi.Core.Services.Data;

/// <summary>
///     Represents the state of a music library scan operation, providing data for a user-friendly progress display.
/// </summary>
public class ScanProgress
{
    /// <summary>
    ///     A human-readable status message indicating the current phase of the scan.
    /// </summary>
    public string StatusText { get; set; } = string.Empty;

    /// <summary>
    ///     The total number of new songs discovered during the current scan.
    /// </summary>
    public int NewSongsFound { get; set; }

    /// <summary>
    ///     The overall completion percentage of the scan operation, typically reaching 100% at the end.
    /// </summary>
    public double Percentage { get; set; }

    /// <summary>
    ///     Indicates whether the UI should display an indeterminate busy indicator for the scan.
    /// </summary>
    public bool IsIndeterminate { get; set; }

    /// <summary>
    ///     The total number of files processed on disk during the scan.
    /// </summary>
    public int TotalFiles { get; set; }

    /// <summary>
    ///     The path of the file currently being processed. Can be null if no specific file is being highlighted.
    /// </summary>
    public string? CurrentFilePath { get; set; }
}