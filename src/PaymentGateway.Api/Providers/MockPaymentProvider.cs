using System.Collections.Concurrent;
using PaymentGateway.Api.Domain;

namespace PaymentGateway.Api.Providers;

/// <summary>
/// A simulated provider. Its behaviour depends on the last two digits of the amount,
/// like the test card numbers real providers publish, so every flow can be tried by hand:
/// <list type="bullet">
/// <item>..13 → declined (insufficient funds)</item>
/// <item>..42 → fails twice with a timeout, then succeeds (shows retries)</item>
/// <item>..77 → pending; the result arrives later by webhook</item>
/// <item>..99 → the primary provider is down (shows failover to the backup)</item>
/// <item>..98 → every provider is down (payment fails)</item>
/// <item>anything else → succeeds</item>
/// </list>
/// </summary>
public sealed class MockPaymentProvider(string name, IEnumerable<PaymentMethod> methods, bool isPrimary) : IPaymentProvider
{
    private readonly HashSet<PaymentMethod> _methods = [.. methods];
    private readonly ConcurrentDictionary<string, int> _attemptsByKey = new();
    private readonly ConcurrentDictionary<string, ChargeResult> _completedCharges = new();

    public string Name { get; } = name;

    public bool Supports(PaymentMethod method, string currency) =>
        _methods.Contains(method) && Money.IsValidCurrency(currency);

    public Task<ChargeResult> ChargeAsync(ChargeRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Like a real provider: the same idempotency key always returns the first result.
        if (_completedCharges.TryGetValue(request.IdempotencyKey, out var previous))
            return Task.FromResult(previous);

        var attempt = _attemptsByKey.AddOrUpdate(request.IdempotencyKey, 1, (_, n) => n + 1);
        var scenario = request.Amount.MinorUnits % 100;

        if (scenario == 98 || (scenario == 99 && isPrimary))
            throw new ProviderTransientException(Name, $"{Name} timed out.");

        if (scenario == 42 && attempt <= 2)
            throw new ProviderTransientException(Name, $"{Name} returned HTTP 503 (attempt {attempt}).");

        var result = scenario switch
        {
            13 => ChargeResult.Declined("insufficient_funds"),
            77 => ChargeResult.Pending(NewId("ch")),
            _ => ChargeResult.Success(NewId("ch"))
        };

        _completedCharges[request.IdempotencyKey] = result;
        return Task.FromResult(result);
    }

    public Task<RefundResult> RefundAsync(RefundRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new RefundResult(NewId("re")));
    }

    private string NewId(string prefix) => $"{Name}_{prefix}_{Guid.NewGuid():N}";
}
