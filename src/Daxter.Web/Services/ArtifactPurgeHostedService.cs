using Daxter.Core.Artifacts;

namespace Daxter.Web.Services;

/// <summary>Background tick that sweeps <see cref="IArtifactStore.PurgeExpiredAsync"/> on a
/// schedule. Without this, TTL-tagged artifacts would only purge on user-initiated clicks of the
/// /artifacts page's "Purge expired" button — fine for a single-user laptop but not for any
/// long-running deployment where the bytes silently pile up.
///
/// CADENCE. Default 6 hours — short enough that disk creeps clear without manual prompting,
/// long enough that an MCP tool race ("put + delete + immediately put again with same key")
/// doesn't fight a purger. Override via <c>DAXTER_ARTIFACTS_PURGE_HOURS</c> (set to 0 to
/// disable — useful in tests).
///
/// SAFETY. The purger ONLY removes artifacts whose <c>ExpiresAt</c> is in the past. Artifacts
/// without a TTL are immortal — the user (or an explicit Delete) is the only way they leave.
/// </summary>
public sealed class ArtifactPurgeHostedService : BackgroundService
{
    private readonly IArtifactStore _store;
    private readonly ILogger<ArtifactPurgeHostedService> _log;
    private readonly TimeSpan _interval;

    public ArtifactPurgeHostedService(IArtifactStore store, ILogger<ArtifactPurgeHostedService> log)
    {
        _store = store;
        _log = log;
        var hours = Environment.GetEnvironmentVariable("DAXTER_ARTIFACTS_PURGE_HOURS");
        _interval = double.TryParse(hours, out var h) && h > 0
            ? TimeSpan.FromHours(h)
            : TimeSpan.FromHours(6);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (Environment.GetEnvironmentVariable("DAXTER_ARTIFACTS_PURGE_HOURS") == "0")
        {
            _log.LogInformation("ArtifactPurgeHostedService disabled (DAXTER_ARTIFACTS_PURGE_HOURS=0).");
            return;
        }

        _log.LogInformation("ArtifactPurgeHostedService started — cadence {Hours}h.", _interval.TotalHours);
        // First tick on startup so a long-stopped deployment catches up immediately. Then on
        // the configured interval. Using a PeriodicTimer keeps drift bounded across restarts.
        using var timer = new PeriodicTimer(_interval);
        do
        {
            try
            {
                var freed = await _store.PurgeExpiredAsync(stoppingToken);
                if (freed > 0)
                    _log.LogInformation("Artifact TTL purge freed {Bytes:N0} bytes.", freed);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                // A failed purge is non-fatal — try again next tick. Don't crash the host
                // for what's effectively a janitor task.
                _log.LogWarning(ex, "Artifact TTL purge failed; will retry on next tick.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
