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
