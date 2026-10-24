using System.Net;
using System.Text;
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

public class WikipediaServiceTests : IDisposable
{
    private const string Mbid = "a74b1b7f-71a5-4011-9441-d0b5e4122711";
    private const string SearchResponse = """{"query":{"search":[{"title":"Q44191"}]}}""";

    private readonly TestHttpMessageHandler _httpHandler = new();
    private readonly ProviderPipelineProvider _pipelines = TestProviderPipeline.Build(ServiceProviderIds.Wikipedia);
    private readonly WikipediaService _service;

    public WikipediaServiceTests()
    {
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient(Arg.Any<string>()).Returns(new HttpClient(_httpHandler));
        _service = new WikipediaService(httpClientFactory, _pipelines, NullLogger<WikipediaService>.Instance);
    }

    public void Dispose()
    {
        _service.Dispose();
        _pipelines.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _httpHandler.Dispose();
    }

    [Theory]
    [InlineData("fr", """{"frwiki":{"title":"Radiohead (groupe)"},"enwiki":{"title":"Radiohead"}}""", "https://fr.wikipedia.org/", "Radiohead%20%28groupe%29")]
    [InlineData("nb", """{"nowiki":{"title":"Radiohead"},"enwiki":{"title":"Radiohead"}}""", "https://no.wikipedia.org/", "Radiohead")]
    [InlineData("de", """{"enwiki":{"title":"Radiohead"}}""", "https://en.wikipedia.org/", "Radiohead")]
    public async Task GetArtistBiographyAsync_ReadsTheLinkedArticleInTheUserLanguageOrEnglish(
        string language, string sitelinks, string expectedHost, string expectedTitle)
    {
        _httpHandler.RespondSequence(
            (HttpStatusCode.OK, SearchResponse),
            (HttpStatusCode.OK, """{"entities":{"Q44191":{"sitelinks":""" + sitelinks + "}}}"),
            (HttpStatusCode.OK, """{"query":{"pages":[{"extract":"  Radiohead is a band.\nFormed in 1985.  "}]}}"""));

        var result = await _service.GetArtistBiographyAsync(Mbid, language);

        result.Data.Should().Be("Radiohead is a band.\nFormed in 1985.\n\nFrom Wikipedia, under the CC BY-SA license.");
        var urls = _httpHandler.Requests.Select(r => r.RequestUri!.AbsoluteUri).ToList();
        urls[0].Should().Contain($"haswbstatement:P434={Mbid}");
        urls[1].Should().Contain("ids=Q44191");
        urls[2].Should().StartWith(expectedHost).And.EndWith($"titles={expectedTitle}");
    }

    [Fact]
    public async Task GetArtistBiographyAsync_WithoutAWikidataEntry_StopsAfterTheSearch()
    {
        _httpHandler.RespondSequence((HttpStatusCode.OK, """{"query":{"search":[]}}"""));

        var result = await _service.GetArtistBiographyAsync(Mbid, "en");

        result.Status.Should().Be(ServiceResultStatus.SuccessNotFound);
        _httpHandler.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task GetArtistBiographyAsync_WhenWikidataFails_IsRetryable()
    {
        _httpHandler.RespondSequence((HttpStatusCode.OK, SearchResponse));
        _httpHandler.SendAsyncFunc = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        });

        var result = await _service.GetArtistBiographyAsync(Mbid, "en");

        result.Status.Should().Be(ServiceResultStatus.TemporaryError);
    }
}
