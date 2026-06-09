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

// Default to all interfaces on 8080 inside the container (override with --urls / ASPNETCORE_URLS).
if (string.IsNullOrEmpty(builder.Configuration["urls"]) &&
    string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ASPNETCORE_URLS")))
{
    builder.WebHost.UseUrls("http://0.0.0.0:8080");
}

var app = builder.Build();

app.UseStaticFiles();
app.UseAntiforgery();
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

app.Run();
