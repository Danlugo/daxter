using Daxter.Core.Connection;
using Daxter.Core.Metadata;
using Daxter.Core.Query;

namespace Daxter.Core.Tests;

public class ModelDiffServiceTests
{
    private sealed class MeasureSession(params (string Name, string Expr)[] measures) : IXmlaSession
    {
        public QueryResult Execute(string query)
            => new(["Name", "Expression"], measures.Select(m => new object?[] { m.Name, m.Expr }).ToList());

        public void ExecuteCommand(string command) { }
        public void Dispose() { }
    }

    [Fact]
    public void Diff_reports_added_removed_changed_and_omits_identical()
    {
        var left = new MeasureSession(("A", "1"), ("B", "2"), ("Same", "x"));
        var right = new MeasureSession(("B", "9"), ("C", "3"), ("Same", "x"));

        var diff = ModelDiffService.DiffMeasures(left, right);
        var byName = diff.Rows.ToDictionary(r => (string)r[1]!, r => (string)r[0]!);

        Assert.Equal("removed", byName["A"]); // only in left
        Assert.Equal("changed", byName["B"]); // expr differs
        Assert.Equal("added", byName["C"]);   // only in right
        Assert.False(byName.ContainsKey("Same")); // identical → omitted
    }

    [Fact]
    public void Diff_of_identical_models_is_empty()
    {
        var a = new MeasureSession(("A", "1"));
        var b = new MeasureSession(("A", "1"));
        Assert.Equal(0, ModelDiffService.DiffMeasures(a, b).RowCount);
    }
}
