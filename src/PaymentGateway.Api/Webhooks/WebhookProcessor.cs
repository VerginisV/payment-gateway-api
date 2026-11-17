using System.Collections.Concurrent;
using PaymentGateway.Api.Concurrency;
using PaymentGateway.Api.Domain;
using PaymentGateway.Api.Persistence;

namespace PaymentGateway.Api.Webhooks;

public sealed record ProviderWebhookEvent(string EventId, string Type, string ProviderPaymentId, string? Reason);

public enum WebhookResult
{
    Processed,
    /// <summary>Already handled (providers send the same event more than once).</summary>
    Duplicate,
    /// <summary>Valid but no longer relevant, for example a late event for a finished payment.</summary>
    Ignored,
    UnknownPayment,
    UnknownEventType
}

/// <summary>
/// Applies provider events to payments. Providers deliver webhooks at least once and
/// sometimes out of order, so processing is idempotent and never moves a finished payment backwards.
/// </summary>
public sealed class WebhookProcessor(
    IPaymentRepository repository,
    KeyedLock locks,
    TimeProvider time,
    ILogger<WebhookProcessor> logger)
{
    private readonly ConcurrentDictionary<string, byte> _processedEvents = new(StringComparer.Ordinal);

    public async Task<WebhookResult> ProcessAsync(string provider, ProviderWebhookEvent evt, CancellationToken cancellationToken)
    {
        var eventKey = $"{provider}:{evt.EventId}";
        if (_processedEvents.ContainsKey(eventKey))
            return WebhookResult.Duplicate;

        if (evt.Type is not ("payment.succeeded" or "payment.failed"))
            return WebhookResult.UnknownEventType;

        var found = await repository.FindByProviderPaymentIdAsync(provider, evt.ProviderPaymentId, cancellationToken);
        if (found is null)
            return WebhookResult.UnknownPayment; // The provider will retry; the payment may not be saved yet.

        using (await locks.AcquireAsync(found.Id, cancellationToken))
        {
            if (!_processedEvents.TryAdd(eventKey, 0))
                return WebhookResult.Duplicate;

            try
            {
                var payment = await repository.GetAsync(found.Id, cancellationToken) ?? throw new InvalidOperationException();
                var loadedVersion = payment.Version;

                if (payment.Status != PaymentStatus.Pending)
                {
                    logger.LogInformation("Ignoring {EventType} for payment {PaymentId}: already {Status}.",
                        evt.Type, payment.Id, payment.Status);
                    return WebhookResult.Ignored;
                }

                if (evt.Type == "payment.succeeded")
                    payment.MarkSucceeded(provider, evt.ProviderPaymentId, time.GetUtcNow());
                else
                    payment.MarkDeclined(provider, evt.Reason ?? "declined_by_provider", time.GetUtcNow());

                await repository.UpdateAsync(payment, loadedVersion, cancellationToken);
                logger.LogInformation("Webhook {EventId} moved payment {PaymentId} to {Status}.", evt.EventId, payment.Id, payment.Status);
                return WebhookResult.Processed;
            }
            catch
            {
                // Forget the event, so the provider's retry gets processed.
                _processedEvents.TryRemove(eventKey, out _);
                throw;
            }
        }
    }
}
