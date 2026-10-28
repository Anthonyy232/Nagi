using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nagi.Core.Models;
using Nagi.Core.Services.Data;
using Nagi.Core.Services.Implementations;
using Nagi.Core.Tests.Utils;
using NSubstitute;
using Xunit;

namespace Nagi.Core.Tests;

public class AlbumCoverSourceTests : IDisposable
{
    private readonly TestHttpMessageHandler _httpHandler = new();
    private readonly IHttpClientFactory _httpClientFactory = Substitute.For<IHttpClientFactory>();

    public AlbumCoverSourceTests()
    {
        _httpClientFactory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(_httpHandler, disposeHandler: false));
    }

    public void Dispose()
    {
        _httpHandler.Dispose();
    }

    [Fact]
    public async Task CoverArtArchive_UsesTheFrontImagesLargeThumbnailOverHttps()
    {
        _httpHandler.RespondSequence((HttpStatusCode.OK, """
            {"images":[
              {"front":false,"image":"https://caa.test/back.png","thumbnails":{"1200":"https://caa.test/back-1200.jpg"}},
              {"front":true,"image":"https://caa.test/front.png","thumbnails":{"1200":"http://caa.test/front-1200.jpg","large":"https://caa.test/front-500.jpg"}}
            ]}
            """));
        await using var pipelines = TestProviderPipeline.Build(ServiceProviderIds.CoverArtArchive);
        var service = new CoverArtArchiveService(_httpClientFactory, pipelines, NullLogger<CoverArtArchiveService>.Instance);

        var result = await service.GetReleaseGroupCoverUrlAsync("group-id");

        result.Data.Should().Be("https://caa.test/front-1200.jpg");
        _httpHandler.Requests[0].RequestUri!.AbsoluteUri.Should().Be("https://coverartarchive.org/release-group/group-id");
    }

    [Fact]
    public async Task CoverArtArchive_ReleaseWithoutArtIsNotFound()
    {
        _httpHandler.RespondSequence((HttpStatusCode.NotFound, ""));
        await using var pipelines = TestProviderPipeline.Build(ServiceProviderIds.CoverArtArchive);
        var service = new CoverArtArchiveService(_httpClientFactory, pipelines, NullLogger<CoverArtArchiveService>.Instance);

        var result = await service.GetReleaseCoverUrlAsync("release-id");

        result.Status.Should().Be(ServiceResultStatus.SuccessNotFound);
    }

    [Fact]
    public async Task AppleMusic_MatchesArtistAndTitleAndAsksForLargeArtwork()
    {
        _httpHandler.RespondSequence((HttpStatusCode.OK, """
            {"resultCount":2,"results":[
              {"artistName":"Radiohead Tribute","collectionName":"OK Computer","artworkUrl100":"https://img.test/other/100x100bb.jpg"},
              {"artistName":"Radiohead","collectionName":"OK Computer (Remastered)","artworkUrl100":"https://img.test/real/100x100bb.jpg"}
            ]}
            """));
        await using var pipelines = TestProviderPipeline.Build(ServiceProviderIds.AppleMusic);
        var service = new AppleMusicService(_httpClientFactory, pipelines, NullLogger<AppleMusicService>.Instance);

        var result = await service.GetAlbumCoverUrlAsync("OK Computer", "Radiohead");

        result.Data.Should().Be("https://img.test/real/1200x1200bb.jpg");
        _httpHandler.Requests[0].RequestUri!.Query.Should().Contain("term=Radiohead%20OK%20Computer").And.Contain("entity=album");
    }
}
