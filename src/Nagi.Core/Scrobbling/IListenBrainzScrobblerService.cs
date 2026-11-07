namespace Nagi.Core.Scrobbling;

public interface IListenBrainzScrobblerService
{
    /// <param name="serverUrl">The ListenBrainz server to ask; null or blank asks the default one.</param>
    Task<ValidateTokenResult> ValidateTokenAsync(string token, string? serverUrl);
}
