using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nagi.Core.Http.Pipelines;
using Nagi.Core.Models;
using Nagi.Core.Services.Data;
using Nagi.Core.Services.Implementations;
using Nagi.Core.Tests.Utils;
using NSubstitute;
using Xunit;

namespace Nagi.Core.Tests;

public class DeezerServiceTests : IDisposable
{
    private readonly TestHttpMessageHandler _httpHandler = new();
    private readonly ProviderPipelineProvider _pipelines = TestProviderPipeline.Build(ServiceProviderIds.Deezer);
    private readonly DeezerService _service;

    public DeezerServiceTests()
    {
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient(Arg.Any<string>()).Returns(new HttpClient(_httpHandler));
        _service = new DeezerService(httpClientFactory, _pipelines, NullLogger<DeezerService>.Instance);
    }

    public void Dispose()
    {
        _service.Dispose();
        _pipelines.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _httpHandler.Dispose();
    }

    [Fact]
    public async Task GetArtistImageUrlAsync_SkipsOtherArtistsAndPlaceholderPictures()
    {
        _httpHandler.RespondSequence((HttpStatusCode.OK, """
            {"data":[
              {"name":"Beyoncé Tribute","picture_xl":"https://cdn.test/images/artist/tribute/1000x1000.jpg"},
              {"name":"BEYONCE","picture_xl":"https://cdn.test/images/artist//1000x1000.jpg"},
              {"name":"Beyoncé","picture_xl":"https://cdn.test/images/artist/real/1000x1000.jpg"}
            ]}
            """));

        var result = await _service.GetArtistImageUrlAsync("Beyonce");

        result.Data.Should().Be("https://cdn.test/images/artist/real/1000x1000.jpg");
        _httpHandler.Requests[0].RequestUri!.Query.Should().Be("?q=Beyonce&limit=10");
    }

    [Theory]
    [InlineData("""{"data":[{"name":"Someone Else","picture_xl":"https://cdn.test/a.jpg"}]}""", ServiceResultStatus.SuccessNotFound)]
    [InlineData("""{"error":{"type":"DataException","message":"no data","code":800}}""", ServiceResultStatus.SuccessNotFound)]
    [InlineData("""{"error":{"type":"Exception","message":"Quota limit exceeded","code":4}}""", ServiceResultStatus.TemporaryError)]
    public async Task GetArtistImageUrlAsync_ReportsMissesAndErrorsReturnedInsideOkResponses(string body, ServiceResultStatus expected)
    {
        _httpHandler.RespondSequence((HttpStatusCode.OK, body));

        var result = await _service.GetArtistImageUrlAsync("Artist");

        result.Status.Should().Be(expected);
    }

    [Fact]
    public async Task GetAlbumCoverUrlAsync_SkipsOtherArtistsAndPlaceholderCovers()
    {
        _httpHandler.RespondSequence((HttpStatusCode.OK, """
            {"data":[
              {"title":"OK Computer","cover_xl":"https://cdn.test/images/cover/tribute/1000x1000.jpg","artist":{"name":"Radiohead Tribute"}},
              {"title":"OK Computer","cover_xl":"https://cdn.test/images/cover//1000x1000.jpg","artist":{"name":"Radiohead"}},
              {"title":"OK Computer [Collector's Edition]","cover_xl":"https://cdn.test/images/cover/real/1000x1000.jpg","artist":{"name":"Radiohead"}}
            ]}
            """));

        var result = await _service.GetAlbumCoverUrlAsync("OK Computer", "Radiohead");

        result.Data.Should().Be("https://cdn.test/images/cover/real/1000x1000.jpg");
    }
}
