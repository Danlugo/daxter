using Daxter.Core;
using Daxter.Core.Formatting;
using Daxter.Core.Query;

namespace Daxter.Core.Tests;

public class FormatterTests
{
    private static QueryResult Sample() => new(
        ["Name", "Amount"],
        [
            ["Alice", 10.5],
            ["Bob", null],
        ]);

    [Fact]
    public void Csv_writes_header_and_rows_invariant()
    {
        var csv = new CsvResultFormatter().Format(Sample());
        var lines = csv.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');

        Assert.Equal("Name,Amount", lines[0]);
        Assert.Equal("Alice,10.5", lines[1]); // invariant decimal point
        Assert.Equal("Bob,", lines[2]);        // null -> empty
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("has,comma", "\"has,comma\"")]
    [InlineData("has\"quote", "\"has\"\"quote\"")]
    [InlineData("has\nnewline", "\"has\nnewline\"")]
    public void Csv_quotes_fields_per_rfc4180(string value, string expectedCell)
    {
        var result = new QueryResult(["Col"], [[value]]);
        var csv = new CsvResultFormatter().Format(result).Replace("\r\n", "\n");
        Assert.Contains(expectedCell, csv);
    }

    [Fact]
    public void Json_emits_array_of_objects_with_nulls()
    {
        var json = new JsonResultFormatter().Format(Sample());
        Assert.Contains("\"Name\": \"Alice\"", json);
        Assert.Contains("\"Amount\": 10.5", json);
        Assert.Contains("\"Amount\": null", json);
        Assert.StartsWith("[", json.TrimStart());
    }

    [Fact]
    public void Table_renders_columns_and_values()
    {
        var table = new TableResultFormatter().Format(Sample());
        Assert.Contains("Name", table);
        Assert.Contains("Amount", table);
        Assert.Contains("Alice", table);
        Assert.Contains("10.5", table);
    }

    [Fact]
    public void Table_handles_no_columns()
    {
        var table = new TableResultFormatter().Format(QueryResult.Empty);
        Assert.Contains("no columns", table);
    }

    [Theory]
    [InlineData(null, typeof(TableResultFormatter))]
    [InlineData("", typeof(TableResultFormatter))]
    [InlineData("table", typeof(TableResultFormatter))]
    [InlineData("CSV", typeof(CsvResultFormatter))]
    [InlineData("Json", typeof(JsonResultFormatter))]
    public void Factory_parses_and_creates_correct_formatter(string? value, Type expected)
    {
        var format = ResultFormatterFactory.Parse(value);
        Assert.IsType(expected, ResultFormatterFactory.Create(format));
    }

    [Fact]
    public void Factory_parse_rejects_unknown_format()
    {
        var ex = Assert.Throws<DaxterException>(() => ResultFormatterFactory.Parse("yaml"));
        Assert.Contains("yaml", ex.Message);
    }
}
