using System.Net;

namespace Nagi.Core.Http.Pipelines;

/// <summary>
///     Shared HTTP status classification used by both the provider pipeline (for retry
///     decisions) and post-pipeline callers (e.g., scrobbler queues that re-queue on
///     transient failures). Kept in one place so the two never silently diverge.
/// </summary>
public static class HttpStatusClassification
{
    /// <summary>
    ///     5xx and 429 are transient; 408 is "the server gave up waiting" and is retryable.
    /// </summary>
    public static bool IsTransient(HttpStatusCode statusCode)
        => statusCode == HttpStatusCode.RequestTimeout         // 408
           || statusCode == HttpStatusCode.TooManyRequests      // 429
           || (int)statusCode >= 500;
}
