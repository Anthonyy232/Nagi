using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;
using Nagi.Core.Services.Implementations;
using NSubstitute;
using Xunit;

namespace Nagi.Core.Tests;

public class AlbumCoverServiceTests
{
    private static readonly ServiceResult<string> NotFound = ServiceResult<string>.FromSuccessNotFound();

    private readonly ICoverArtArchiveService _coverArtArchive = Substitute.For<ICoverArtArchiveService>();
    private readonly IMusicBrainzService _musicBrainz = Substitute.For<IMusicBrainzService>();
    private readonly IAppleMusicService _appleMusic = Substitute.For<IAppleMusicService>();
    private readonly IDeezerService _deezer = Substitute.For<IDeezerService>();
    private readonly ILastFmMetadataService _lastFm = Substitute.For<ILastFmMetadataService>();
    private readonly AlbumCoverService _service;

    public AlbumCoverServiceTests()
    {
        _coverArtArchive.GetReleaseCoverUrlAsync(default!, default).ReturnsForAnyArgs(NotFound);
        _coverArtArchive.GetReleaseGroupCoverUrlAsync(default!, default).ReturnsForAnyArgs(NotFound);
        _musicBrainz.SearchReleaseGroupAsync(default!, default!, default).ReturnsForAnyArgs(NotFound);
        _appleMusic.GetAlbumCoverUrlAsync(default!, default!, default).ReturnsForAnyArgs(NotFound);
        _deezer.GetAlbumCoverUrlAsync(default!, default!, default).ReturnsForAnyArgs(NotFound);
        _lastFm.GetAlbumCoverUrlAsync(default!, default!, default).ReturnsForAnyArgs(NotFound);
        _service = new AlbumCoverService(_coverArtArchive, _musicBrainz, _appleMusic, _deezer, _lastFm,
            NullLogger<AlbumCoverService>.Instance);
    }

    private static List<ServiceProviderSetting> Providers(params string[] ids) =>
        ids.Select(id => new ServiceProviderSetting { Id = id }).ToList();

    [Fact]
    public async Task FindCoverUrlAsync_StopsAtTheFirstSourceInPriorityOrderThatHasACover()
    {
        _deezer.GetAlbumCoverUrlAsync("Album", "Artist", default).ReturnsForAnyArgs(ServiceResult<string>.FromSuccess("deezer"));
        _appleMusic.GetAlbumCoverUrlAsync("Album", "Artist", default).ReturnsForAnyArgs(ServiceResult<string>.FromSuccess("apple"));

        var result = await _service.FindCoverUrlAsync(new AlbumCoverQuery("Album", "Artist"),
            Providers(ServiceProviderIds.LastFm, ServiceProviderIds.Deezer, ServiceProviderIds.AppleMusic));

        result.Data.Should().Be("deezer");
        await _appleMusic.DidNotReceiveWithAnyArgs().GetAlbumCoverUrlAsync(default!, default!, default);
    }

    [Fact]
    public async Task FindCoverUrlAsync_FallsBackFromATaggedReleaseToItsTaggedReleaseGroup()
    {
        _coverArtArchive.GetReleaseGroupCoverUrlAsync("group-id", Arg.Any<CancellationToken>())
            .Returns(ServiceResult<string>.FromSuccess("group-cover"));

        var result = await _service.FindCoverUrlAsync(new AlbumCoverQuery("Album", "Artist", "release-id", "group-id"),
            Providers(ServiceProviderIds.MusicBrainz, ServiceProviderIds.CoverArtArchive));

        result.Data.Should().Be("group-cover");
        await _coverArtArchive.Received(1).GetReleaseCoverUrlAsync("release-id", Arg.Any<CancellationToken>());
        await _musicBrainz.DidNotReceiveWithAnyArgs().SearchReleaseGroupAsync(default!, default!, default);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FindCoverUrlAsync_SearchesMusicBrainzForUntaggedAlbumsOnlyWhenItIsEnabled(bool musicBrainzEnabled)
    {
        _musicBrainz.SearchReleaseGroupAsync("Album", "Artist", Arg.Any<CancellationToken>())
            .Returns(ServiceResult<string>.FromSuccess("found-group"));
        _coverArtArchive.GetReleaseGroupCoverUrlAsync("found-group", Arg.Any<CancellationToken>())
            .Returns(ServiceResult<string>.FromSuccess("searched-cover"));
        var providers = musicBrainzEnabled
            ? Providers(ServiceProviderIds.MusicBrainz, ServiceProviderIds.CoverArtArchive)
            : Providers(ServiceProviderIds.CoverArtArchive);

        var result = await _service.FindCoverUrlAsync(new AlbumCoverQuery("Album (Remastered 2011)", "Artist"), providers);

        result.Data.Should().Be(musicBrainzEnabled ? "searched-cover" : null);
        result.IsConclusive.Should().BeTrue();
    }

    [Fact]
    public async Task FindCoverUrlAsync_NeverSearchesByNameForAnUnknownAlbum()
    {
        var result = await _service.FindCoverUrlAsync(new AlbumCoverQuery(Album.UnknownAlbumName, "Artist"),
            Providers(ServiceProviderIds.MusicBrainz, ServiceProviderIds.CoverArtArchive, ServiceProviderIds.AppleMusic,
                ServiceProviderIds.Deezer, ServiceProviderIds.LastFm));

        result.Status.Should().Be(ServiceResultStatus.SuccessNotFound);
        _musicBrainz.ReceivedCalls().Should().BeEmpty();
        _appleMusic.ReceivedCalls().Should().BeEmpty();
        _deezer.ReceivedCalls().Should().BeEmpty();
        _lastFm.ReceivedCalls().Should().BeEmpty();
    }

    [Theory]
    [InlineData(false, ServiceResultStatus.TemporaryError)]
    [InlineData(true, ServiceResultStatus.Success)]
    public async Task FindCoverUrlAsync_IsRetryableWhenASourceFailedAndNoOtherHadACover(bool laterSourceHasCover,
        ServiceResultStatus expected)
    {
        _appleMusic.GetAlbumCoverUrlAsync("Album", "Artist", default).ReturnsForAnyArgs(ServiceResult<string>.FromTemporaryError());
        if (laterSourceHasCover)
            _deezer.GetAlbumCoverUrlAsync("Album", "Artist", default).ReturnsForAnyArgs(ServiceResult<string>.FromSuccess("deezer"));

        var result = await _service.FindCoverUrlAsync(new AlbumCoverQuery("Album", "Artist"),
            Providers(ServiceProviderIds.AppleMusic, ServiceProviderIds.Deezer));

        result.Status.Should().Be(expected);
    }
}
