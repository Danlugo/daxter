using Daxter.Core;
using Daxter.Core.Connection;
using Daxter.Core.Query;
using Daxter.Core.Scheduling;

namespace Daxter.Core.Tests;

public class SerialPartitionRefreshTests
{
    private sealed class FakeSession : IXmlaSession
    {
        public List<string> Commands { get; } = [];
        public bool Disposed { get; private set; }
        public QueryResult Execute(string query) => QueryResult.Empty;
        public void ExecuteCommand(string command) => Commands.Add(command);
        public void Dispose() => Disposed = true;
    }

    private sealed class FakeProgress : IRefreshProgress
    {
        public List<string> Events { get; } = [];
        public int Done { get; private set; }
        public int Total { get; private set; }
        public bool Cancel { get; set; }
        public void Event(string message) => Events.Add(message);
        public void Partitions(int done, int total) { Done = done; Total = total; }
        public void Plan(IReadOnlyList<string> orderedPartitions) => Total = orderedPartitions.Count;
        public bool CancelRequested => Cancel;
    }

    // Regression for "Refreshing the expired access-token has failed": a long serial refresh must open
    // a FRESH session (=> fresh token) per partition, not reuse one session whose token expires mid-run.
    [Fact]
    public async Task Opens_a_fresh_session_per_partition_and_disposes_each()
    {
        var opened = new List<FakeSession>();
        var progress = new FakeProgress();
        string[] parts = ["2026Q1", "2025Q4", "2025Q3"];

        await SerialPartitionRefresh.RunAsync(
            "Sales", parts, "DB",
            buildRefresh: (_, name) => $"refresh {name}",
            openSession: _ => { var s = new FakeSession(); opened.Add(s); return Task.FromResult<IXmlaSession>(s); },
            retries: 0, progress, CancellationToken.None);

        Assert.Equal(parts.Length, opened.Count);                                  // one NEW session per partition
        Assert.All(opened, s => Assert.True(s.Disposed));                          // each disposed
        Assert.Equal(parts.Select(p => $"refresh {p}"), opened.Select(s => s.Commands.Single()));  // right tmsl on each
        Assert.Equal(parts.Length, progress.Total);
        Assert.Equal(parts.Length, progress.Done);                                 // progress reached total
    }

    // A session whose refresh command fails once (a dropped connection) then works.
    private sealed class FlakySession : IXmlaSession
    {
        private readonly bool _fail;
        public FlakySession(bool failFirst) => _fail = failFirst;
        public bool Disposed { get; private set; }
        public QueryResult Execute(string query) => QueryResult.Empty;
        public void ExecuteCommand(string command) { if (_fail) throw new DaxterException("The connection is not open."); }
        public void Dispose() => Disposed = true;
    }

    // Regression for "The connection is not open": a dropped connection mid-refresh must be retried on a
    // FRESH session (re-opened), not the dead one — otherwise the retry can't recover.
    [Fact]
    public async Task Retries_a_dropped_connection_by_reopening_a_fresh_session()
    {
        var savedBackoff = RetryPolicy.MaxBackoff;
        RetryPolicy.MaxBackoff = TimeSpan.FromMilliseconds(1);   // keep the test fast
        try
        {
            var opened = new List<FlakySession>();
            var progress = new FakeProgress();

            await SerialPartitionRefresh.RunAsync(
                "Sales", ["2026Q1"], "DB",
                buildRefresh: (_, n) => n,
                openSession: _ =>
                {
                    var s = new FlakySession(failFirst: opened.Count == 0);   // first drops, re-open succeeds
                    opened.Add(s);
                    return Task.FromResult<IXmlaSession>(s);
                },
                retries: 0, progress, CancellationToken.None);                  // retries=0 → MinRetries floor kicks in

            Assert.Equal(2, opened.Count);          // re-opened a fresh session on the retry
            Assert.True(opened[0].Disposed);        // the dropped session was disposed
            Assert.Equal(1, progress.Done);         // partition completed after the retry
        }
        finally { RetryPolicy.MaxBackoff = savedBackoff; }
    }

    [Fact]
    public async Task Stops_and_throws_when_cancel_requested()
    {
        var opened = new List<FakeSession>();
        var progress = new FakeProgress { Cancel = true };

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            SerialPartitionRefresh.RunAsync(
                "Sales", ["P1", "P2"], "DB",
                buildRefresh: (_, n) => n,
                openSession: _ => { var s = new FakeSession(); opened.Add(s); return Task.FromResult<IXmlaSession>(s); },
                retries: 0, progress, CancellationToken.None));

        Assert.Empty(opened);   // cancelled before opening any session
    }

    [Fact]
    public async Task Empty_partition_list_is_rejected()
    {
        await Assert.ThrowsAsync<DaxterException>(() =>
            SerialPartitionRefresh.RunAsync(
                "Sales", [], "DB",
                buildRefresh: (_, n) => n,
                openSession: _ => Task.FromResult<IXmlaSession>(new FakeSession()),
                retries: 0, new FakeProgress(), CancellationToken.None));
    }
}
