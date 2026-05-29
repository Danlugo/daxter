using Daxter.Core.Auth;
using Daxter.Core.Query;

namespace Daxter.Core.Tests;

public class QueryResultTests
{
    [Fact]
    public void Empty_has_no_columns_or_rows()
    {
        Assert.Equal(0, QueryResult.Empty.ColumnCount);
        Assert.Equal(0, QueryResult.Empty.RowCount);
    }

    [Fact]
    public void Reports_counts()
    {
        var result = new QueryResult(["A", "B"], [[1, 2], [3, 4], [5, 6]]);
        Assert.Equal(2, result.ColumnCount);
        Assert.Equal(3, result.RowCount);
    }

    [Theory]
    [InlineData(10, false)]   // expires in 10 min, 5 min skew -> not expired
    [InlineData(2, true)]     // expires in 2 min, 5 min skew -> treated as expired
    [InlineData(-1, true)]    // already past
    public void AccessToken_IsExpired_respects_skew(int minutesFromNow, bool expected)
    {
        var token = new XmlaAccessToken("t", DateTimeOffset.UtcNow.AddMinutes(minutesFromNow));
        Assert.Equal(expected, token.IsExpired(TimeSpan.FromMinutes(5)));
    }
}
