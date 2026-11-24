using System.Text.Json;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Nagi.Core.Artwork;
using Nagi.Core.Library;
using Nagi.Core.OnlineProviders;
using Nagi.Core.Playback;
using Nagi.Core.Presence;
using Nagi.Core.Settings;
using Nagi.Core.Tests.Utils;
using NSubstitute;
using Xunit;

namespace Nagi.Core.Tests.Presence;

public class DiscordPresenceServiceTests
{
    private readonly IMusicPlaybackService _playback = Substitute.For<IMusicPlaybackService>();
    private readonly IAlbumCoverService _albumCovers = Substitute.For<IAlbumCoverService>();
    private readonly SettingsService _settings = TestSettings.Create();

    public DiscordPresenceServiceTests() => _playback.PlaybackRate.Returns(1.0);

    [Fact]
    public async Task TurningRichPresenceOffAndOn_ReconnectsWithANewClient()
    {
        var configuration = Substitute.For<IConfiguration>();
        configuration["Discord:AppId"].Returns("123456789");
        await using var discord = new FakeDiscord();
        var clients = 0;
        using var service = new DiscordPresenceService(configuration,
            NullLogger<DiscordPresenceService>.Instance, _playback, _albumCovers, _settings, appId =>
            {
                clients++;
                return new DiscordIpcClient(appId, NullLogger.Instance, () => [discord.PipeName]);
            });

        service.Start();
        clients.Should().Be(0);
        var first = discord.AcceptAsync();
        _settings.Set(CoreSettings.DiscordRichPresence, true);
        await first;
        _settings.Set(CoreSettings.DiscordRichPresence, false);
        await discord.WaitForCloseAsync();
        var second = discord.AcceptAsync();
        _settings.Set(CoreSettings.DiscordRichPresence, true);
        await second;

        clients.Should().Be(2);
        service.Stop();
        await discord.WaitForCloseAsync();
    }

    [Fact]
    public async Task Pausing_KeepsTheArtistOnTheStatus()
    {
        await using var discord = new FakeDiscord();
        using var service = await StartPlayingAsync(discord);

        _playback.IsPlaying.Returns(false);
        _playback.PlaybackStateChanged += Raise.Event<Action>();

        var paused = JsonElement.Parse(await discord.ReadActivityAsync()).GetProperty("state").GetString()!;
        _playback.CurrentTrack!.ArtistName = string.Join(", ", Enumerable.Repeat("Kalimba Orchestra", 12));
        _playback.PlaybackStateChanged += Raise.Event<Action>();
        var longCredit = JsonElement.Parse(await discord.ReadActivityAsync()).GetProperty("state").GetString();

        paused.Should().Contain("Kalimba").And.Contain("3:00");
        // Discord's limit cuts the artist, not the paused text.
        longCredit.Should().HaveLength(128).And.StartWith(paused.Replace("Kalimba", "", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Start_WhenAppIdIsMissing_DoesNotCreateClient(string? appId)
    {
        var configuration = Substitute.For<IConfiguration>();
        configuration["Discord:AppId"].Returns(appId);
        var createClient = Substitute.For<Func<string, DiscordIpcClient>>();
        _settings.Set(CoreSettings.DiscordRichPresence, true);
        using var service = new DiscordPresenceService(configuration,
            NullLogger<DiscordPresenceService>.Instance, _playback, _albumCovers, _settings, createClient);

        service.Start();

        createClient.DidNotReceive().Invoke(Arg.Any<string>());
    }

    [Theory]
    [InlineData(1.0, 1, false)]
    [InlineData(1.0, 30, true)]
    [InlineData(1.0, -30, true)]
    [InlineData(1.5, 1, false)]
    public void IsOutOfSync_DetectsSeeksButNotReportingLagOrPlaybackSpeed(double playbackRate,
        int secondsFromExpected, bool expected)
    {
        var now = DateTimeOffset.UtcNow;
        var timestamps = new DiscordTimestamps((now - TimeSpan.FromMinutes(1)).ToUnixTimeMilliseconds(), null);
        var progress = TimeSpan.FromMinutes(1) * playbackRate + TimeSpan.FromSeconds(secondsFromExpected);

        DiscordPresenceService.IsOutOfSync(timestamps, progress, playbackRate, now).Should().Be(expected);
    }

    [Theory]
    [InlineData(true, 40, true)]
    [InlineData(true, 257, false)]
    [InlineData(false, 40, false)]
    public async Task FindCoverUrlAsync_LooksUpEachAlbumOnceWhenOnlineMetadataIsOn(bool onlineMetadata, int urlLength,
        bool expectCover)
    {
        var url = "https://covers.test/".PadRight(urlLength, 'x');
        var expected = expectCover ? url : null;
        _albumCovers.FindCoverUrlAsync(default!, default)
            .ReturnsForAnyArgs(onlineMetadata ? ServiceResult<string>.FromSuccess(url) : null);
        var album = new Album { Title = "Album" };
        using var service = new DiscordPresenceService(Substitute.For<IConfiguration>(),
            NullLogger<DiscordPresenceService>.Instance, _playback, _albumCovers, Substitute.For<ISettingsService>(),
            Substitute.For<Func<string, DiscordIpcClient>>());

        var first = await service.FindCoverUrlAsync(new Song { Title = "One", Album = album, MusicBrainzReleaseId = "release" });
        var second = await service.FindCoverUrlAsync(new Song { Title = "Two", Album = album });

        first.Should().Be(expected);
        second.Should().Be(expected);
        // Without online metadata there is no answer to keep, so the next song asks again.
        await _albumCovers.Received(onlineMetadata ? 1 : 2).FindCoverUrlAsync(Arg.Any<AlbumCoverQuery>(), Arg.Any<CancellationToken>());
        await _albumCovers.Received(1).FindCoverUrlAsync(
            Arg.Is<AlbumCoverQuery>(q => q.Title == "Album" && q.ReleaseId == "release"), Arg.Any<CancellationToken>());
    }

    // Connected, with the playing status already read.
    private async Task<DiscordPresenceService> StartPlayingAsync(FakeDiscord discord)
    {
        var configuration = Substitute.For<IConfiguration>();
        configuration["Discord:AppId"].Returns("123456789");
        _playback.CurrentTrack.Returns(new Song
        {
            Title = "Song",
            ArtistName = "Kalimba",
            Duration = TimeSpan.FromMinutes(3)
        });
        _playback.IsPlaying.Returns(true);
        _settings.Set(CoreSettings.DiscordRichPresence, true);
        var service = new DiscordPresenceService(configuration, NullLogger<DiscordPresenceService>.Instance,
            _playback, _albumCovers, _settings,
            appId => new DiscordIpcClient(appId, NullLogger.Instance, () => [discord.PipeName]));
        var accepted = discord.AcceptAsync();
        service.Start();
        await accepted;
        await discord.ReadActivityAsync();
        return service;
    }
}
