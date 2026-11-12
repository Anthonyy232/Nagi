using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nagi.Core.OnlineProviders;
using Nagi.Core.OnlineProviders.Pipelines;
using Nagi.Core.Tests.Utils;
using NSubstitute;
using Xunit;

namespace Nagi.Core.Tests.OnlineProviders;

/// <summary>
///     What every provider gets from <see cref="ProviderClient" />. Provider tests cover only their own URLs, parsing
///     and body status rules.
/// </summary>
public sealed class ProviderClientTests : IDisposable
{
    private const string KeyName = "fanarttv";

    private readonly TestHttpMessageHandler _http = new();
    private readonly IApiKeyService _apiKeys = Substitute.For<IApiKeyService>();
    private readonly ProviderPipelineProvider _pipelines = TestProviderPipeline.Build(ServiceProviderIds.FanartTv);
    private readonly ProviderClient _client;
    private readonly ProviderClient _keyedClient;

    public ProviderClientTests()
    {
        var policy = ProviderCatalog.FanartTv.Policy;
        _client = new ProviderClient(policy, new HttpClient(_http), _pipelines, NullLogger.Instance);
        _keyedClient = new ProviderClient(policy, new HttpClient(_http), _pipelines, NullLogger.Instance, _apiKeys, KeyName);
        _apiKeys.GetApiKeyAsync(KeyName, Arg.Any<CancellationToken>()).Returns("first-key");
    }

    public void Dispose()
    {
        _pipelines.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _http.Dispose();
    }

    private sealed record Body(string? Name, string? Error);

    private Task<ServiceResult<string>> GetAsync(CancellationToken cancellationToken = default) =>
        _client.GetJsonAsync<Body, string>("https://provider.test/item",
            body => ServiceResult<string>.FromSuccess(body.Name!), "item", cancellationToken);

    private Task<ServiceResult<string>> GetWithKeyAsync() =>
        _keyedClient.GetJsonAsync<Body, string>(key => $"https://provider.test/item?key={key}",
            body => ServiceResult<string>.FromSuccess(body.Name!), "item", CancellationToken.None);

    private void Respond(HttpStatusCode status, string body = "{}") =>
        _http.SendAsyncFunc = (_, _) => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });

    [Fact]
    public async Task A404_IsNotFound()
    {
        Respond(HttpStatusCode.NotFound);

        (await GetAsync()).Status.Should().Be(ServiceResultStatus.SuccessNotFound);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task AnyOtherFailureStatus_IsRetryable(HttpStatusCode status)
    {
        Respond(status);

        (await GetAsync()).Status.Should().Be(ServiceResultStatus.TemporaryError);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ATransportFailureOrTimeout_IsRetryable(bool timeout)
    {
        _http.SendAsyncFunc = (_, _) => Task.FromException<HttpResponseMessage>(
            timeout ? new TaskCanceledException("Timed out") : new HttpRequestException("Offline"));

        (await GetAsync()).Status.Should().Be(ServiceResultStatus.TemporaryError);
    }

    [Fact]
    public async Task ABodyThatIsNotJson_IsRetryable()
    {
        Respond(HttpStatusCode.OK, "{ not json }");

        (await GetAsync()).Status.Should().Be(ServiceResultStatus.TemporaryError);
    }

    [Fact]
    public async Task CancellingTheCaller_IsNotTurnedIntoAResult()
    {
        using var cts = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _http.SendAsyncFunc = async (_, token) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        };

        var request = GetAsync(cts.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
    }

    [Fact]
    public async Task WithoutItsKey_NothingIsSentAndTheNextCallAsksForTheKeyAgain()
    {
        _apiKeys.GetApiKeyAsync(KeyName, Arg.Any<CancellationToken>()).Returns((string?)null, "first-key");
        Respond(HttpStatusCode.OK, """{"name":"found"}""");

        var first = await GetWithKeyAsync();
        var requestsAfterFirst = _http.Requests.Count;
        var second = await GetWithKeyAsync();

        first.Status.Should().Be(ServiceResultStatus.TemporaryError);
        requestsAfterFirst.Should().Be(0);
        second.Data.Should().Be("found");
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task ARejectedKey_IsFetchedAgainAndTheRequestRetriedOnce(HttpStatusCode rejection)
    {
        _apiKeys.RefreshApiKeyAsync(KeyName, Arg.Any<CancellationToken>()).Returns("second-key");
        _http.SendAsyncFunc = (request, _) => Task.FromResult(request.RequestUri!.Query.Contains("second-key")
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"name":"found"}""") }
            : new HttpResponseMessage(rejection));

        var result = await GetWithKeyAsync();

        result.Data.Should().Be("found");
        _http.Requests.Should().HaveCount(2);
    }

    [Theory]
    [InlineData(null, 1)]
    [InlineData("second-key", 2)]
    public async Task AKeyThatStaysRejected_IsRetryableWithoutFurtherRequests(string? refreshedKey, int requests)
    {
        _apiKeys.RefreshApiKeyAsync(KeyName, Arg.Any<CancellationToken>()).Returns(refreshedKey);
        Respond(HttpStatusCode.Unauthorized);

        var result = await GetWithKeyAsync();

        result.Status.Should().Be(ServiceResultStatus.TemporaryError);
        _http.Requests.Should().HaveCount(requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "missing", ServiceResultStatus.SuccessNotFound)]
    [InlineData(HttpStatusCode.OK, "busy", ServiceResultStatus.TemporaryError)]
    [InlineData(HttpStatusCode.BadRequest, null, ServiceResultStatus.TemporaryError)]
    [InlineData(HttpStatusCode.OK, null, ServiceResultStatus.Success)]
    public async Task ABodyCheck_DecidesWhateverTheStatus(HttpStatusCode status, string? error, ServiceResultStatus expected)
    {
        Respond(status, error is null ? """{"name":"found"}""" : $$"""{"error":"{{error}}"}""");

        var result = await _client.GetJsonAsync<Body, string>("https://provider.test/item",
            body => ServiceResult<string>.FromSuccess(body.Name!), "item", CancellationToken.None,
            body => body.Error switch
            {
                "missing" => BodyStatus.NotFound,
                "busy" => BodyStatus.Failed,
                _ => BodyStatus.Ok
            });

        result.Status.Should().Be(expected);
    }
}
