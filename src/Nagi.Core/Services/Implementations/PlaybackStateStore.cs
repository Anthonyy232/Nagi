using System.Text.Json;
using Microsoft.Extensions.Logging;
using Nagi.Core.Helpers;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;

namespace Nagi.Core.Services.Implementations;

public sealed class PlaybackStateStore(IPathConfiguration pathConfig, ILogger<PlaybackStateStore> logger)
    : IPlaybackStateStore
{
    public async Task<PlaybackState?> LoadAsync()
    {
        try
        {
            var json = await File.ReadAllTextAsync(pathConfig.PlaybackStateFilePath).ConfigureAwait(false);
            return JsonSerializer.Deserialize<PlaybackState>(json);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Discarding invalid playback state.");
            Clear();
            return null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Error reading playback state.");
            return null;
        }
    }

    public async Task SaveAsync(PlaybackState state)
    {
        var path = pathConfig.PlaybackStateFilePath;
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(temporaryPath, JsonSerializer.Serialize(state)).ConfigureAwait(false);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporaryPath); }
            catch (Exception ex) { logger.LogWarning(ex, "Could not remove temporary playback state {Path}.", temporaryPath); }
        }
    }

    public void Clear()
    {
        try
        {
            File.Delete(pathConfig.PlaybackStateFilePath);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Error clearing playback state file.");
        }
    }
}
