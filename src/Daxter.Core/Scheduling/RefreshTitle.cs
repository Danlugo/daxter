using Daxter.Core.Maintenance;

namespace Daxter.Core.Scheduling;

/// <summary>
/// Shared, human-readable naming for a <see cref="RefreshSpec"/>. Used by every interface so a job's
/// title and plan description read identically whether it was enqueued from the CLI, the MCP server
/// or the web console.
/// </summary>
public static class RefreshTitle
{
    private static string OrderText(PartitionOrder o) =>
        o == PartitionOrder.NewestFirst ? "newest→oldest" : "oldest→newest";

    private static string TypeSuffix(RefreshType t) => t == RefreshType.Full ? "" : $" [{t}]";

    /// <summary>Short title for the Jobs list (e.g. "Refresh all partitions · FACT (newest→oldest)").</summary>
    public static string For(RefreshSpec s)
    {
        var t = TypeSuffix(s.Type);
        return s.Kind switch
        {
            RefreshKind.Model => $"Refresh model · {s.Dataset}{t}",
            RefreshKind.Table => $"Refresh table · {s.Table}{t}",
            RefreshKind.Partition => $"Refresh partition · {s.Table}[{s.Partition}]{t}",
            RefreshKind.AllPartitions => $"Refresh all partitions · {s.Table} ({OrderText(s.Order)}){t}",
            RefreshKind.SomePartitions => $"Refresh {s.Partitions?.Count ?? 0} partitions · {s.Table}{t}",
            _ => "Refresh",
        };
    }

    /// <summary>Longer plan description for CLI/MCP confirmation + dry-run (e.g. "all partitions of
    /// 'FACT' (newest→oldest) in 'WS - Dev' / 'Model', retry 3×").</summary>
    public static string Describe(RefreshSpec s)
    {
        var core = s.Kind switch
        {
            RefreshKind.Model => "the whole model",
            RefreshKind.Table => $"table '{s.Table}'",
            RefreshKind.Partition => $"partition '{s.Partition}' of '{s.Table}'",
            RefreshKind.SomePartitions => $"{s.Partitions?.Count ?? 0} selected partition(s) of '{s.Table}'",
            RefreshKind.AllPartitions => $"all partitions of '{s.Table}' ({OrderText(s.Order)})",
            _ => "the model",
        };
        var type = s.Type == RefreshType.Full ? "" : $", type {s.Type}";
        var retry = s.Retries > 0 ? $", retry {s.Retries}×" : "";
        return $"refresh {core} in '{s.Workspace}' / '{s.Dataset}'{type}{retry}";
    }
}
