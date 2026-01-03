using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Azure.Functions.Worker;
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
        // Get the native ASP.NET Core HttpContext
        var httpContext = context.GetHttpContext();
        if (httpContext == null)
        {
            await next(context);
            return;
        }

        var request = httpContext.Request;
        var requestPath = request.Path.Value ?? string.Empty;

        // Bypass authentication for public OpenAPI/Swagger documentation endpoints.
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
            await CreateErrorResponse(httpContext, HttpStatusCode.ServiceUnavailable,
                "Error: The service is not configured correctly.");
            return;
        }

        // Validate the API key provided in the request header.
        if (!request.Headers.TryGetValue(ApiKeyHeaderName, out var values) ||
            !_serverApiKey.Equals(values.FirstOrDefault()))
        {
            _logger.LogWarning("Unauthorized access attempt to '{Path}'. Missing or invalid API Key.", requestPath);
            await CreateErrorResponse(httpContext, HttpStatusCode.Unauthorized,
                "Unauthorized: A valid API key is required.");
            return;
        }

        // If the key is valid, proceed to the next function in the pipeline.
        await next(context);
    }

    /// <summary>
    ///     Creates a standardized error response using the HttpContext.
    /// </summary>
    private static async Task CreateErrorResponse(HttpContext context, HttpStatusCode statusCode, string message)
    {
        context.Response.StatusCode = (int)statusCode;
        context.Response.ContentType = "text/plain; charset=utf-8";
        await context.Response.WriteAsync(message);
    }
}