using PaymentGateway.Api.Domain;
using PaymentGateway.Api.Payments;
using PaymentGateway.Api.Providers;

namespace PaymentGateway.Tests;

public class PaymentServiceTests
{
    [Fact]
    public async Task Successful_charge_uses_the_primary_provider()
    {
        var harness = new Harness();

        var payment = await harness.PayAsync(2500);

        Assert.Equal(PaymentStatus.Succeeded, payment.Status);
        Assert.Equal("acmepay", payment.Provider);
        Assert.Single(payment.Attempts);
    }

    [Fact]
    public async Task Decline_is_final_and_not_sent_to_the_backup_provider()
    {
        var harness = new Harness();

        var payment = await harness.PayAsync(1013);

        Assert.Equal(PaymentStatus.Declined, payment.Status);
        Assert.Equal("insufficient_funds", payment.FailureReason);
        Assert.All(payment.Attempts, a => Assert.Equal("acmepay", a.Provider));
    }

    [Fact]
    public async Task Transient_errors_are_retried_on_the_same_provider()
    {
        var harness = new Harness();

        var payment = await harness.PayAsync(1042);

        Assert.Equal(PaymentStatus.Succeeded, payment.Status);
        Assert.Equal("acmepay", payment.Provider);
        Assert.Equal(3, payment.Attempts.Count);
        Assert.Equal(2, harness.Delay.Waits.Count);
    }

    [Fact]
    public async Task Fails_over_to_the_backup_when_the_primary_is_down()
    {
        var harness = new Harness();

        var payment = await harness.PayAsync(1099);

        Assert.Equal(PaymentStatus.Succeeded, payment.Status);
        Assert.Equal("novapay", payment.Provider);
        Assert.Equal(3, payment.Attempts.Count(a => a.Provider == "acmepay"));
    }

    [Fact]
    public async Task Fails_when_every_provider_is_down()
    {
        var harness = new Harness();

        var payment = await harness.PayAsync(1098);

        Assert.Equal(PaymentStatus.Failed, payment.Status);
        Assert.Equal("all_providers_unavailable", payment.FailureReason);
    }

    [Fact]
    public async Task Fails_when_no_provider_supports_the_method()
    {
        var harness = new Harness(new MockPaymentProvider("cardsonly", [PaymentMethod.Card], isPrimary: true));

        var payment = await harness.PayAsync(1000, PaymentMethod.Wallet);

        Assert.Equal(PaymentStatus.Failed, payment.Status);
        Assert.Equal("no_provider_supports_payment", payment.FailureReason);
    }

    [Fact]
    public async Task Pending_charge_keeps_the_provider_reference_for_the_webhook()
    {
        var harness = new Harness();

        var payment = await harness.PayAsync(5077);

        Assert.Equal(PaymentStatus.Pending, payment.Status);
        Assert.NotNull(payment.ProviderPaymentId);
    }

    [Fact]
    public async Task Saved_payment_matches_the_returned_one()
    {
        var harness = new Harness();
        var payment = await harness.PayAsync(2500);

        var loaded = await harness.Payments.GetAsync(payment.Id, CancellationToken.None);

        Assert.Equal(payment.Status, loaded.Status);
        Assert.Equal(payment.Version, loaded.Version);
    }

    [Fact]
    public async Task Refunds_up_to_the_paid_amount()
    {
        var harness = new Harness();
        var payment = await harness.PayAsync(1000);

        await harness.Payments.RefundAsync(payment.Id, 300, "r1", CancellationToken.None);
        var refunded = await harness.Payments.RefundAsync(payment.Id, 700, "r2", CancellationToken.None);

        Assert.Equal(PaymentStatus.Refunded, refunded.Status);
        await Assert.ThrowsAsync<InvalidPaymentStateException>(
            () => harness.Payments.RefundAsync(payment.Id, 1, "r3", CancellationToken.None));
    }

    [Fact]
    public async Task Concurrent_refunds_never_exceed_the_paid_amount()
    {
        var harness = new Harness();
        var payment = await harness.PayAsync(1000);

        var attempts = Enumerable.Range(0, 10).Select(async i =>
        {
            try
            {
                await harness.Payments.RefundAsync(payment.Id, 300, $"r{i}", CancellationToken.None);
                return true;
            }
            catch (InvalidPaymentStateException)
            {
                return false;
            }
        });
        var results = await Task.WhenAll(attempts);

        var final = await harness.Payments.GetAsync(payment.Id, CancellationToken.None);
        Assert.Equal(3, results.Count(r => r));
        Assert.Equal(900, final.RefundedMinorUnits);
    }

    [Fact]
    public async Task Unknown_payment_throws_not_found()
    {
        var harness = new Harness();

        await Assert.ThrowsAsync<PaymentNotFoundException>(() => harness.Payments.GetAsync("pay_missing", CancellationToken.None));
    }
}
