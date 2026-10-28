using Nagi.Core.Models;

namespace Nagi.Core.Services.Abstractions;

public interface ILastFmScrobblerService
{
    Task<bool> UpdateNowPlayingAsync(Song song);

    Task<bool> ScrobbleAsync(Song song, DateTime playStartTime);
}
