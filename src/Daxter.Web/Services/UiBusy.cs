namespace Daxter.Web.Services;

/// <summary>
/// Circuit-scoped UI busy state. Pages wrap any async load in <see cref="Run"/>; <c>MainLayout</c>
/// renders one global blocking overlay (spinner) while anything is in flight — so a slow load (incl.
/// arriving on a page via a Frequent/deep-link click) shows a spinner and blocks clicks until done.
/// Counter-based so overlapping/nested loads don't clear the overlay early.
/// </summary>
public sealed class UiBusy
{
    private int _count;

    /// <summary>True while one or more <see cref="Run"/> operations are in flight.</summary>
    public bool IsBusy => _count > 0;

    /// <summary>Raised whenever the busy state changes (MainLayout re-renders the overlay).</summary>
    public event Action? Changed;

    /// <summary>Run an async load behind the global busy overlay. Always clears its own slice of the
    /// busy count, even on error (the exception still propagates to the caller).</summary>
    public async Task Run(Func<Task> work)
    {
        Begin();
        try { await work(); }
        finally { End(); }
    }

    /// <summary>Manual counterpart to <see cref="Run"/> for pages that already toggle a local busy
    /// flag in a try/finally — call <see cref="Begin"/> with the flag and <see cref="End"/> in finally.</summary>
    public void Begin() { _count++; Changed?.Invoke(); }
    public void End() { if (_count > 0) _count--; Changed?.Invoke(); }
}
