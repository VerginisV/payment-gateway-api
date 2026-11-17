using PaymentGateway.Api.Concurrency;
using PaymentGateway.Api.Domain;
using PaymentGateway.Api.Persistence;
using PaymentGateway.Api.Providers;
using PaymentGateway.Api.Resilience;

namespace PaymentGateway.Api.Payments;

public sealed class PaymentNotFoundException(string id) : Exception($"Payment {id} not found.");

public sealed class PaymentService(
    IPaymentRepository repository,
    ProviderRouter router,
    RetryPolicy retryPolicy,
    KeyedLock locks,
    TimeProvider time,
    ILogger<PaymentService> logger)
{
    /// <summary>
    /// Charges through the primary provider, retrying transient errors, and fails over to the
    /// next provider if it stays down. Declines are final and never routed elsewhere.
    /// </summary>
    public async Task<Payment> CreateAsync(CreatePaymentRequest request, CancellationToken cancellationToken)
    {
        var payment = new Payment(
            $"pay_{Guid.NewGuid():N}",
            request.MerchantReference,
            new Money(request.AmountMinorUnits, request.Currency),
            request.Method,
            time.GetUtcNow());

        await repository.AddAsync(payment, cancellationToken);
        var loadedVersion = payment.Version;

        using (await locks.AcquireAsync(payment.Id, cancellationToken))
        {
            var candidates = router.CandidatesFor(request.Method, request.Currency);

            foreach (var provider in candidates)
            {
                try
                {
                    var result = await retryPolicy.ExecuteAsync(
                        (attempt, ct) => provider.ChargeAsync(
                            new ChargeRequest(payment.Id, payment.Amount, payment.Method, IdempotencyKey: payment.Id), ct),
                        (attempt, ex) => payment.RecordAttempt(provider.Name, attempt, $"error: {ex.Message}", time.GetUtcNow()),
                        cancellationToken);

                    var attemptNumber = payment.Attempts.Count(a => a.Provider == provider.Name) + 1;
                    payment.RecordAttempt(provider.Name, attemptNumber, result.Outcome.ToString().ToLowerInvariant(), time.GetUtcNow());
                    Apply(payment, provider.Name, result);
                    break;
                }
                catch (ProviderTransientException)
                {
                    logger.LogWarning("Provider {Provider} unavailable for payment {PaymentId}; trying the next provider.",
                        provider.Name, payment.Id);
                }
            }

            if (payment.Status == PaymentStatus.Pending && payment.ProviderPaymentId is null)
                payment.MarkFailed(candidates.Count == 0 ? "no_provider_supports_payment" : "all_providers_unavailable", time.GetUtcNow());

            await repository.UpdateAsync(payment, loadedVersion, cancellationToken);
        }

        logger.LogInformation("Payment {PaymentId} for {Amount} finished as {Status} via {Provider}.",
            payment.Id, payment.Amount, payment.Status, payment.Provider ?? "none");
        return payment;
    }

    public async Task<Payment> GetAsync(string id, CancellationToken cancellationToken) =>
        await repository.GetAsync(id, cancellationToken) ?? throw new PaymentNotFoundException(id);

    public async Task<Payment> RefundAsync(string paymentId, long amountMinorUnits, string idempotencyKey, CancellationToken cancellationToken)
    {
        using (await locks.AcquireAsync(paymentId, cancellationToken))
        {
            var payment = await GetAsync(paymentId, cancellationToken);
            var loadedVersion = payment.Version;

            if (amountMinorUnits <= 0 || amountMinorUnits > payment.RefundableMinorUnits)
                throw new InvalidPaymentStateException(
                    $"Refund must be between 1 and {payment.RefundableMinorUnits} (payment is {payment.Status}).");

            var provider = router.Find(payment.Provider!)
                ?? throw new InvalidOperationException($"Provider {payment.Provider} is not configured.");

            var result = await retryPolicy.ExecuteAsync(
                (_, ct) => provider.RefundAsync(
                    new RefundRequest(payment.ProviderPaymentId!, amountMinorUnits, payment.Amount.Currency, idempotencyKey), ct),
                onAttemptFailed: null,
                cancellationToken);

            payment.AddRefund(new Refund($"rf_{Guid.NewGuid():N}", amountMinorUnits, result.ProviderRefundId, time.GetUtcNow()));
            await repository.UpdateAsync(payment, loadedVersion, cancellationToken);

            logger.LogInformation("Refunded {Amount} of payment {PaymentId}; status now {Status}.",
                amountMinorUnits, payment.Id, payment.Status);
            return payment;
        }
    }

    private void Apply(Payment payment, string provider, ChargeResult result)
    {
        var now = time.GetUtcNow();
        switch (result.Outcome)
        {
            case ChargeOutcome.Succeeded:
                payment.MarkSucceeded(provider, result.ProviderPaymentId!, now);
                break;
            case ChargeOutcome.Pending:
                payment.MarkPendingAtProvider(provider, result.ProviderPaymentId!, now);
                break;
            case ChargeOutcome.Declined:
                payment.MarkDeclined(provider, result.DeclineReason ?? "declined", now);
                break;
        }
    }
}
