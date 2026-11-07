using FluentAssertions;
using Nagi.Core.Scrobbling;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Implementations;
using Nagi.Core.Settings;
using Nagi.Core.Tests.Utils;
using NSubstitute;
using Xunit;

namespace Nagi.Core.Tests;

public class ScrobblingAccountsTests
{
    private readonly ICredentialLockerService _credentials = Substitute.For<ICredentialLockerService>();
    private readonly SettingsService _settings;
    private readonly ScrobblingAccounts _accounts;

    public ScrobblingAccountsTests()
    {
        _credentials.SaveCredential(default!, default!, default!).ReturnsForAnyArgs(true);
        _settings = TestSettings.Create(credentials: _credentials);
        _accounts = new ScrobblingAccounts(_settings);
    }

    [Fact]
    public async Task DisconnectLastFm_TurnsSubmissionOffAndDropsThePendingSignIn()
    {
        await _accounts.ConnectLastFmAsync("user", "session");
        _settings.Set(CoreSettings.LastFmAuthToken, "pending");

        await _accounts.DisconnectLastFmAsync();

        _credentials.Received(1).RemoveCredential("Nagi/LastFm");
        _settings.Get(CoreSettings.LastFmScrobbling).Should().BeFalse();
        _settings.Get(CoreSettings.LastFmNowPlaying).Should().BeFalse();
        _settings.Get(CoreSettings.LastFmAuthToken).Should().BeNull();
    }

    [Fact]
    public async Task ListenBrainz_CountsListensFromTheFirstConnectionUntilDisconnected()
    {
        (await _accounts.ConnectListenBrainzAsync("token")).Should().BeTrue();
        var since = _settings.Get(CoreSettings.ListenBrainzEnabledSinceUtc);
        since.Should().NotBeNull();
        (await _accounts.ConnectListenBrainzAsync("new-token")).Should().BeTrue();
        _settings.Get(CoreSettings.ListenBrainzEnabledSinceUtc).Should().Be(since);
        _settings.Set(CoreSettings.ListenBrainzScrobbling, true);

        await _accounts.DisconnectListenBrainzAsync();

        _credentials.Received(1).RemoveCredential("Nagi/ListenBrainz");
        _settings.Get(CoreSettings.ListenBrainzScrobbling).Should().BeFalse();
        _settings.Get(CoreSettings.ListenBrainzEnabledSinceUtc).Should().BeNull();
    }

    [Fact]
    public async Task Connect_TokenTheCredentialStoreRefuses_ChangesNoSettings()
    {
        _credentials.SaveCredential(default!, default!, default!).ReturnsForAnyArgs(false);

        (await _accounts.ConnectLastFmAsync("user", "session")).Should().BeFalse();
        (await _accounts.ConnectListenBrainzAsync("token")).Should().BeFalse();

        _settings.Get(CoreSettings.LastFmScrobbling).Should().BeFalse();
        _settings.Get(CoreSettings.ListenBrainzEnabledSinceUtc).Should().BeNull();
    }
}
