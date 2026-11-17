namespace PaymentGateway.Api.Domain;

/// <summary>
/// An amount in minor units (cents) plus an ISO 4217 currency code.
/// Minor units avoid floating point rounding errors with money.
/// </summary>
public readonly record struct Money(long MinorUnits, string Currency)
{
    public static readonly IReadOnlySet<string> SupportedCurrencies =
        new HashSet<string>(StringComparer.Ordinal) { "EUR", "USD", "GBP" };

    public static bool IsValidCurrency(string? currency) =>
        currency is not null && SupportedCurrencies.Contains(currency);

    public override string ToString() => $"{MinorUnits / 100m:0.00} {Currency}";
}
