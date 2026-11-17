using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using PaymentGateway.Api;
using PaymentGateway.Api.Resilience;
using PaymentGateway.Api.Webhooks;

namespace PaymentGateway.Tests;

/// <summary>Starts the real app on a free local port and talks to it over HTTP.</summary>
public sealed class TestApi : IAsyncDisposable
{
    public const string ApiKey = "test_key";
    public const string AcmeSecret = "whsec_test_acmepay";

    private readonly WebApplication _app;

    private TestApi(WebApplication app, HttpClient client)
    {
        _app = app;
        Client = client;
    }

    public HttpClient Client { get; }

    public static async Task<TestApi> StartAsync()
    {
        var app = PaymentGatewayApp.Create(
        [
            "--urls=http://127.0.0.1:0",
            "--environment=Testing",
            $"--ApiKeys:Keys:0={ApiKey}",
            $"--Providers:WebhookSecrets:acmepay={AcmeSecret}",
            "--Logging:LogLevel:Default=Warning"
        ], builder => builder.Services.AddSingleton<IRetryDelay, NoRetryDelay>());

        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        var client = new HttpClient { BaseAddress = new Uri(address) };
        client.DefaultRequestHeaders.Add("X-Api-Key", ApiKey);
        return new TestApi(app, client);
    }

    public Task<HttpResponseMessage> PostPaymentAsync(long amount, string idempotencyKey, string currency = "EUR")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/payments")
        {
            Content = JsonContent.Create(new { amountMinorUnits = amount, currency, method = "Card", merchantReference = "order-1" })
        };
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        return Client.SendAsync(request);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

public class ApiTests
{
    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    [Fact]
    public async Task Health_endpoint_is_public()
    {
        await using var api = await TestApi.StartAsync();
        using var anonymous = new HttpClient { BaseAddress = api.Client.BaseAddress };

        var response = await anonymous.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Payments_require_an_api_key()
    {
        await using var api = await TestApi.StartAsync();
        using var anonymous = new HttpClient { BaseAddress = api.Client.BaseAddress };

        var response = await anonymous.GetAsync("/payments/pay_1");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Create_then_get_a_payment()
    {
        await using var api = await TestApi.StartAsync();

        var created = await api.PostPaymentAsync(2500, "key-1");
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await JsonAsync(created)).GetProperty("id").GetString();

        var fetched = await api.Client.GetAsync($"/payments/{id}");
        var body = await JsonAsync(fetched);

        Assert.Equal(HttpStatusCode.OK, fetched.StatusCode);
        Assert.Equal("Succeeded", body.GetProperty("status").GetString());
        Assert.Equal(2500, body.GetProperty("amountMinorUnits").GetInt64());
    }

    [Fact]
    public async Task Retrying_with_the_same_key_returns_the_same_payment()
    {
        await using var api = await TestApi.StartAsync();

        var first = await JsonAsync(await api.PostPaymentAsync(2500, "key-1"));
        var retry = await api.PostPaymentAsync(2500, "key-1");

        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        Assert.True(retry.Headers.Contains("Idempotent-Replayed"));
        Assert.Equal(first.GetProperty("id").GetString(), (await JsonAsync(retry)).GetProperty("id").GetString());
    }

    [Fact]
    public async Task Reusing_a_key_with_another_body_is_rejected()
    {
        await using var api = await TestApi.StartAsync();

        await api.PostPaymentAsync(2500, "key-1");
        var reused = await api.PostPaymentAsync(9900, "key-1");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, reused.StatusCode);
    }

    [Fact]
    public async Task Missing_idempotency_key_is_rejected()
    {
        await using var api = await TestApi.StartAsync();

        var response = await api.Client.PostAsJsonAsync("/payments",
            new { amountMinorUnits = 2500, currency = "EUR", method = "Card", merchantReference = "order-1" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Invalid_request_returns_validation_errors()
    {
        await using var api = await TestApi.StartAsync();

        var response = await api.PostPaymentAsync(-1, "key-1", currency: "XYZ");
        var body = await JsonAsync(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(body.GetProperty("errors").TryGetProperty("amountMinorUnits", out _));
        Assert.True(body.GetProperty("errors").TryGetProperty("currency", out _));
    }

    [Fact]
    public async Task Signed_webhook_completes_a_pending_payment()
    {
        await using var api = await TestApi.StartAsync();
        var payment = await JsonAsync(await api.PostPaymentAsync(5077, "key-1"));
        var providerPaymentId = payment.GetProperty("providerPaymentId").GetString();

        var body = JsonSerializer.Serialize(new { eventId = "evt_1", type = "payment.succeeded", providerPaymentId });
        var webhook = new HttpRequestMessage(HttpMethod.Post, "/webhooks/acmepay")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        webhook.Headers.Add(WebhookSignature.HeaderName, WebhookSignature.Sign(TestApi.AcmeSecret, body, DateTimeOffset.UtcNow));

        var response = await api.Client.SendAsync(webhook);
        var fetched = await JsonAsync(await api.Client.GetAsync($"/payments/{payment.GetProperty("id").GetString()}"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Succeeded", fetched.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Webhook_with_a_bad_signature_is_rejected()
    {
        await using var api = await TestApi.StartAsync();

        var webhook = new HttpRequestMessage(HttpMethod.Post, "/webhooks/acmepay")
        {
            Content = new StringContent("""{"eventId":"evt_1","type":"payment.succeeded","providerPaymentId":"ch_1"}""", Encoding.UTF8, "application/json")
        };
        webhook.Headers.Add(WebhookSignature.HeaderName, "t=1700000000,v1=0000");

        var response = await api.Client.SendAsync(webhook);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Refunding_more_than_paid_returns_conflict()
    {
        await using var api = await TestApi.StartAsync();
        var payment = await JsonAsync(await api.PostPaymentAsync(1000, "key-1"));

        var refund = new HttpRequestMessage(HttpMethod.Post, $"/payments/{payment.GetProperty("id").GetString()}/refunds")
        {
            Content = JsonContent.Create(new { amountMinorUnits = 1500 })
        };
        refund.Headers.Add("Idempotency-Key", "refund-1");

        var response = await api.Client.SendAsync(refund);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }
}
