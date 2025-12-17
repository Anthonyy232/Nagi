using Nagi.Core.Models;

namespace Nagi.Core.Services.Abstractions;

/// <summary>
///     Defines a contract for extracting metadata from a music file.
/// </summary>
public interface IMetadataService
{
    /// <summary>
    ///     Asynchronously extracts all relevant metadata from a single music file.
    /// </summary>
    /// <param name="filePath">The absolute path to the music file.</param>
    /// <param name="baseFolderPath">
    ///     Optional. The root folder path for the library scan. When provided, the service will search
    ///     for cover art files in the directory hierarchy up to (and including) this folder if no 
    ///     embedded art is found.
    /// </param>
    /// <returns>A <see cref="SongFileMetadata" /> object containing the extracted data and file properties.</returns>
    Task<SongFileMetadata> ExtractMetadataAsync(string filePath, string? baseFolderPath = null);
}
