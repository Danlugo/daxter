using Daxter.Core.Rest;

namespace Daxter.Core.Tests;

public class PipelineRulesServiceTests
{
    private static readonly StageColumn[] Stages = [new(0, "Dev"), new(1, "Prod")];

    // Dev→Prod parameter matrix; Prod values: Sales/Returns = DB_PROD, Inventory = DB_STAGING (anomaly).
    private static PipelineScan Scan()
    {
        ModelMatrix Model(string name, params string?[] prodAndDev) =>
            new(name, new PipelineParamMatrix(
                Stages,
                [new ParamRow("DB_NAME", prodAndDev, prodAndDev.Distinct().Count() > 1)],
                []));

        return new PipelineScan("pl1", Stages,
        [
            Model("Sales", "DB_DEV", "DB_PROD"),
            Model("Returns", "DB_DEV", "DB_PROD"),
            Model("Inventory", "DB_DEV", "DB_STAGING"),
            // No DB_NAME param → excluded from Checked.
            new("Misc", new PipelineParamMatrix(Stages, [new ParamRow("OTHER", ["x", "y"], true)], [])),
        ]);
    }

    // "≠ DB_PROD" is a finder: it returns the models whose value is NOT DB_PROD (the anomalies),
    // NOT the compliant DB_PROD ones. (Regression for the run-all "violations were inverted" bug.)
    [Fact]
    public void NotEquals_returns_models_whose_value_differs()
    {
        var r = PipelineRulesService.EvaluateRule(Scan(), "Prod", "DB_NAME", "DB_PROD", notEquals: true);

        Assert.Equal(3, r.Checked);                 // Sales, Returns, Inventory (Misc has no DB_NAME)
        Assert.Equal(1, r.Matched);
        var hit = Assert.Single(r.Matches);
        Assert.Equal("Inventory", hit.Model);
        Assert.Equal("DB_STAGING", hit.Actual);
    }

    [Fact]
    public void Equals_returns_models_whose_value_matches()
    {
        var r = PipelineRulesService.EvaluateRule(Scan(), "Prod", "DB_NAME", "DB_PROD", notEquals: false);

        Assert.Equal(3, r.Checked);
        Assert.Equal(2, r.Matched);
        Assert.Equal(["Returns", "Sales"], r.Matches.Select(m => m.Model).OrderBy(x => x).ToArray());
    }

    [Fact]
    public void Unknown_stage_yields_empty_result()
    {
        var r = PipelineRulesService.EvaluateRule(Scan(), "QA", "DB_NAME", "DB_PROD", notEquals: true);

        Assert.Equal(0, r.Checked);
        Assert.Equal(0, r.Matched);
        Assert.Empty(r.Matches);
    }
}
