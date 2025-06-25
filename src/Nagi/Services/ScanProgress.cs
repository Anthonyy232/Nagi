using System.IO;

namespace Nagi.Services;

/// <summary>
///     Represents the state of a music library scan operation at a point in time.
/// </summary>
public class ScanProgress
{
    public int FilesProcessed { get; set; }
    public int TotalFiles { get; set; }
    public string? CurrentFilePath { get; set; }
    public string? StatusText { get; set; }
    public double Percentage { get; set; }

    /// <summary>
    ///     A computed message for display, combining the progress count and current file.
    /// </summary>
    public string Message => CurrentFilePath != null
        ? $"({FilesProcessed}/{TotalFiles}) {Path.GetFileName(CurrentFilePath)}"
        : $"({FilesProcessed}/{TotalFiles})";
}