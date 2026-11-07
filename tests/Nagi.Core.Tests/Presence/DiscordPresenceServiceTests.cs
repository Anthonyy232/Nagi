using DiscordRPC;
using DiscordRPC.IO;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;
using Nagi.Core.Services.Implementations;
using Nagi.Core.Services.Implementations.Presence;
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
    public async Task InitializeAsync_AfterDeactivation_RecreatesClientAndUpdateTimer()
    {
        var configuration = Substitute.For<IConfiguration>();
        configuration["Discord:AppId"].Returns("123456789");
        var clients = new List<DiscordRpcClient>();
        await using var service = new DiscordPresenceService(configuration,
            NullLogger<DiscordPresenceService>.Instance, _playback, _albumCovers, Substitute.For<ISettingsService>(), appId =>
            {
                var client = new DiscordRpcClient(appId, client: Substitute.For<INamedPipeClient>());
                clients.Add(client);
                return client;
            });

        await service.InitializeAsync();
        await service.OnTrackChangedAsync(new Song { Title = "First activation" });
        await service.DisposeAsync();
        await service.InitializeAsync();
        await service.OnTrackChangedAsync(new Song { Title = "Second activation" });

        clients.Should().HaveCount(2);
        clients[0].IsDisposed.Should().BeTrue();
        clients[1].IsInitialized.Should().BeTrue();
        clients[1].IsDisposed.Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task InitializeAsync_WhenAppIdIsMissing_DoesNotCreateClient(string? appId)
    {
        var configuration = Substitute.For<IConfiguration>();
        configuration["Discord:AppId"].Returns(appId);
        var createClient = Substitute.For<Func<string, DiscordRpcClient>>();
        await using var service = new DiscordPresenceService(configuration,
            NullLogger<DiscordPresenceService>.Instance, _playback, _albumCovers, Substitute.For<ISettingsService>(), createClient);

        await service.InitializeAsync();

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
        var now = DateTime.UtcNow;
        var timestamps = new Timestamps { Start = now - TimeSpan.FromMinutes(1) };
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
        await using var service = new DiscordPresenceService(Substitute.For<IConfiguration>(),
            NullLogger<DiscordPresenceService>.Instance, _playback, _albumCovers, Substitute.For<ISettingsService>(),
            Substitute.For<Func<string, DiscordRpcClient>>());

        var first = await service.FindCoverUrlAsync(new Song { Title = "One", Album = album, MusicBrainzReleaseId = "release" });
        var second = await service.FindCoverUrlAsync(new Song { Title = "Two", Album = album });

        first.Should().Be(expected);
        second.Should().Be(expected);
        // Without online metadata there is no answer to keep, so the next song asks again.
        await _albumCovers.Received(onlineMetadata ? 1 : 2).FindCoverUrlAsync(Arg.Any<AlbumCoverQuery>(), Arg.Any<CancellationToken>());
        await _albumCovers.Received(1).FindCoverUrlAsync(
            Arg.Is<AlbumCoverQuery>(q => q.Title == "Album" && q.ReleaseId == "release"), Arg.Any<CancellationToken>());
    }
}
