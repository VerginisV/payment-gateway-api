namespace PaymentGateway.Api.Domain;

public enum PaymentStatus
{
    /// <summary>Accepted by the provider, final result arrives later by webhook.</summary>
    Pending,
    Succeeded,
    Declined,
    Failed,
    PartiallyRefunded,
    Refunded
}

public enum PaymentMethod
{
    Card,
    Wallet
}

public sealed record ProviderAttempt(
    string Provider,
    int AttemptNumber,
    string Outcome,
    DateTimeOffset At);

public sealed record Refund(
    string Id,
    long AmountMinorUnits,
    string ProviderRefundId,
    DateTimeOffset CreatedAt);

/// <summary>
/// A payment and its state machine. All state changes go through methods here,
/// so an invalid transition (for example refunding a declined payment) is impossible.
/// </summary>
public sealed class Payment
{
    private readonly List<ProviderAttempt> _attempts = [];
    private readonly List<Refund> _refunds = [];

    public Payment(string id, string merchantReference, Money amount, PaymentMethod method, DateTimeOffset createdAt)
    {
        Id = id;
        MerchantReference = merchantReference;
        Amount = amount;
        Method = method;
        CreatedAt = createdAt;
        UpdatedAt = createdAt;
        Status = PaymentStatus.Pending;
    }

    public string Id { get; }
    public string MerchantReference { get; }
    public Money Amount { get; }
    public PaymentMethod Method { get; }
    public PaymentStatus Status { get; private set; }
    public string? Provider { get; private set; }
    public string? ProviderPaymentId { get; private set; }
    public string? FailureReason { get; private set; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Incremented on every change; used for optimistic concurrency.</summary>
    public int Version { get; private set; }

    public IReadOnlyList<ProviderAttempt> Attempts => _attempts;
    public IReadOnlyList<Refund> Refunds => _refunds;

    public long RefundedMinorUnits => _refunds.Sum(r => r.AmountMinorUnits);
    public long RefundableMinorUnits =>
        Status is PaymentStatus.Succeeded or PaymentStatus.PartiallyRefunded
            ? Amount.MinorUnits - RefundedMinorUnits
            : 0;

    public bool IsFinal => Status is not PaymentStatus.Pending;

    public void RecordAttempt(string provider, int attemptNumber, string outcome, DateTimeOffset at)
    {
        _attempts.Add(new ProviderAttempt(provider, attemptNumber, outcome, at));
        Touch(at);
    }

    public void MarkSucceeded(string provider, string providerPaymentId, DateTimeOffset at)
    {
        EnsureStatus(PaymentStatus.Pending);
        Provider = provider;
        ProviderPaymentId = providerPaymentId;
        Status = PaymentStatus.Succeeded;
        Touch(at);
    }

    public void MarkPendingAtProvider(string provider, string providerPaymentId, DateTimeOffset at)
    {
        EnsureStatus(PaymentStatus.Pending);
        Provider = provider;
        ProviderPaymentId = providerPaymentId;
        Touch(at);
    }

    public void MarkDeclined(string provider, string reason, DateTimeOffset at)
    {
        EnsureStatus(PaymentStatus.Pending);
        Provider = provider;
        FailureReason = reason;
        Status = PaymentStatus.Declined;
        Touch(at);
    }

    public void MarkFailed(string reason, DateTimeOffset at)
    {
        EnsureStatus(PaymentStatus.Pending);
        FailureReason = reason;
        Status = PaymentStatus.Failed;
        Touch(at);
    }

    public void AddRefund(Refund refund)
    {
        if (refund.AmountMinorUnits <= 0)
            throw new InvalidPaymentStateException("Refund amount must be positive.");
        if (refund.AmountMinorUnits > RefundableMinorUnits)
            throw new InvalidPaymentStateException(
                $"Refund of {refund.AmountMinorUnits} exceeds refundable amount {RefundableMinorUnits}.");

        _refunds.Add(refund);
        Status = RefundedMinorUnits == Amount.MinorUnits
            ? PaymentStatus.Refunded
            : PaymentStatus.PartiallyRefunded;
        Touch(refund.CreatedAt);
    }

    /// <summary>Deep copy, so callers never mutate the stored instance by accident.</summary>
    public Payment Clone()
    {
        var copy = new Payment(Id, MerchantReference, Amount, Method, CreatedAt)
        {
            Status = Status,
            Provider = Provider,
            ProviderPaymentId = ProviderPaymentId,
            FailureReason = FailureReason,
            UpdatedAt = UpdatedAt,
            Version = Version
        };
        copy._attempts.AddRange(_attempts);
        copy._refunds.AddRange(_refunds);
        return copy;
    }

    private void EnsureStatus(PaymentStatus expected)
    {
        if (Status != expected)
            throw new InvalidPaymentStateException($"Payment {Id} is {Status}, expected {expected}.");
    }

    private void Touch(DateTimeOffset at)
    {
        UpdatedAt = at;
        Version++;
    }
}

public sealed class InvalidPaymentStateException(string message) : Exception(message);
