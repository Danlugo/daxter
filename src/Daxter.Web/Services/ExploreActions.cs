namespace Daxter.Web.Services;

/// <summary>
/// Scoped bus so the global sidebar (MainLayout) can ask the Explore page to run a saved query.
/// Both live in the same circuit, so the scoped instance is shared.
/// </summary>
public sealed class ExploreActions
{
    public event Func<QueryEntry, Task>? RunRequested;

    public async Task Run(QueryEntry entry)
    {
        if (RunRequested is not null) await RunRequested(entry);
    }
}
