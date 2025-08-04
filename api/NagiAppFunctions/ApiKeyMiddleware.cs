using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Azure.Functions.Worker.Middleware;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace NagiAppFunctions;

/// <summary>
///     Middleware to enforce API key authentication for HTTP-triggered functions.
/// </summary>
public class ApiKeyMiddleware : IFunctionsWorkerMiddleware
{
    private const string ApiKeyHeaderName = "X-API-KEY";
    private const string ApiKeyConfigPath = "ServerAuth:ApiKey";
    private readonly ILogger<ApiKeyMiddleware> _logger;
    private readonly string? _serverApiKey;

    public ApiKeyMiddleware(IConfiguration config, ILogger<ApiKeyMiddleware> logger)
    {
        _logger = logger;
        _serverApiKey = config[ApiKeyConfigPath];
    }

    /// <summary>
    ///     Intercepts the function invocation to perform an API key check.
    /// </summary>
    public async Task Invoke(FunctionContext context, FunctionExecutionDelegate next)
    {
        // Attempt to get HTTP request data. If it's not an HTTP trigger, do nothing.
        var requestData = await context.GetHttpRequestDataAsync();
        if (requestData == null)
        {
            await next(context);
            return;
        }

        // Bypass authentication for public OpenAPI/Swagger documentation endpoints.
        var requestPath = requestData.Url.AbsolutePath;
        if (requestPath.StartsWith("/api/swagger") || requestPath.StartsWith("/api/openapi"))
        {
            await next(context);
            return;
        }

        // Critical configuration check: The server must have an API key defined.
        if (string.IsNullOrEmpty(_serverApiKey))
        {
            _logger.LogCritical("Server is misconfigured. The API key at '{ApiKeyConfigPath}' is missing.",
                ApiKeyConfigPath);
            await CreateErrorResponse(context, requestData, HttpStatusCode.ServiceUnavailable,
                "Error: The service is not configured correctly.");
            return;
        }

        // Validate the API key provided in the request header.
        if (!requestData.Headers.TryGetValues(ApiKeyHeaderName, out var values) ||
            !_serverApiKey.Equals(values.FirstOrDefault()))
        {
            _logger.LogWarning("Unauthorized access attempt to '{Path}'. Missing or invalid API Key.", requestPath);
            await CreateErrorResponse(context, requestData, HttpStatusCode.Unauthorized,
                "Unauthorized: A valid API key is required.");
            return;
        }

        // If the key is valid, proceed to the next function in the pipeline.
        await next(context);
    }

    /// <summary>
    ///     Creates and sets a standardized error response on the function context.
    /// </summary>
    private static async Task CreateErrorResponse(FunctionContext context, HttpRequestData request,
        HttpStatusCode statusCode, string message)
    {
        var response = request.CreateResponse(statusCode);
        response.Headers.Add("Content-Type", "text/plain; charset=utf-8");
        await response.WriteStringAsync(message);

        // Set the response on the invocation result to terminate the request and return the error.
        context.GetInvocationResult().Value = response;
    }
}