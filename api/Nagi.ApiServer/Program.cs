var builder = WebApplication.CreateBuilder(args);

// --- Add CORS Policy ---
var myAppOrigin = "_myAppOrigin";
builder.Services.AddCors(options => {
    options.AddPolicy(name: myAppOrigin,
        policy => {
            policy.WithOrigins("*").AllowAnyHeader().AllowAnyMethod();
        });
});

var app = builder.Build();

// --- Custom API Key Middleware (Now with a built-in fatal error check) ---
var serverApiKey = app.Configuration["ServerAuth:ApiKey"];

app.Use(async (context, next) => {
    // FATAL CHECK FIRST: Is the server even configured correctly?
    // This check runs on every request.
    if (string.IsNullOrEmpty(serverApiKey)) {
        // Use 503 Service Unavailable, as the service is not configured to run.
        context.Response.StatusCode = 503;
        await context.Response.WriteAsync("CRITICAL ERROR: Server is misconfigured. The ServerAuth:ApiKey is missing from configuration. Check Key Vault references.");
        return; // Stop processing this request.
    }

    // If the server is configured, proceed with the normal endpoint protection.
    if (!context.Request.Path.StartsWithSegments("/api/key")) {
        await next();
        return;
    }

    if (!context.Request.Headers.TryGetValue("X-API-KEY", out var extractedApiKey) || !serverApiKey.Equals(extractedApiKey)) {
        context.Response.StatusCode = 401;
        await context.Response.WriteAsync("Unauthorized client.");
        return;
    }

    // If all checks pass, proceed to the endpoint.
    await next();
});

// --- API Endpoint Definition ---
app.MapGet("/api/key", (IConfiguration config) => {
    var lastFmKey = config["LastFm:ApiKey"];
    if (string.IsNullOrEmpty(lastFmKey)) {
        return Results.Problem("Last.fm API Key not configured on the server.", statusCode: 500);
    }
    return Results.Ok(lastFmKey);
});

// --- Standard Middleware ---
app.UseHttpsRedirection();
app.UseCors(myAppOrigin);
app.Run(); // The one and only app.Run() call