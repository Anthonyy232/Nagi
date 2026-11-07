using Nagi.Core.Library;
using Nagi.Core.Models;
using Nagi.Core.Services.Data;

namespace Nagi.Core.Services.Abstractions;

/// <summary>An online source of lyrics.</summary>
public interface ILyricsProvider
{
    /// <summary>The provider's id in <see cref="ServiceProviderIds" />, as the lyrics provider settings name it.</summary>
    string Id { get; }

    /// <returns>Raw LRC or plain text.</returns>
    Task<ServiceResult<string>> GetLyricsAsync(Song song, CancellationToken cancellationToken = default);
}
