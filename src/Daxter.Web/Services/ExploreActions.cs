namespace Daxter.Web.Services;

/// <summary>
/// Scoped bus that hands a DAX query to the <c>/query</c> page — from the global sidebar
/// (Recent queries) or from the Browse page ("Top N rows", "Run measure"). Everything lives in
/// the same circuit, so the scoped instance is shared. If the Query page is already mounted it
/// runs the query immediately (via <see cref="PendingChanged"/>); otherwise the page picks it up
/// from <see cref="Pending"/> when it loads after the caller navigates to <c>/query</c>.
/// </summary>
public sealed class ExploreActions
{
    /// <summary>Raised when a query is handed over while the Query page is already mounted.</summary>
    public event Func<Task>? PendingChanged;

    /// <summary>A query waiting to be run on the Query page (consumed via <see cref="TakePending"/>).</summary>
    public QueryEntry? Pending { get; private set; }

    /// <summary>Hand a query to the Query page. Caller should then navigate to <c>/query</c>.</summary>
    public async Task Run(QueryEntry entry)
    {
        Pending = entry;
        if (PendingChanged is not null) await PendingChanged();
    }

    /// <summary>The Query page takes (and clears) the pending query so it isn't re-run on the next visit.</summary>
    public QueryEntry? TakePending()
    {
        var p = Pending;
        Pending = null;
        return p;
    }
}
