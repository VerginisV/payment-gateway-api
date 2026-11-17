using PaymentGateway.Api.Domain;

namespace PaymentGateway.Api.Persistence;

public interface IPaymentRepository
{
    Task AddAsync(Payment payment, CancellationToken cancellationToken);

    /// <summary>Returns a copy; change it and save it with <see cref="UpdateAsync"/>.</summary>
    Task<Payment?> GetAsync(string id, CancellationToken cancellationToken);

    Task<Payment?> FindByProviderPaymentIdAsync(string provider, string providerPaymentId, CancellationToken cancellationToken);

    /// <summary>
    /// Saves only if nobody else saved the payment since it was loaded (optimistic concurrency).
    /// </summary>
    /// <exception cref="ConcurrencyConflictException">The stored version is not <paramref name="expectedVersion"/>.</exception>
    Task UpdateAsync(Payment payment, int expectedVersion, CancellationToken cancellationToken);
}

public sealed class ConcurrencyConflictException(string message) : Exception(message);

/// <summary>
/// Thread-safe in-memory store. Swap for a database implementation (for example EF Core with
/// a rowversion column) without changing the services, since they only see the interface.
/// </summary>
public sealed class InMemoryPaymentRepository : IPaymentRepository
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Payment> _payments = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Provider, string ProviderPaymentId), string> _byProviderId = [];

    public Task AddAsync(Payment payment, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!_payments.TryAdd(payment.Id, payment.Clone()))
                throw new InvalidOperationException($"Payment {payment.Id} already exists.");
            IndexProviderId(payment);
        }
        return Task.CompletedTask;
    }

    public Task<Payment?> GetAsync(string id, CancellationToken cancellationToken)
    {
        lock (_gate)
            return Task.FromResult(_payments.TryGetValue(id, out var p) ? p.Clone() : null);
    }

    public Task<Payment?> FindByProviderPaymentIdAsync(string provider, string providerPaymentId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var found = _byProviderId.TryGetValue((provider, providerPaymentId), out var id)
                        && _payments.TryGetValue(id, out var p)
                ? p.Clone()
                : null;
            return Task.FromResult(found);
        }
    }

    public Task UpdateAsync(Payment payment, int expectedVersion, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!_payments.TryGetValue(payment.Id, out var stored))
                throw new InvalidOperationException($"Payment {payment.Id} does not exist.");
            if (stored.Version != expectedVersion)
                throw new ConcurrencyConflictException(
                    $"Payment {payment.Id} was changed by someone else (version {stored.Version}, expected {expectedVersion}).");

            _payments[payment.Id] = payment.Clone();
            IndexProviderId(payment);
        }
        return Task.CompletedTask;
    }

    private void IndexProviderId(Payment payment)
    {
        if (payment.Provider is not null && payment.ProviderPaymentId is not null)
            _byProviderId[(payment.Provider, payment.ProviderPaymentId)] = payment.Id;
    }
}
