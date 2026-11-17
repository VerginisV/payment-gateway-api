using PaymentGateway.Api.Domain;
using PaymentGateway.Api.Webhooks;

namespace PaymentGateway.Tests;

public class WebhookProcessorTests
{
    private static ProviderWebhookEvent Event(string providerPaymentId, string type = "payment.succeeded", string id = "evt_1") =>
        new(id, type, providerPaymentId, null);

    [Fact]
    public async Task Success_event_completes_a_pending_payment()
    {
        var harness = new Harness();
        var payment = await harness.PayAsync(5077);

        var result = await harness.Webhooks.ProcessAsync("acmepay", Event(payment.ProviderPaymentId!), CancellationToken.None);

        Assert.Equal(WebhookResult.Processed, result);
        Assert.Equal(PaymentStatus.Succeeded, (await harness.Payments.GetAsync(payment.Id, CancellationToken.None)).Status);
    }

    [Fact]
    public async Task Failure_event_declines_a_pending_payment()
    {
        var harness = new Harness();
        var payment = await harness.PayAsync(5077);

        await harness.Webhooks.ProcessAsync("acmepay", Event(payment.ProviderPaymentId!, "payment.failed"), CancellationToken.None);

        Assert.Equal(PaymentStatus.Declined, (await harness.Payments.GetAsync(payment.Id, CancellationToken.None)).Status);
    }

    [Fact]
    public async Task The_same_event_delivered_twice_is_processed_once()
    {
        var harness = new Harness();
        var payment = await harness.PayAsync(5077);
        var evt = Event(payment.ProviderPaymentId!);

        var first = await harness.Webhooks.ProcessAsync("acmepay", evt, CancellationToken.None);
        var second = await harness.Webhooks.ProcessAsync("acmepay", evt, CancellationToken.None);

        Assert.Equal(WebhookResult.Processed, first);
        Assert.Equal(WebhookResult.Duplicate, second);
    }

    [Fact]
    public async Task A_late_event_does_not_change_a_finished_payment()
    {
        var harness = new Harness();
        var payment = await harness.PayAsync(5077);

        await harness.Webhooks.ProcessAsync("acmepay", Event(payment.ProviderPaymentId!, "payment.succeeded", "evt_1"), CancellationToken.None);
        var late = await harness.Webhooks.ProcessAsync("acmepay", Event(payment.ProviderPaymentId!, "payment.failed", "evt_2"), CancellationToken.None);

        Assert.Equal(WebhookResult.Ignored, late);
        Assert.Equal(PaymentStatus.Succeeded, (await harness.Payments.GetAsync(payment.Id, CancellationToken.None)).Status);
    }

    [Fact]
    public async Task Unknown_payment_is_reported_so_the_provider_retries()
    {
        var harness = new Harness();

        var result = await harness.Webhooks.ProcessAsync("acmepay", Event("ch_unknown"), CancellationToken.None);

        Assert.Equal(WebhookResult.UnknownPayment, result);
    }

    [Fact]
    public async Task Unknown_event_types_are_ignored_safely()
    {
        var harness = new Harness();
        var payment = await harness.PayAsync(5077);

        var result = await harness.Webhooks.ProcessAsync("acmepay", Event(payment.ProviderPaymentId!, "payment.disputed"), CancellationToken.None);

        Assert.Equal(WebhookResult.UnknownEventType, result);
        Assert.Equal(PaymentStatus.Pending, (await harness.Payments.GetAsync(payment.Id, CancellationToken.None)).Status);
    }
}
