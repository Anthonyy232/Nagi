using System.Net;
using System.Text;

namespace Nagi.Core.Tests.Utils;

/// <summary>
///     Provides a test-specific HttpMessageHandler that intercepts outgoing HTTP requests,
///     records them, and returns a configurable mock response.
/// </summary>
public class TestHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<(HttpStatusCode Status, string Body)> _sequence = new();
    public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? SendAsyncFunc { get; set; }
    public List<HttpRequestMessage> Requests { get; } = new();

    /// <summary>
    ///     Queues a sequence of canned responses. SendAsync returns them in FIFO order, once each.
    ///     After the sequence is exhausted, requests fall through to <see cref="SendAsyncFunc" /> or
    ///     default 200 OK.
    /// </summary>
    public void RespondSequence(params (HttpStatusCode Status, string Body)[] responses)
    {
        foreach (var r in responses) _sequence.Enqueue(r);
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Requests.Add(request);

        if (_sequence.Count > 0)
        {
            var (status, body) = _sequence.Dequeue();
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }

        return SendAsyncFunc != null
            ? SendAsyncFunc(request, cancellationToken)
            : Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }
}
