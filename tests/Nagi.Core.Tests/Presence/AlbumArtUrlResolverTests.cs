using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nagi.Core.Http.Pipelines;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;
using Nagi.Core.Services.Implementations.Presence;
using Nagi.Core.Tests.Utils;
using NSubstitute;
using Xunit;

namespace Nagi.Core.Tests.Presence;

public class AlbumArtUrlResolverTests : IDisposable
{
    private const string ReleaseGroupId = "b5b4bb4b-8ba5-3acf-88cb-4cae2699d8da";
    private const string CoverUrl = "https://archive.org/download/cover-500.jpg";

    private readonly TestHttpMessageHandler _httpHandler;
    private readonly IMusicBrainzService _musicBrainz;
    private readonly ProviderPipelineProvider _pipelines;
    private readonly AlbumArtUrlResolver _resolver;

    public AlbumArtUrlResolverTests()
    {
        _httpHandler = new TestHttpMessageHandler();
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient(Arg.Any<string>()).Returns(new HttpClient(_httpHandler));

        _musicBrainz = Substitute.For<IMusicBrainzService>();
        _pipelines = TestProviderPipeline.Build(ServiceProviderIds.ImageDownload);
        _resolver = new AlbumArtUrlResolver(httpClientFactory, _pipelines, _musicBrainz,
            NullLogger<AlbumArtUrlResolver>.Instance);
    }

    public void Dispose()
    {
        _pipelines.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _httpHandler.Dispose();
    }

    [Fact]
    public async Task GetArtworkUrlAsync_ReturnsFrontCoverThumbnail()
    {
        ArrangeMusicBrainzMatch();
        RespondWithListing(new
        {
            images = new[]
            {
                new
                {
                    front = true,
                    image = "https://archive.org/download/cover-full.jpg",
                    thumbnails = new Dictionary<string, string>
                    {
                        ["250"] = "https://archive.org/download/cover-250.jpg",
                        ["500"] = CoverUrl
                    }
                }
            }
        });

        var url = await _resolver.GetArtworkUrlAsync(CreateSong());

        url.Should().Be(CoverUrl);
    }

    [Fact]
    public async Task GetArtworkUrlAsync_UpgradesPlainHttpImagesToHttps()
    {
        ArrangeMusicBrainzMatch();
        RespondWithListing(new
        {
            images = new[]
            {
                new
                {
                    front = true,
                    image = "http://coverartarchive.org/release/cover-full.jpg",
                    thumbnails = new Dictionary<string, string>
                    {
                        ["500"] = "http://coverartarchive.org/release/cover-500.jpg"
                    }
                }
            }
        });

        var url = await _resolver.GetArtworkUrlAsync(CreateSong());

        url.Should().Be("https://coverartarchive.org/release/cover-500.jpg");
    }

    [Fact]
    public async Task GetArtworkUrlAsync_CachesResultPerAlbum()
    {
        ArrangeMusicBrainzMatch();
        RespondWithListing(new
        {
            images = new[]
            {
                new { front = true, image = CoverUrl, thumbnails = new Dictionary<string, string>() }
            }
        });
        var song = CreateSong();

        await _resolver.GetArtworkUrlAsync(song);
        await _resolver.GetArtworkUrlAsync(song);

        await _musicBrainz.Received(1).SearchReleaseGroupAsync("Hybrid Theory", "Linkin Park",
            Arg.Any<CancellationToken>());
        _httpHandler.Requests.Should().HaveCount(1);
    }

    [Fact]
    public async Task GetArtworkUrlAsync_WhenNoReleaseGroupMatch_ReturnsNullAndCachesMiss()
    {
        _musicBrainz.SearchReleaseGroupAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ServiceResult<string>.FromSuccessNotFound());
        var song = CreateSong();

        var first = await _resolver.GetArtworkUrlAsync(song);
        var second = await _resolver.GetArtworkUrlAsync(song);

        first.Should().BeNull();
        second.Should().BeNull();
        await _musicBrainz.Received(1).SearchReleaseGroupAsync(Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<CancellationToken>());
        _httpHandler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task GetArtworkUrlAsync_WhenCoverArtArchiveHasNoImages_ReturnsNull()
    {
        ArrangeMusicBrainzMatch();
        RespondWithListing(new { images = Array.Empty<object>() });

        var url = await _resolver.GetArtworkUrlAsync(CreateSong());

        url.Should().BeNull();
    }

    [Fact]
    public async Task GetArtworkUrlAsync_WhenProviderIsUnavailable_DoesNotCacheTheMiss()
    {
        _musicBrainz.SearchReleaseGroupAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ServiceResult<string>.FromTemporaryError());
        var song = CreateSong();

        await _resolver.GetArtworkUrlAsync(song);
        await _resolver.GetArtworkUrlAsync(song);

        await _musicBrainz.Received(2).SearchReleaseGroupAsync(Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    private static Song CreateSong()
    {
        var albumId = Guid.NewGuid();
        return new Song
        {
            Title = "Papercut",
            ArtistName = "Linkin Park",
            AlbumId = albumId,
            Album = new Album { Id = albumId, Title = "Hybrid Theory", ArtistName = "Linkin Park" }
        };
    }

    private void ArrangeMusicBrainzMatch() =>
        _musicBrainz.SearchReleaseGroupAsync("Hybrid Theory", "Linkin Park", Arg.Any<CancellationToken>())
            .Returns(ServiceResult<string>.FromSuccess(ReleaseGroupId));

    private void RespondWithListing(object listing) =>
        _httpHandler.SendAsyncFunc = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(listing))
        });
}
