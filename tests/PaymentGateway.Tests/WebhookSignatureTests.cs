using PaymentGateway.Api.Webhooks;

namespace PaymentGateway.Tests;

public class WebhookSignatureTests
{
    private const string Secret = "whsec_test";
    private const string Body = """{"eventId":"evt_1","type":"payment.succeeded","providerPaymentId":"ch_1"}""";
    private static readonly DateTimeOffset Now = Harness.Start;
    private static readonly TimeSpan Tolerance = WebhookSignature.DefaultTolerance;

    [Fact]
    public void Accepts_a_correctly_signed_payload()
    {
        var header = WebhookSignature.Sign(Secret, Body, Now);

        Assert.True(WebhookSignature.Verify(Secret, header, Body, Now, Tolerance));
    }

    [Fact]
    public void Rejects_a_changed_body()
    {
        var header = WebhookSignature.Sign(Secret, Body, Now);

        Assert.False(WebhookSignature.Verify(Secret, header, Body.Replace("succeeded", "failed"), Now, Tolerance));
    }

    [Fact]
    public void Rejects_a_signature_made_with_another_secret()
    {
        var header = WebhookSignature.Sign("whsec_attacker", Body, Now);

        Assert.False(WebhookSignature.Verify(Secret, header, Body, Now, Tolerance));
    }

    [Fact]
    public void Rejects_an_old_replayed_request()
    {
        var header = WebhookSignature.Sign(Secret, Body, Now);

        Assert.False(WebhookSignature.Verify(Secret, header, Body, Now.AddMinutes(6), Tolerance));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("t=abc,v1=00")]
    [InlineData("v1=deadbeef")]
    public void Rejects_malformed_headers(string? header)
    {
        Assert.False(WebhookSignature.Verify(Secret, header, Body, Now, Tolerance));
    }
}
