using PaymentGateway.Api.Domain;

namespace PaymentGateway.Tests;

public class PaymentTests
{
    private static readonly DateTimeOffset Now = Harness.Start;

    private static Payment NewPayment(long amount = 1000) =>
        new("pay_1", "order-1", new Money(amount, "EUR"), PaymentMethod.Card, Now);

    [Fact]
    public void New_payment_starts_pending()
    {
        var payment = NewPayment();

        Assert.Equal(PaymentStatus.Pending, payment.Status);
        Assert.False(payment.IsFinal);
        Assert.Equal(0, payment.RefundableMinorUnits);
    }

    [Fact]
    public void Partial_then_full_refund_updates_status()
    {
        var payment = NewPayment(1000);
        payment.MarkSucceeded("acmepay", "ch_1", Now);

        payment.AddRefund(new Refund("rf_1", 400, "re_1", Now));
        Assert.Equal(PaymentStatus.PartiallyRefunded, payment.Status);
        Assert.Equal(600, payment.RefundableMinorUnits);

        payment.AddRefund(new Refund("rf_2", 600, "re_2", Now));
        Assert.Equal(PaymentStatus.Refunded, payment.Status);
        Assert.Equal(0, payment.RefundableMinorUnits);
    }

    [Fact]
    public void Cannot_refund_more_than_was_paid()
    {
        var payment = NewPayment(1000);
        payment.MarkSucceeded("acmepay", "ch_1", Now);

        Assert.Throws<InvalidPaymentStateException>(() => payment.AddRefund(new Refund("rf_1", 1001, "re_1", Now)));
    }

    [Fact]
    public void Cannot_refund_a_declined_payment()
    {
        var payment = NewPayment();
        payment.MarkDeclined("acmepay", "insufficient_funds", Now);

        Assert.Throws<InvalidPaymentStateException>(() => payment.AddRefund(new Refund("rf_1", 1, "re_1", Now)));
    }

    [Fact]
    public void A_finished_payment_cannot_change_result()
    {
        var payment = NewPayment();
        payment.MarkSucceeded("acmepay", "ch_1", Now);

        Assert.Throws<InvalidPaymentStateException>(() => payment.MarkDeclined("acmepay", "late", Now));
        Assert.Throws<InvalidPaymentStateException>(() => payment.MarkFailed("late", Now));
    }

    [Fact]
    public void Every_change_increments_the_version()
    {
        var payment = NewPayment();
        var before = payment.Version;

        payment.RecordAttempt("acmepay", 1, "succeeded", Now);
        payment.MarkSucceeded("acmepay", "ch_1", Now);

        Assert.Equal(before + 2, payment.Version);
    }

    [Fact]
    public void Clone_is_independent_of_the_original()
    {
        var payment = NewPayment();
        var copy = payment.Clone();

        copy.MarkSucceeded("acmepay", "ch_1", Now);

        Assert.Equal(PaymentStatus.Pending, payment.Status);
        Assert.Equal(PaymentStatus.Succeeded, copy.Status);
    }
}
