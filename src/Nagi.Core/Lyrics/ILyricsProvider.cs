using Nagi.Core.Library;
using Nagi.Core.OnlineProviders;

namespace Nagi.Core.Lyrics;

/// <summary>An online source of lyrics.</summary>
public interface ILyricsProvider
{
    /// <summary>The provider's id in <see cref="ProviderCatalog" />.</summary>
    string Id { get; }

    /// <returns>Raw LRC or plain text.</returns>
    Task<ServiceResult<string>> GetLyricsAsync(Song song, CancellationToken cancellationToken = default);
}
