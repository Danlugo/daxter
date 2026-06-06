using Daxter.Core.Maintenance;
using Daxter.Core.Scheduling;

namespace Daxter.Core.Tests;

public class EnhancedRefreshTests
{
    private static RefreshSpec Spec(RefreshKind kind, string? table = "FACT",
        IReadOnlyList<string>? partitions = null, int retries = 0) =>
        new(kind, "WS", "M", table, null, PartitionOrder.NewestFirst, RefreshType.Full, partitions, retries);

    [Fact]
    public void BuildBody_all_partitions_uses_partialBatch_and_table_object()
    {
        var json = EnhancedRefresh.BuildBody(Spec(RefreshKind.AllPartitions), maxParallelism: 4);

        Assert.Contains("\"commitMode\":\"partialBatch\"", json);
        Assert.Contains("\"applyRefreshPolicy\":false", json);   // required with partialBatch
        Assert.Contains("\"maxParallelism\":4", json);
        Assert.Contains("\"retryCount\":1", json);               // floored to at least 1
        Assert.Contains("\"table\":\"FACT\"", json);
        Assert.DoesNotContain("partition", json);                // AllPartitions submits table-only; server expands
    }

    [Fact]
    public void BuildBody_some_partitions_lists_each_and_clamps_parallelism()
    {
        var json = EnhancedRefresh.BuildBody(Spec(RefreshKind.SomePartitions, partitions: ["P1", "P2"], retries: 3), maxParallelism: 50);

        Assert.Contains("\"maxParallelism\":10", json);          // clamped to 10
        Assert.Contains("\"retryCount\":3", json);
        Assert.Contains("\"partition\":\"P1\"", json);
        Assert.Contains("\"partition\":\"P2\"", json);
    }

    [Fact]
    public void BuildBody_model_is_transactional_with_no_objects()
    {
        var json = EnhancedRefresh.BuildBody(Spec(RefreshKind.Model, table: null), maxParallelism: 4);

        Assert.Contains("\"commitMode\":\"transactional\"", json);
        Assert.DoesNotContain("objects", json);
        Assert.DoesNotContain("applyRefreshPolicy", json);
    }
}
