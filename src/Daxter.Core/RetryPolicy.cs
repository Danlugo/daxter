namespace Daxter.Core;

/// <summary>
/// Retries a transient operation up to <c>retries</c> additional times (so <c>retries + 1</c> total
/// attempts) with linear backoff. Used to make refresh/maintenance resilient to transient XMLA/REST
/// failures (connection drops, timeouts, throttling). Note: this retries the <i>operation within the
/// process</i> — it cannot recover from the process itself being killed externally.
/// </summary>
public static class RetryPolicy
{
    /// <summary>Max backoff between attempts (caps the linear growth).</summary>
    public static TimeSpan MaxBackoff { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Backoff before the Nth retry (0-based attempt index): 5s, 10s, 15s … capped.</summary>
    public static TimeSpan Backoff(int attemptIndex) =>
        TimeSpan.FromSeconds(Math.Min(MaxBackoff.TotalSeconds, 5 * (attemptIndex + 1)));

    /// <summary>
    /// Runs <paramref name="action"/>; on failure retries up to <paramref name="retries"/> more times.
    /// <paramref name="onRetry"/> is invoked as (attemptNumber, totalRetries, error) before each wait.
    /// </summary>
    public static void Execute(Action action, int retries, Action<int, int, Exception>? onRetry = null,
        Func<TimeSpan, bool>? sleep = null)
    {
        var max = Math.Max(0, retries);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (Exception ex)
            {
                if (attempt >= max) throw;
                onRetry?.Invoke(attempt + 1, max, ex);
                var delay = Backoff(attempt);
                if (sleep is null) Thread.Sleep(delay); else sleep(delay);
            }
        }
    }

    /// <summary>Async variant of <see cref="Execute"/>.</summary>
    public static async Task ExecuteAsync(Func<Task> action, int retries,
        Action<int, int, Exception>? onRetry = null, CancellationToken ct = default)
    {
        var max = Math.Max(0, retries);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await action();
                return;
            }
            catch (Exception ex)
            {
                if (attempt >= max) throw;
                onRetry?.Invoke(attempt + 1, max, ex);
                await Task.Delay(Backoff(attempt), ct);
            }
        }
    }
}
