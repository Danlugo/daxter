using Daxter.Core.Artifacts;
using Daxter.Web.Components;
using Daxter.Web.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddSingleton<ConfigState>();
builder.Services.AddScoped<UiBusy>();
builder.Services.AddScoped<DaxterUi>();
builder.Services.AddScoped<ExploreActions>();

// Artifact store — the transport-agnostic file plane every file-shaped feature passes through.
// Singleton because LocalArtifactStore serialises index-file writes via a process-wide
// SemaphoreSlim; a per-request instance would defeat that guarantee. Backs onto
// ~/.daxter/artifacts/ (override via DAXTER_ARTIFACTS_ROOT) with a 5GB default cap (override
// via DAXTER_ARTIFACTS_QUOTA_MB).
builder.Services.AddSingleton<IArtifactStore>(_ => new LocalArtifactStore());
// TTL purge — sweeps expired artifacts every 6 hours by default
// (DAXTER_ARTIFACTS_PURGE_HOURS to override; 0 to disable). Phase 2 addition.
builder.Services.AddHostedService<ArtifactPurgeHostedService>();

// Update check (GitHub releases) — GitHub requires a User-Agent.
builder.Services.AddHttpClient("github", c =>
{
    c.DefaultRequestHeaders.UserAgent.ParseAdd("DAXter-Console");
    c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    c.Timeout = TimeSpan.FromSeconds(10);
});
builder.Services.AddSingleton<VersionService>();
builder.Services.AddSingleton<UsageStore>();
builder.Services.AddSingleton<QueryHistoryStore>();
builder.Services.AddSingleton<JobHistoryStore>();
builder.Services.AddSingleton<Daxter.Core.Scheduling.RefreshQueueStore>();
builder.Services.AddSingleton<JobService>();
// The single shared refresh worker — drains the queue every interface (CLI/MCP/UI) enqueues to.
builder.Services.AddHostedService<RefreshWorkerHostedService>();
builder.Services.AddSingleton<PipelineScanStore>();
builder.Services.AddSingleton<AuditHistoryStore>();
builder.Services.AddSingleton<Daxter.Core.Audit.SavedAuditCheckStore>();

// Capture app logs into an in-memory buffer the Logs page reads (in addition to the console).
var logSink = new LogSink();
builder.Services.AddSingleton(logSink);
builder.Logging.AddProvider(new LogSinkLoggerProvider(logSink));

// v1.40.0 — SAFE-BY-DEFAULT BIND. The Web console holds the signed-in AAD token and can mutate
// Power BI; an open port = an open session. Default to LOOPBACK (127.0.0.1) so a laptop on a
// shared network isn't exposing the console to the subnet. Widen explicitly via DAXTER_WEB_BIND
// (the container deploy sets it to 0.0.0.0 so Docker port-mapping works — but the deploy maps the
// host side to 127.0.0.1, so the host still only exposes localhost). --urls / ASPNETCORE_URLS
// still win if set, for advanced hosting.
if (string.IsNullOrEmpty(builder.Configuration["urls"]) &&
    string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ASPNETCORE_URLS")))
{
    var bind = Environment.GetEnvironmentVariable("DAXTER_WEB_BIND");
    if (string.IsNullOrWhiteSpace(bind)) bind = "127.0.0.1";
    builder.WebHost.UseUrls($"http://{bind}:8080");
}

var app = builder.Build();

// v1.40.0 — startup posture check. If the console is bound beyond loopback AND no Web bearer
// token is set, the /api/* endpoints are reachable without authentication. Warn so a
// misconfiguration is obvious in the logs rather than silent.
{
    var boundUrls = (Environment.GetEnvironmentVariable("ASPNETCORE_URLS")
        ?? builder.Configuration["urls"]
        ?? $"http://{(string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DAXTER_WEB_BIND")) ? "127.0.0.1" : Environment.GetEnvironmentVariable("DAXTER_WEB_BIND"))}:8080");
    var loopbackOnly = boundUrls.Contains("127.0.0.1") || boundUrls.Contains("localhost") || boundUrls.Contains("[::1]");
    var hasWebToken = Daxter.Core.Auth.BearerTokenStore.FromEnv(Daxter.Web.Services.ApiBearerAuthMiddleware.EnvVarName) is not null;
    if (!loopbackOnly && !hasWebToken)
    {
        app.Logger.LogWarning(
            "SECURITY: the Web console is bound beyond localhost ({Bind}) without DAXTER_WEB_BEARER_TOKEN. " +
            "The /api/* endpoints will not require authentication. Set DAXTER_WEB_BEARER_TOKEN to require a " +
            "bearer, or bind to 127.0.0.1.", boundUrls);
    }
}

app.UseStaticFiles();
app.UseAntiforgery();

// v1.40.0 — gate the sensitive HTTP /api/* endpoints behind a bearer token WHEN
// DAXTER_WEB_BEARER_TOKEN is set (no-op otherwise; localhost-bind is the protection in that
// case). Must precede the endpoint maps so an unauth'd /api/sql/export never executes.
app.UseMiddleware<Daxter.Web.Services.ApiBearerAuthMiddleware>();

app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

// SQL streaming export — bypasses the Blazor SignalR circuit so multi-million-row downloads don't
// buffer in the browser. The /sql page POSTs a hidden form here; the response is a streaming CSV
// download (Content-Disposition: attachment). See SqlExportEndpoint.cs for the implementation.
Daxter.Web.Endpoints.SqlExportEndpoint.Map(app);

// Artifact streaming — same Blazor-circuit-bypass story. /artifacts uses these to download a
// single file or zip-stream a whole prefix (PBIR bundle, copy-job definition, etc.). External
// MCP/CLI callers also fetch large artifacts via these URLs when the inline base64 path is too
// big. Phase 1: GET (list + download). Phase 2: POST (upload).
Daxter.Web.Endpoints.ArtifactsEndpoint.Map(app);

// v1.36.0 — /api/health: Semantics-friendly fleet probe. Unauthenticated GET returning
// tenant_id, label, version, uptime, and artifact/context store stats. Designed so a fleet
// orchestration dashboard can populate a per-tenant grid with one curl per container.
Daxter.Web.Endpoints.HealthEndpoint.Map(app);

app.Run();
