using PaymentGateway.Api.Domain;

namespace PaymentGateway.Api.Providers;

public enum ChargeOutcome
{
    Succeeded,
    /// <summary>Provider accepted the charge; the final result comes later by webhook.</summary>
    Pending,
    Declined
}

/// <param name="IdempotencyKey">
/// Sent to the provider on every retry, so a retried request can never charge the customer twice.
/// </param>
public sealed record ChargeRequest(string PaymentId, Money Amount, PaymentMethod Method, string IdempotencyKey);

public sealed record ChargeResult(ChargeOutcome Outcome, string? ProviderPaymentId, string? DeclineReason)
{
    public static ChargeResult Success(string providerPaymentId) => new(ChargeOutcome.Succeeded, providerPaymentId, null);
    public static ChargeResult Pending(string providerPaymentId) => new(ChargeOutcome.Pending, providerPaymentId, null);
    public static ChargeResult Declined(string reason) => new(ChargeOutcome.Declined, null, reason);
}

public sealed record RefundRequest(string ProviderPaymentId, long AmountMinorUnits, string Currency, string IdempotencyKey);

public sealed record RefundResult(string ProviderRefundId);

/// <summary>
/// A payment provider (acquirer, PSP or wallet). Each real provider would get its own
/// implementation that maps its API to these calls.
/// </summary>
public interface IPaymentProvider
{
    string Name { get; }

    bool Supports(PaymentMethod method, string currency);

    /// <exception cref="ProviderTransientException">Timeout, 5xx or rate limit: safe to retry.</exception>
    Task<ChargeResult> ChargeAsync(ChargeRequest request, CancellationToken cancellationToken);

    /// <exception cref="ProviderTransientException">Timeout, 5xx or rate limit: safe to retry.</exception>
    Task<RefundResult> RefundAsync(RefundRequest request, CancellationToken cancellationToken);
}

/// <summary>A temporary provider failure (timeout, HTTP 5xx, rate limit) that is safe to retry.</summary>
public sealed class ProviderTransientException(string provider, string message) : Exception(message)
{
    public string Provider { get; } = provider;
}
