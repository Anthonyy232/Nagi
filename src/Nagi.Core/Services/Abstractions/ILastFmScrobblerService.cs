using Nagi.Core.Models;

namespace Nagi.Core.Services.Abstractions;

public interface ILastFmScrobblerService
{
    Task<bool> UpdateNowPlayingAsync(Song song);

    /// <param name="song">The song that was played.</param>
    Task<bool> ScrobbleAsync(Song song, DateTime playStartTime);
}
