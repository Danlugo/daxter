using System.Text.Json;

namespace Daxter.Web.Services;

public enum UpdateState { Dev, UpToDate, UpdateAvailable, Unknown }

/// <summary>Result of an update check.</summary>
public sealed record UpdateInfo(
    string Current, string? Latest, string? Url, UpdateState State, string? Detail);

/// <summary>
/// Reports the running build's version (stamped into the image at build time via
/// <c>DAXTER_VERSION</c>) and, on demand, checks GitHub for a newer release. The check is
/// outbound-only and happens just when the user clicks — no telemetry, no background polling.
/// </summary>
public sealed class VersionService
{
    private readonly IHttpClientFactory _httpFactory;

    /// <summary>GitHub owner/repo, overridable for forks via <c>DAXTER_REPO</c>.</summary>
    public string Repo { get; }

    /// <summary>The running version, e.g. "v1.6.3", or "dev" for a local build.</summary>
    public string Current { get; }

    public VersionService(IHttpClientFactory httpFactory)
    {
        _httpFactory = httpFactory;
        Current = Trim(Environment.GetEnvironmentVariable("DAXTER_VERSION")) ?? "dev";
        Repo = Trim(Environment.GetEnvironmentVariable("DAXTER_REPO")) ?? "Danlugo/daxter";
    }

    public string RepoUrl => $"https://github.com/{Repo}";

    public async Task<UpdateInfo> CheckLatestAsync(CancellationToken ct = default)
    {
        try
        {
            var http = _httpFactory.CreateClient("github");
            using var resp = await http.GetAsync(
                $"https://api.github.com/repos/{Repo}/releases/latest", ct);

            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return new(Current, null, $"{RepoUrl}/releases", UpdateState.Unknown,
                    "No published releases found yet.");
            }

            resp.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;
            var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
            var url = root.TryGetProperty("html_url", out var u) ? u.GetString() : $"{RepoUrl}/releases";

            return new(Current, tag, url, Compare(Current, tag), null);
        }
        catch (Exception ex)
        {
            return new(Current, null, $"{RepoUrl}/releases", UpdateState.Unknown,
                $"Couldn't reach GitHub: {ex.Message}");
        }
    }

    private static UpdateState Compare(string current, string? latest)
    {
        if (string.IsNullOrWhiteSpace(current) || current.Equals("dev", StringComparison.OrdinalIgnoreCase))
            return UpdateState.Dev;

        if (TryParse(current, out var cur) && TryParse(latest, out var lat))
            return lat > cur ? UpdateState.UpdateAvailable : UpdateState.UpToDate;

        return UpdateState.Unknown;
    }

    private static bool TryParse(string? value, out Version version)
    {
        version = new Version(0, 0);
        if (string.IsNullOrWhiteSpace(value)) return false;
        var v = value.Trim().TrimStart('v', 'V');
        return Version.TryParse(v, out version!);
    }

    private static string? Trim(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
