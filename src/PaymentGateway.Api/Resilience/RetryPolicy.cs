using Microsoft.Extensions.Options;
using PaymentGateway.Api.Providers;

namespace PaymentGateway.Api.Resilience;

public sealed class RetryOptions
{
    public int MaxAttempts { get; set; } = 3;
    public int BaseDelayMilliseconds { get; set; } = 200;
}

/// <summary>Abstracts waiting, so tests can retry instantly.</summary>
public interface IRetryDelay
{
    Task WaitAsync(TimeSpan delay, CancellationToken cancellationToken);
}

public sealed class TaskRetryDelay : IRetryDelay
{
    public Task WaitAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.Delay(delay, cancellationToken);
}

/// <summary>
/// Retries only transient provider errors, with exponential backoff and jitter
/// (200 ms, 400 ms, 800 ms ... plus up to 20% random), so many clients retrying
/// at once don't hit a recovering provider at the same moment.
/// Declines are never retried: retrying a declined card does not make it valid.
/// </summary>
public sealed class RetryPolicy(IOptions<RetryOptions> options, IRetryDelay delay, ILogger<RetryPolicy> logger)
{
    private readonly RetryOptions _options = options.Value;

    public async Task<T> ExecuteAsync<T>(
        Func<int, CancellationToken, Task<T>> action,
        Action<int, Exception>? onAttemptFailed,
        CancellationToken cancellationToken)
    {
        var maxAttempts = Math.Max(1, _options.MaxAttempts);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await action(attempt, cancellationToken);
            }
            catch (ProviderTransientException ex) when (attempt < maxAttempts)
            {
                onAttemptFailed?.Invoke(attempt, ex);
                var wait = BackoffFor(attempt);
                logger.LogWarning("Attempt {Attempt}/{MaxAttempts} on {Provider} failed: {Error}. Retrying in {Delay} ms.",
                    attempt, maxAttempts, ex.Provider, ex.Message, (int)wait.TotalMilliseconds);
                await delay.WaitAsync(wait, cancellationToken);
            }
            catch (ProviderTransientException ex)
            {
                onAttemptFailed?.Invoke(attempt, ex);
                throw;
            }
        }
    }

    public TimeSpan BackoffFor(int attempt)
    {
        var baseMs = _options.BaseDelayMilliseconds * Math.Pow(2, attempt - 1);
        var jitterMs = baseMs * 0.2 * Random.Shared.NextDouble();
        return TimeSpan.FromMilliseconds(baseMs + jitterMs);
    }
}
