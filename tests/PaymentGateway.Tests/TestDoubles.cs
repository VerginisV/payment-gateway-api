using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PaymentGateway.Api.Concurrency;
using PaymentGateway.Api.Domain;
using PaymentGateway.Api.Payments;
using PaymentGateway.Api.Persistence;
using PaymentGateway.Api.Providers;
using PaymentGateway.Api.Resilience;
using PaymentGateway.Api.Webhooks;

namespace PaymentGateway.Tests;

public sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; private set; } = start;

    public override DateTimeOffset GetUtcNow() => Now;

    public void Advance(TimeSpan by) => Now += by;
}

public sealed class NoRetryDelay : IRetryDelay
{
    public List<TimeSpan> Waits { get; } = [];

    public Task WaitAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        Waits.Add(delay);
        return Task.CompletedTask;
    }
}

/// <summary>Builds the real services wired together, with instant retries and a fixed clock.</summary>
public sealed class Harness
{
    public static readonly DateTimeOffset Start = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    public ManualTimeProvider Time { get; } = new(Start);
    public NoRetryDelay Delay { get; } = new();
    public InMemoryPaymentRepository Repository { get; } = new();
    public KeyedLock Locks { get; } = new();
    public PaymentService Payments { get; }
    public WebhookProcessor Webhooks { get; }

    public Harness(params IPaymentProvider[] providers)
    {
        if (providers.Length == 0)
        {
            providers =
            [
                new MockPaymentProvider("acmepay", [PaymentMethod.Card], isPrimary: true),
                new MockPaymentProvider("novapay", [PaymentMethod.Card, PaymentMethod.Wallet], isPrimary: false)
            ];
        }

        var retry = CreateRetryPolicy(Delay);
        Payments = new PaymentService(Repository, new ProviderRouter(providers), retry, Locks, Time, NullLogger<PaymentService>.Instance);
        Webhooks = new WebhookProcessor(Repository, Locks, Time, NullLogger<WebhookProcessor>.Instance);
    }

    public static RetryPolicy CreateRetryPolicy(IRetryDelay delay, int maxAttempts = 3) =>
        new(Options.Create(new RetryOptions { MaxAttempts = maxAttempts, BaseDelayMilliseconds = 100 }), delay, NullLogger<RetryPolicy>.Instance);

    public Task<Payment> PayAsync(long amount, PaymentMethod method = PaymentMethod.Card, string currency = "EUR") =>
        Payments.CreateAsync(new CreatePaymentRequest(amount, currency, method, "order-1"), CancellationToken.None);
}
