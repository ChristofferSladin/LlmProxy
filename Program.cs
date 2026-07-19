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
ProxyOptions proxyOptions;
{
    proxyOptions = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<ProxyOptions>>().Value;
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

// T0c seam: startup-validation call site. No-op today (see StartupValidation's remarks) — T6
// fills in the real cross-field consistency rules without touching Program.cs again.
StartupValidation.Validate(proxyOptions, app.Environment);

// --- Inbound auth (T0c: happy path only) ---
// Scoped to /v1/* by construction, not applied globally: the conditional path check below means
// "/" and "/health" never reach InboundAuth.TryResolve and stay reachable with no header
// regardless of InboundKeys configuration. T0c only needs to distinguish "let the request
// through" (open — no keys configured — or a resolved caller) from everything else, which gets a
// minimal generic 401 here; T1 owns the real rejection envelope (400 for out-of-grant alias,
// rotation, no-key-material-in-logs, etc.) and replaces the body of the `if` below without
// restructuring this middleware.
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/v1"))
    {
        var options = context.RequestServices.GetRequiredService<Microsoft.Extensions.Options.IOptions<ProxyOptions>>().Value;
        var authHeader = context.Request.Headers["Authorization"].ToString();
        if (!InboundAuth.TryResolve(string.IsNullOrEmpty(authHeader) ? null : authHeader, options, out var caller))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(
                "{\"error\":{\"message\":\"Unauthorized\",\"type\":\"authentication_error\",\"code\":401}}");
            return;
        }

        // Only set when a key was actually resolved (keys configured + matched); the forwarding
        // path (and T1's future alias-grant check) can look for this item's presence to know
        // whether the caller is restricted at all.
        if (caller is not null) context.Items[InboundAuth.CallerItemKey] = caller;
    }

    await next(context);
});

// --- Rate limiting (T2) ---
// Registered as a second middleware, strictly AFTER the auth middleware above: it reads
// context.Items[InboundAuth.CallerItemKey], which is only populated once auth has resolved the
// caller (rate limiting partitions by App, which doesn't exist until then — see the PRD's Risks
// section on pipeline ordering). No caller in Items => either "/v1" wasn't matched or no keys are
// configured (open path) => skip rate limiting entirely, preserving today's unconfigured-means-
// unlimited local behavior. See RateLimitCounter's remarks for why this is a hand-rolled counter
// rather than Microsoft.AspNetCore.RateLimiting's AddRateLimiter.
var rateLimiter = new RateLimitCounter(TimeSpan.FromSeconds(proxyOptions.RateLimitWindowSeconds));
app.Use(async (context, next) =>
{
    if (context.Items.TryGetValue(InboundAuth.CallerItemKey, out var callerObj) && callerObj is ResolvedCaller caller)
    {
        var result = rateLimiter.TryAcquire(RateLimitPartition.KeyFor(caller), caller.RequestsPerMinute);
        if (!result.Allowed)
        {
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            context.Response.Headers["Retry-After"] = result.RetryAfterSeconds.ToString();
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(
                "{\"error\":{\"message\":\"Rate limit exceeded\",\"type\":\"rate_limit_error\",\"code\":429}}");
            return;
        }
    }

    await next(context);
});

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
