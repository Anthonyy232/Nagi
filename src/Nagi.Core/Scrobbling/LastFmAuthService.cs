using System.Text.Json;
using Microsoft.Extensions.Logging;
using Nagi.Core.OnlineProviders;
using Nagi.Core.OnlineProviders.Pipelines;

namespace Nagi.Core.Scrobbling;

public class LastFmAuthService : ILastFmAuthService
{
    private readonly LastFmSignedClient _client;
    private readonly ILogger<LastFmAuthService> _logger;

    public LastFmAuthService(
        IHttpClientFactory httpClientFactory,
        ProviderPipelineProvider pipelines,
        IApiKeyService apiKeyService,
        ILogger<LastFmAuthService> logger)
    {
        _client = new LastFmSignedClient(httpClientFactory.CreateClient(), pipelines, apiKeyService, logger);
        _logger = logger;
    }

    public Task<(string Token, string AuthUrl)?> GetAuthenticationTokenAsync() =>
        _client.SendAsync<(string Token, string AuthUrl)?>(HttpMethod.Get,
            new Dictionary<string, string> { ["method"] = "auth.getToken", ["format"] = "json" },
            reply =>
            {
                if (Read<LastFmTokenResponse>(reply, "auth token")?.Token is not { Length: > 0 } token) return null;
                var authUrl = $"https://www.last.fm/api/auth/?api_key={Uri.EscapeDataString(reply.ApiKey)}&token={Uri.EscapeDataString(token)}";
                return (token, authUrl);
            },
            fallback: null);

    public Task<(string Username, string SessionKey)?> GetSessionAsync(string token) =>
        _client.SendAsync<(string Username, string SessionKey)?>(HttpMethod.Get,
            new Dictionary<string, string> { ["method"] = "auth.getSession", ["token"] = token, ["format"] = "json" },
            reply =>
            {
                if (Read<LastFmSessionResponse>(reply, "session")?.Session is not
                    { Name: { Length: > 0 } name, Key: { Length: > 0 } key }) return null;
                _logger.LogInformation("Successfully retrieved Last.fm session for user {Username}.", name);
                return (name, key);
            },
            fallback: null);

    private T? Read<T>(LastFmReply reply, string subject) where T : class
    {
        if (reply.Response.IsSuccessStatusCode && JsonSerializer.Deserialize<T>(reply.Body, JsonSerializerOptions.Web) is { } body)
            return body;

        _logger.LogError("Failed to get the Last.fm {Subject}. Status: {StatusCode}, Response: {ResponseContent}",
            subject, reply.Response.StatusCode, reply.Body);
        return null;
    }

    private sealed class LastFmTokenResponse
    {
        public string? Token { get; set; }
    }

    private sealed class LastFmSessionResponse
    {
        public LastFmSession? Session { get; set; }
    }

    private sealed class LastFmSession
    {
        public string? Name { get; set; }
        public string? Key { get; set; }
    }
}
