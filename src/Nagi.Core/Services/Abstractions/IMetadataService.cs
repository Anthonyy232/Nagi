using Nagi.Core.Models;

namespace Nagi.Core.Services.Abstractions;

public interface IMetadataService
{
    /// <summary>
    ///     Asynchronously extracts all relevant metadata from a single music file.
    /// </summary>
    /// <param name="baseFolderPath">
    ///     Optional. The root folder path for the library scan. When provided, the service will search
    ///     for cover art files in the directory hierarchy up to (and including) this folder if no
    ///     embedded art is found.
    /// </param>
    /// <param name="includeMediaAssets">Whether to process cover art and synchronized lyrics.</param>
    /// <returns>A <see cref="SongFileMetadata" /> object containing the extracted data and file properties.</returns>
    Task<SongFileMetadata> ExtractMetadataAsync(string filePath, string? baseFolderPath = null,
        bool includeMediaAssets = true);
}
