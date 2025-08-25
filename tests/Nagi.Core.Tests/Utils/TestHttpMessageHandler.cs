using System.Net;

namespace Nagi.Core.Tests.Utils;

/// <summary>
///     Provides a test-specific HttpMessageHandler that intercepts outgoing HTTP requests,
///     records them, and returns a configurable mock response.
/// </summary>
public class TestHttpMessageHandler : HttpMessageHandler
{
    public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? SendAsyncFunc { get; set; }
    public List<HttpRequestMessage> Requests { get; } = new();

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return SendAsyncFunc != null
            ? SendAsyncFunc(request, cancellationToken)
            : Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }
}