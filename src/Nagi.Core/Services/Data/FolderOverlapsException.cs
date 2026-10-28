namespace Nagi.Core.Services.Data;

/// <summary>
///     Thrown when adding a library folder that is inside, or contains, a folder already in the library.
/// </summary>
public sealed class FolderOverlapsException(string path)
    : InvalidOperationException($"'{path}' overlaps a folder already in the library.");
