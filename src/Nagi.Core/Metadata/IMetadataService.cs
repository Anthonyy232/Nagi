namespace Nagi.Core.Metadata;

public interface IMetadataService
{
    /// <param name="baseFolderPath">
    ///     The root folder of the library scan. When given, a cover art file in the song's folder or a parent
    ///     folder, up to and including this one, is used before embedded art.
    /// </param>
    /// <param name="includeMediaAssets">Whether to process cover art and synchronized lyrics.</param>
    /// <returns>
    ///     The file's tags, cleaned up the way the library stores them even when the file couldn't be read, which
    ///     <see cref="SongFileMetadata.Failure" /> tells.
    /// </returns>
    Task<SongFileMetadata> ExtractMetadataAsync(string filePath, string? baseFolderPath = null,
        bool includeMediaAssets = true);

    /// <summary>The lyrics file <see cref="ExtractMetadataAsync" /> gives the song, without processing its cover art.</summary>
    Task<string?> GetLyricsPathAsync(string filePath);

    /// <summary>
    ///     Writes ReplayGain track tags that <see cref="ExtractMetadataAsync" /> reads back. Logs, rather than throws,
    ///     when the file can't be written.
    /// </summary>
    void WriteReplayGain(string filePath, double gainDb, double peak);
}
