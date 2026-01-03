using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
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
    [OpenApiOperation("GetLastFmKey", "Keys", Summary = "Gets the Last.fm API Key")]
    [OpenApiSecurity("ApiKey", SecuritySchemeType.ApiKey, Name = "X-API-KEY", In = OpenApiSecurityLocationType.Header)]
    [OpenApiResponseWithBody(HttpStatusCode.OK, "application/json", typeof(KeyResponse),
        Description = "A JSON object containing the Last.fm API Key.")]
    [OpenApiResponseWithBody(HttpStatusCode.ServiceUnavailable, "text/plain", typeof(string),
        Description = "The requested key is not configured on the server.")]
    public IActionResult GetLastFmKey(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "lastfm-key")]
        HttpRequest req)
    {
        return CreateKeyResponse(req, "LastFm:ApiKey", "Last.fm API Key");
    }

    [Function("GetLastFmSecretKey")]
    [OpenApiOperation("GetLastFmSecretKey", "Keys", Summary = "Gets the Last.fm Shared Secret")]
    [OpenApiSecurity("ApiKey", SecuritySchemeType.ApiKey, Name = "X-API-KEY", In = OpenApiSecurityLocationType.Header)]
    [OpenApiResponseWithBody(HttpStatusCode.OK, "application/json", typeof(KeyResponse),
        Description = "A JSON object containing the Last.fm Shared Secret.")]
    [OpenApiResponseWithBody(HttpStatusCode.ServiceUnavailable, "text/plain", typeof(string),
        Description = "The requested key is not configured on the server.")]
    public IActionResult GetLastFmSecretKey(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "lastfm-secret-key")]
        HttpRequest req)
    {
        return CreateKeyResponse(req, "LastFm:SharedSecret", "Last.fm Shared Secret");
    }

    [Function("GetSpotifyKey")]
    [OpenApiOperation("GetSpotifyKey", "Keys",
        Summary = "Gets the Spotify Client ID and Secret, concatenated.")]
    [OpenApiSecurity("ApiKey", SecuritySchemeType.ApiKey, Name = "X-API-KEY", In = OpenApiSecurityLocationType.Header)]
    [OpenApiResponseWithBody(HttpStatusCode.OK, "application/json", typeof(KeyResponse),
        Description = "A JSON object containing the Spotify credentials in the format 'ClientId:ClientSecret'.")]
    [OpenApiResponseWithBody(HttpStatusCode.ServiceUnavailable, "text/plain", typeof(string),
        Description = "The requested key is not configured on the server.")]
    public IActionResult GetSpotifyKey(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "spotify-key")]
        HttpRequest req)
    {
        // The client expects a single string "ClientId:ClientSecret".
        var clientId = _config["Spotify:ClientId"];
        var clientSecret = _config["Spotify:ClientSecret"];
        const string keyName = "Spotify ClientId/Secret";

        if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(clientSecret))
        {
            _logger.LogError("{KeyName} is not configured correctly on the server.", keyName);
            return new ObjectResult($"{keyName} is not configured on the server.")
            {
                StatusCode = (int)HttpStatusCode.ServiceUnavailable
            };
        }

        return new OkObjectResult(new KeyResponse { Value = $"{clientId}:{clientSecret}" });
    }

    [Function("GetFanartTvKey")]
    [OpenApiOperation("GetFanartTvKey", "Keys", Summary = "Gets the Fanart.tv API Key")]
    [OpenApiSecurity("ApiKey", SecuritySchemeType.ApiKey, Name = "X-API-KEY", In = OpenApiSecurityLocationType.Header)]
    [OpenApiResponseWithBody(HttpStatusCode.OK, "application/json", typeof(KeyResponse),
        Description = "A JSON object containing the Fanart.tv API Key.")]
    [OpenApiResponseWithBody(HttpStatusCode.ServiceUnavailable, "text/plain", typeof(string),
        Description = "The requested key is not configured on the server.")]
    public IActionResult GetFanartTvKey(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "fanarttv-key")]
        HttpRequest req)
    {
        return CreateKeyResponse(req, "FanartTv:ApiKey", "Fanart.tv API Key");
    }

    [Function("GetTheAudioDbKey")]
    [OpenApiOperation("GetTheAudioDbKey", "Keys", Summary = "Gets TheAudioDB API Key")]
    [OpenApiSecurity("ApiKey", SecuritySchemeType.ApiKey, Name = "X-API-KEY", In = OpenApiSecurityLocationType.Header)]
    [OpenApiResponseWithBody(HttpStatusCode.OK, "application/json", typeof(KeyResponse),
        Description = "A JSON object containing TheAudioDB API Key.")]
    [OpenApiResponseWithBody(HttpStatusCode.ServiceUnavailable, "text/plain", typeof(string),
        Description = "The requested key is not configured on the server.")]
    public IActionResult GetTheAudioDbKey(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "theaudiodb-key")]
        HttpRequest req)
    {
        return CreateKeyResponse(req, "TheAudioDb:ApiKey", "TheAudioDB API Key");
    }

    /// <summary>
    ///     A generic helper to retrieve a configuration value and create an appropriate HTTP response.
    /// </summary>
    private IActionResult CreateKeyResponse(HttpRequest req, string configPath, string keyName)
    {
        var keyValue = _config[configPath];

        if (string.IsNullOrEmpty(keyValue))
        {
            _logger.LogError("{KeyName} is not configured on the server. Path: {ConfigPath}", keyName, configPath);
            return new ObjectResult($"{keyName} is not configured on the server.")
            {
                StatusCode = (int)HttpStatusCode.ServiceUnavailable
            };
        }

        return new OkObjectResult(new KeyResponse { Value = keyValue });
    }
}