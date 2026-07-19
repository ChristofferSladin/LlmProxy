using LlmProxy;

var builder = WebApplication.CreateBuilder(args);

// Local, git-ignored secrets file. Loaded in any environment; overrides appsettings.json.
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);

builder.Services.Configure<ProxyOptions>(builder.Configuration.GetSection("Proxy"));
builder.Services.AddHttpClient("upstream", c =>
{
    // Long timeout so streamed completions aren't cut off mid-generation.
    c.Timeout = TimeSpan.FromMinutes(10);
});
builder.Services.AddSingleton<ProviderRegistry>();
builder.Services.AddSingleton<ModelCatalog>();
builder.Services.AddSingleton<RoutingState>();
builder.Services.AddSingleton<ProxyService>();

var app = builder.Build();

// Resolve SystemPromptFile -> SystemPrompt once at startup; fail fast if a configured file is missing.
{
    var proxyOptions = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<ProxyOptions>>().Value;
    foreach (var (name, provider) in proxyOptions.Providers)
    {
        if (string.IsNullOrWhiteSpace(provider.SystemPromptFile)) continue;
        var path = Path.IsPathRooted(provider.SystemPromptFile)
            ? provider.SystemPromptFile
            : Path.Combine(builder.Environment.ContentRootPath, provider.SystemPromptFile);
        if (!File.Exists(path))
            throw new FileNotFoundException($"Provider '{name}': SystemPromptFile not found at '{path}'.");
        provider.SystemPrompt = File.ReadAllText(path).Trim();
    }
}

// --- Health / root ---
app.MapGet("/", () => Results.Ok(new { status = "ok", service = "llm-proxy" }));
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// --- Models (cached, merged across providers) ---
app.MapGet("/v1/models", async (ModelCatalog catalog, HttpContext http, CancellationToken ct) =>
{
    var models = await catalog.GetModelsAsync(ct);
    http.Response.ContentType = "application/json";
    await http.Response.WriteAsync(models.ToJsonString(), ct);
});

// Convenience: force a refresh of the cached model list.
app.MapPost("/v1/models/refresh", (ModelCatalog catalog) =>
{
    catalog.Invalidate();
    return Results.Ok(new { refreshed = true });
});

// --- OpenAI-compatible POST endpoints (passthrough with auth + model routing) ---
app.MapPost("/v1/chat/completions", (HttpContext http, ProxyService proxy, CancellationToken ct) =>
    proxy.ForwardJsonAsync(http, "chat/completions", ct));

// Responses API (what Codex uses). Passthrough — works for upstreams that implement it.
app.MapPost("/v1/responses", (HttpContext http, ProxyService proxy, CancellationToken ct) =>
    proxy.ForwardJsonAsync(http, "responses", ct));

app.MapPost("/v1/completions", (HttpContext http, ProxyService proxy, CancellationToken ct) =>
    proxy.ForwardJsonAsync(http, "completions", ct));

app.MapPost("/v1/embeddings", (HttpContext http, ProxyService proxy, CancellationToken ct) =>
    proxy.ForwardJsonAsync(http, "embeddings", ct));

app.Run();

// Marker so `WebApplicationFactory<Program>` (LlmProxy.Tests/IntegrationHost.cs) can boot this
// minimal-API app in-process for integration tests. Top-level statements above are unaffected.
public partial class Program { }
