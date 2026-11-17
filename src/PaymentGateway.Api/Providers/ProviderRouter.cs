using PaymentGateway.Api.Domain;

namespace PaymentGateway.Api.Providers;

/// <summary>
/// Chooses which providers can process a payment, in priority order.
/// The first is the primary; the rest are used for failover when it is down.
/// </summary>
public sealed class ProviderRouter(IEnumerable<IPaymentProvider> providers)
{
    private readonly IReadOnlyList<IPaymentProvider> _providers = [.. providers];

    public IReadOnlyList<IPaymentProvider> CandidatesFor(PaymentMethod method, string currency) =>
        [.. _providers.Where(p => p.Supports(method, currency))];

    public IPaymentProvider? Find(string name) =>
        _providers.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
}
