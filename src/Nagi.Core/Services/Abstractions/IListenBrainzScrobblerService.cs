using Nagi.Core.Services.Data;

namespace Nagi.Core.Services.Abstractions;

public interface IListenBrainzScrobblerService
{
    /// <param name="serverUrl">The ListenBrainz server to ask; null or blank asks the default one.</param>
    Task<ValidateTokenResult> ValidateTokenAsync(string token, string? serverUrl = null);
}
