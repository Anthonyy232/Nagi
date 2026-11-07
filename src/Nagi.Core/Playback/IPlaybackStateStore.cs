namespace Nagi.Core.Playback;

/// <summary>
///     The queue and current track saved between sessions.
/// </summary>
public interface IPlaybackStateStore
{
    /// <returns>The saved state, or null when none is saved or it can't be read.</returns>
    Task<PlaybackState?> LoadAsync();

    /// <summary>
    ///     Replaces the saved state. A save that fails keeps the previous state.
    /// </summary>
    Task SaveAsync(PlaybackState state);

    void Clear();
}
