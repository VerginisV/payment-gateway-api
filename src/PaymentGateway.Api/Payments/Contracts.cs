using PaymentGateway.Api.Domain;

namespace PaymentGateway.Api.Payments;

public sealed record CreatePaymentRequest(
    long AmountMinorUnits,
    string Currency,
    PaymentMethod Method,
    string MerchantReference);

public sealed record CreateRefundRequest(long AmountMinorUnits);

public sealed record AttemptResponse(string Provider, int AttemptNumber, string Outcome, DateTimeOffset At);

public sealed record RefundResponse(string Id, long AmountMinorUnits, DateTimeOffset CreatedAt);

public sealed record PaymentResponse(
    string Id,
    string MerchantReference,
    long AmountMinorUnits,
    string Currency,
    PaymentMethod Method,
    PaymentStatus Status,
    string? Provider,
    string? ProviderPaymentId,
    string? FailureReason,
    long RefundedMinorUnits,
    IReadOnlyList<AttemptResponse> Attempts,
    IReadOnlyList<RefundResponse> Refunds,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public static PaymentResponse From(Payment p) => new(
        p.Id,
        p.MerchantReference,
        p.Amount.MinorUnits,
        p.Amount.Currency,
        p.Method,
        p.Status,
        p.Provider,
        p.ProviderPaymentId,
        p.FailureReason,
        p.RefundedMinorUnits,
        [.. p.Attempts.Select(a => new AttemptResponse(a.Provider, a.AttemptNumber, a.Outcome, a.At))],
        [.. p.Refunds.Select(r => new RefundResponse(r.Id, r.AmountMinorUnits, r.CreatedAt))],
        p.CreatedAt,
        p.UpdatedAt);
}

public static class PaymentValidation
{
    public const long MaxAmountMinorUnits = 10_000_000; // 100,000.00

    public static Dictionary<string, string[]> Validate(CreatePaymentRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        if (request.AmountMinorUnits <= 0 || request.AmountMinorUnits > MaxAmountMinorUnits)
            errors["amountMinorUnits"] = [$"Must be between 1 and {MaxAmountMinorUnits}."];
        if (!Money.IsValidCurrency(request.Currency))
            errors["currency"] = [$"Must be one of: {string.Join(", ", Money.SupportedCurrencies)}."];
        if (!Enum.IsDefined(request.Method))
            errors["method"] = ["Unknown payment method."];
        if (string.IsNullOrWhiteSpace(request.MerchantReference) || request.MerchantReference.Length > 64)
            errors["merchantReference"] = ["Required, at most 64 characters."];
        return errors;
    }
}
