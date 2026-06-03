using Daxter.Core;

namespace Daxter.Core.Tests;

public class RetryPolicyTests
{
    [Fact]
    public void Succeeds_first_try_no_retry()
    {
        var calls = 0;
        RetryPolicy.Execute(() => calls++, retries: 3, sleep: _ => true);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Retries_then_succeeds()
    {
        var calls = 0;
        var retried = 0;
        RetryPolicy.Execute(
            () => { calls++; if (calls < 3) throw new InvalidOperationException("transient"); },
            retries: 3,
            onRetry: (_, _, _) => retried++,
            sleep: _ => true);

        Assert.Equal(3, calls);    // failed twice, succeeded on the 3rd
        Assert.Equal(2, retried);  // two retries fired
    }

    [Fact]
    public void Exhausts_retries_then_throws_last_error()
    {
        var calls = 0;
        var ex = Assert.Throws<DaxterException>(() => RetryPolicy.Execute(
            () => { calls++; throw new DaxterException($"fail {calls}"); },
            retries: 2, sleep: _ => true));

        Assert.Equal(3, calls);        // 1 initial + 2 retries
        Assert.Equal("fail 3", ex.Message);
    }

    [Fact]
    public void Zero_retries_runs_once_and_propagates()
    {
        var calls = 0;
        Assert.Throws<DaxterException>(() => RetryPolicy.Execute(
            () => { calls++; throw new DaxterException("x"); }, retries: 0, sleep: _ => true));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Backoff_grows_linearly_and_caps()
    {
        Assert.Equal(5, RetryPolicy.Backoff(0).TotalSeconds);
        Assert.Equal(10, RetryPolicy.Backoff(1).TotalSeconds);
        Assert.Equal(30, RetryPolicy.Backoff(10).TotalSeconds);   // capped at MaxBackoff (30s)
    }
}
