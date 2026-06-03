using Daxter.Web.Components;
using Daxter.Web.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddSingleton<ConfigState>();
builder.Services.AddScoped<DaxterUi>();
builder.Services.AddScoped<ExploreActions>();

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

app.Run();
