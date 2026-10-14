using System.Net;

namespace Nagi.Core.Http.Pipelines;

/// <summary>
///     HTTP status codes eligible for automatic pipeline retries.
/// </summary>
public static class HttpStatusClassification
{
    public static bool IsTransient(HttpStatusCode statusCode)
        => statusCode == HttpStatusCode.RequestTimeout
           || statusCode == HttpStatusCode.TooManyRequests
           || (int)statusCode >= 500;
}
