using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);

// Define the CORS policy name for use in the application.
var myAppOrigin = "_myAppOrigin";

// Add and configure CORS services.
builder.Services.AddCors(options =>
{
    options.AddPolicy(name: myAppOrigin,
        policy =>
        {
            policy.WithOrigins("*")
                  .AllowAnyHeader()
                  .AllowAnyMethod();
        });
});

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    // Configure Swagger to include an API key authorization field.
    // This allows users to provide the API key directly in the Swagger UI.
    options.AddSecurityDefinition("ApiKey", new OpenApiSecurityScheme
    {
        In = ParameterLocation.Header,
        Name = "X-API-KEY",
        Type = SecuritySchemeType.ApiKey,
        Description = "API Key authentication"
    });

    // Enforce the use of the API key for all endpoints.
    options.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = "ApiKey"
                }
            },
            Array.Empty<string>()
        }
    });
});

var app = builder.Build();

// Configure the HTTP request pipeline.

// Enable middleware to serve generated Swagger as a JSON endpoint.
// It's common to wrap this and the Swagger UI in an environment check
// to only expose them in development environments.
app.UseSwagger();
app.UseSwaggerUI();
app.UseHttpsRedirection();
app.UseCors(myAppOrigin);
app.UseMiddleware<ApiKeyMiddleware>();

// Group API endpoints under the "/api".
var apiGroup = app.MapGroup("/api");

apiGroup.MapGet("/lastfm-key", (IConfiguration config) =>
{
    var lastFmKey = config["LastFm:ApiKey"];
    if (string.IsNullOrEmpty(lastFmKey))
    {
        return Results.Problem("Last.fm API Key not configured on the server.", statusCode: 503);
    }
    return Results.Ok(lastFmKey);
});

apiGroup.MapGet("/spotify-key", (IConfiguration config) =>
{
    var spotifyKey = config["Spotify:ApiKey"];
    if (string.IsNullOrEmpty(spotifyKey))
    {
        return Results.Problem("Spotify API Key not configured on the server.", statusCode: 503);
    }
    return Results.Ok(spotifyKey);
});

app.Run();

/// <summary>
/// Middleware to validate the presence and correctness of an API key for API-specific routes.
/// </summary>
public class ApiKeyMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ApiKeyMiddleware> _logger;
    private readonly string? _serverApiKey;
    
    private const string ApiKeyHeaderName = "X-API-KEY";
    private const string ApiKeyConfigPath = "ServerAuth:ApiKey";
    private const string ApiPathPrefix = "/api";

    public ApiKeyMiddleware(RequestDelegate next, IConfiguration config, ILogger<ApiKeyMiddleware> logger)
    {
        _next = next;
        _logger = logger;
        _serverApiKey = config[ApiKeyConfigPath];
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // A missing server-side API key is a fatal configuration error.
        if (string.IsNullOrEmpty(_serverApiKey))
        {
            _logger.LogCritical("CRITICAL ERROR: Server is misconfigured. The '{ApiKeyConfigPath}' is missing from configuration.", ApiKeyConfigPath);
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await context.Response.WriteAsync("Error: The service is not configured correctly.");
            return;
        }

        // The middleware should only apply to routes under the API path.
        if (!context.Request.Path.StartsWithSegments(ApiPathPrefix))
        {
            await _next(context);
            return;
        }
        
        if (!context.Request.Headers.TryGetValue(ApiKeyHeaderName, out var extractedApiKey) || !_serverApiKey.Equals(extractedApiKey))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsync("Unauthorized: A valid API key is required.");
            return;
        }

        await _next(context);
    }
}
