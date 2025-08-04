using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Azure.WebJobs.Extensions.OpenApi.Core.Attributes;
using Microsoft.Azure.WebJobs.Extensions.OpenApi.Core.Enums;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.OpenApi.Models;

namespace NagiAppFunctions;

/// <summary>
///     A DTO for the JSON response containing a configuration value.
/// </summary>
file class KeyResponse
{
    public string Value { get; set; } = string.Empty;
}

/// <summary>
///     Provides secure endpoints for client applications to retrieve necessary third-party API keys.
/// </summary>
public class KeyProviderFunctions
{
    private readonly IConfiguration _config;
    private readonly ILogger<KeyProviderFunctions> _logger;

    public KeyProviderFunctions(IConfiguration config, ILogger<KeyProviderFunctions> logger)
    {
        _config = config;
        _logger = logger;
    }

    [Function("GetLastFmKey")]
    [OpenApiOperation("GetLastFmKey", new[] { "Keys" }, Summary = "Gets the Last.fm API Key")]
    [OpenApiSecurity("ApiKey", SecuritySchemeType.ApiKey, Name = "X-API-KEY", In = OpenApiSecurityLocationType.Header)]
    [OpenApiResponseWithBody(HttpStatusCode.OK, "application/json", typeof(KeyResponse),
        Description = "A JSON object containing the Last.fm API Key.")]
    [OpenApiResponseWithBody(HttpStatusCode.ServiceUnavailable, "text/plain", typeof(string),
        Description = "The requested key is not configured on the server.")]
    public async Task<HttpResponseData> GetLastFmKey(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "lastfm-key")]
        HttpRequestData req)
    {
        return await CreateKeyResponseAsync(req, "LastFm:ApiKey", "Last.fm API Key");
    }

    [Function("GetLastFmSecretKey")]
    [OpenApiOperation("GetLastFmSecretKey", new[] { "Keys" }, Summary = "Gets the Last.fm Shared Secret")]
    [OpenApiSecurity("ApiKey", SecuritySchemeType.ApiKey, Name = "X-API-KEY", In = OpenApiSecurityLocationType.Header)]
    [OpenApiResponseWithBody(HttpStatusCode.OK, "application/json", typeof(KeyResponse),
        Description = "A JSON object containing the Last.fm Shared Secret.")]
    [OpenApiResponseWithBody(HttpStatusCode.ServiceUnavailable, "text/plain", typeof(string),
        Description = "The requested key is not configured on the server.")]
    public async Task<HttpResponseData> GetLastFmSecretKey(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "lastfm-secret-key")]
        HttpRequestData req)
    {
        return await CreateKeyResponseAsync(req, "LastFm:SharedSecret", "Last.fm Shared Secret");
    }

    [Function("GetSpotifyKey")]
    [OpenApiOperation("GetSpotifyKey", new[] { "Keys" },
        Summary = "Gets the Spotify Client ID and Secret, concatenated.")]
    [OpenApiSecurity("ApiKey", SecuritySchemeType.ApiKey, Name = "X-API-KEY", In = OpenApiSecurityLocationType.Header)]
    [OpenApiResponseWithBody(HttpStatusCode.OK, "application/json", typeof(KeyResponse),
        Description = "A JSON object containing the Spotify credentials in the format 'ClientId:ClientSecret'.")]
    [OpenApiResponseWithBody(HttpStatusCode.ServiceUnavailable, "text/plain", typeof(string),
        Description = "The requested key is not configured on the server.")]
    public async Task<HttpResponseData> GetSpotifyKey(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "spotify-key")]
        HttpRequestData req)
    {
        // The client expects a single string "ClientId:ClientSecret".
        var clientId = _config["Spotify:ClientId"];
        var clientSecret = _config["Spotify:ClientSecret"];
        const string keyName = "Spotify ClientId/Secret";

        if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(clientSecret))
        {
            _logger.LogError("{KeyName} is not configured correctly on the server.", keyName);
            var errorResponse = req.CreateResponse(HttpStatusCode.ServiceUnavailable);
            await errorResponse.WriteStringAsync($"{keyName} is not configured on the server.");
            return errorResponse;
        }

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new KeyResponse { Value = $"{clientId}:{clientSecret}" });
        return response;
    }

    /// <summary>
    ///     A generic helper to retrieve a configuration value and create an appropriate HTTP response.
    /// </summary>
    /// <param name="req">The incoming HTTP request data.</param>
    /// <param name="configPath">The path to the key in the application configuration (e.g., "LastFm:ApiKey").</param>
    /// <param name="keyName">A user-friendly name for the key, used in error messages.</param>
    /// <returns>An HttpResponseData object containing the key or an error.</returns>
    private async Task<HttpResponseData> CreateKeyResponseAsync(HttpRequestData req, string configPath, string keyName)
    {
        var keyValue = _config[configPath];

        if (string.IsNullOrEmpty(keyValue))
        {
            _logger.LogError("{KeyName} is not configured on the server. Path: {ConfigPath}", keyName, configPath);
            var errorResponse = req.CreateResponse(HttpStatusCode.ServiceUnavailable);
            await errorResponse.WriteStringAsync($"{keyName} is not configured on the server.");
            return errorResponse;
        }

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new KeyResponse { Value = keyValue });
        return response;
    }
}