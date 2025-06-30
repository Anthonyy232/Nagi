var builder = WebApplication.CreateBuilder(args);

// --- Add CORS Policy ---
var myAppOrigin = "_myAppOrigin";
builder.Services.AddCors(options =>
{
    options.AddPolicy(myAppOrigin,
        policy => { policy.WithOrigins("*").AllowAnyHeader().AllowAnyMethod(); });
});

// Add services to the container.
// This is crucial for enabling endpoint discovery for Swagger.
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(); // <--- ADD THIS LINE

var app = builder.Build();

// --- Custom API Key Middleware (Now with a built-in fatal error check) ---
var serverApiKey = app.Configuration["ServerAuth:ApiKey"];

app.Use(async (context, next) =>
{
    if (string.IsNullOrEmpty(serverApiKey))
    {
        context.Response.StatusCode = 503;
        await context.Response.WriteAsync(
            "CRITICAL ERROR: Server is misconfigured. The ServerAuth:ApiKey is missing from configuration. Check Key Vault references.");
        return;
    }

    if (!context.Request.Path.StartsWithSegments("/api"))
    {
        await next();
        return;
    }

    if (!context.Request.Headers.TryGetValue("X-API-KEY", out var extractedApiKey) ||
        !serverApiKey.Equals(extractedApiKey))
    {
        context.Response.StatusCode = 401;
        await context.Response.WriteAsync("Unauthorized client.");
        return;
    }

    await next();
});

// Configure the HTTP request pipeline.
// UseSwagger and UseSwaggerUI should typically be called before UseHttpsRedirection or UseAuthorization
// and usually within app.Environment.IsDevelopment() if you want to restrict them to dev environments.
app.UseSwagger(); // <--- ADD THIS LINE
app.UseSwaggerUI(); // <--- ADD THIS LINE (This enables the Swagger UI at /swagger)

// --- API Endpoint Definition ---
app.MapGet("/api/lastfm-key", (IConfiguration config) =>
{
    var lastFmKey = config["LastFm:ApiKey"];
    if (string.IsNullOrEmpty(lastFmKey))
        return Results.Problem("Last.fm API Key not configured on the server.", statusCode: 500);
    return Results.Ok(lastFmKey);
});

app.MapGet("/api/spotify-key", (IConfiguration config) =>
{
    var spotifyKey = config["Spotify:ApiKey"];
    if (string.IsNullOrEmpty(spotifyKey))
        return Results.Problem("Spotify API Key not configured on the server.", statusCode: 500);
    return Results.Ok(spotifyKey);
});

// --- Standard Middleware ---
app.UseHttpsRedirection();
app.UseCors(myAppOrigin);
app.Run();