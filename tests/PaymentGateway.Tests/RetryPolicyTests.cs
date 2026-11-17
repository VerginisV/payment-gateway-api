using PaymentGateway.Api.Providers;

namespace PaymentGateway.Tests;

public class RetryPolicyTests
{
    [Fact]
    public async Task Retries_transient_errors_until_success()
    {
        var delay = new NoRetryDelay();
        var policy = Harness.CreateRetryPolicy(delay, maxAttempts: 3);
        var calls = 0;

        var result = await policy.ExecuteAsync((attempt, _) =>
        {
            calls++;
            if (attempt < 3) throw new ProviderTransientException("p", "timeout");
            return Task.FromResult("ok");
        }, null, CancellationToken.None);

        Assert.Equal("ok", result);
        Assert.Equal(3, calls);
        Assert.Equal(2, delay.Waits.Count);
    }

    [Fact]
    public async Task Gives_up_after_max_attempts()
    {
        var policy = Harness.CreateRetryPolicy(new NoRetryDelay(), maxAttempts: 3);
        var failures = 0;

        await Assert.ThrowsAsync<ProviderTransientException>(() => policy.ExecuteAsync<string>(
            (_, _) => throw new ProviderTransientException("p", "down"),
            (_, _) => failures++,
            CancellationToken.None));

        Assert.Equal(3, failures);
    }

    [Fact]
    public async Task Does_not_retry_other_errors()
    {
        var policy = Harness.CreateRetryPolicy(new NoRetryDelay());
        var calls = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(() => policy.ExecuteAsync<string>((_, _) =>
        {
            calls++;
            throw new InvalidOperationException("bug");
        }, null, CancellationToken.None));

        Assert.Equal(1, calls);
    }

    [Fact]
    public void Backoff_grows_exponentially_with_bounded_jitter()
    {
        var policy = Harness.CreateRetryPolicy(new NoRetryDelay());

        for (var attempt = 1; attempt <= 4; attempt++)
        {
            var baseMs = 100 * Math.Pow(2, attempt - 1);
            var wait = policy.BackoffFor(attempt).TotalMilliseconds;
            Assert.InRange(wait, baseMs, baseMs * 1.2);
        }
    }
}
