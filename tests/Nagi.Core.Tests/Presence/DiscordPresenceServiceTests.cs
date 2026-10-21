using DiscordRPC;
using DiscordRPC.IO;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Implementations.Presence;
using NSubstitute;
using Xunit;

namespace Nagi.Core.Tests.Presence;

public class DiscordPresenceServiceTests
{
    [Fact]
    public async Task InitializeAsync_AfterDeactivation_RecreatesClientAndUpdateTimer()
    {
        var configuration = Substitute.For<IConfiguration>();
        configuration["Discord:AppId"].Returns("123456789");
        var clients = new List<DiscordRpcClient>();
        await using var service = new DiscordPresenceService(configuration,
            NullLogger<DiscordPresenceService>.Instance, Substitute.For<IAlbumArtUrlResolver>(), appId =>
            {
                var client = new DiscordRpcClient(appId, client: Substitute.For<INamedPipeClient>());
                clients.Add(client);
                return client;
            });

        await service.InitializeAsync();
        await service.OnTrackChangedAsync(new Song { Title = "First activation" }, 1);
        await service.DisposeAsync();
        await service.InitializeAsync();
        await service.OnTrackChangedAsync(new Song { Title = "Second activation" }, 2);

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
            NullLogger<DiscordPresenceService>.Instance, Substitute.For<IAlbumArtUrlResolver>(), createClient);

        await service.InitializeAsync();

        createClient.DidNotReceive().Invoke(Arg.Any<string>());
    }

    [Fact]
    public void BuildPresence_WhenPlayingWithArtwork_ShowsListeningActivityAndCover()
    {
        var song = new Song
        {
            Title = "From Zero",
            ArtistName = "Linkin Park",
            Album = new Album { Title = "From Zero (Live 2024)" }
        };
        var timestamps = new Timestamps { Start = DateTime.UtcNow, End = DateTime.UtcNow.AddMinutes(5) };

        var presence = DiscordPresenceService.BuildPresence(song, true, TimeSpan.FromSeconds(42), timestamps,
            "https://archive.org/download/cover.jpg");

        presence.Type.Should().Be(ActivityType.Listening);
        presence.StatusDisplay.Should().Be(StatusDisplayType.Name);
        presence.Details.Should().Be("From Zero");
        presence.State.Should().Be("by Linkin Park");
        presence.Timestamps.Should().BeSameAs(timestamps);
        presence.Assets.LargeImageKey.Should().Be("https://archive.org/download/cover.jpg");
        presence.Assets.LargeImageText.Should().Be("From Zero (Live 2024)");
        presence.Assets.SmallImageKey.Should().Be("logo");
        presence.Assets.SmallImageText.Should().Be("Nagi");
    }

    [Fact]
    public void BuildPresence_WhenPausedWithoutArtwork_UsesLogoAndHidesTimestamps()
    {
        var song = new Song
        {
            Title = "Papercut",
            ArtistName = "Linkin Park",
            Duration = TimeSpan.FromMinutes(3)
        };

        var presence = DiscordPresenceService.BuildPresence(song, false, TimeSpan.FromSeconds(31), null, null);

        presence.State.Should().Be("Paused | 0:31 / 3:00");
        presence.Timestamps.Should().BeNull();
        presence.Assets.LargeImageKey.Should().Be("logo");
        presence.Assets.SmallImageKey.Should().Be("logo");
        presence.Assets.SmallImageText.Should().Be("Nagi");
    }

    [Fact]
    public void BuildPresence_WithoutTrack_ShowsIdleListeningActivity()
    {
        var presence = DiscordPresenceService.BuildPresence(null, false, TimeSpan.Zero, null, null);

        presence.Type.Should().Be(ActivityType.Listening);
        presence.Details.Should().Be("Browsing Music Library");
        presence.State.Should().Be("Idling");
        presence.Assets.Should().BeNull();
    }
}
