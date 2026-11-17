using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;
using PaymentGateway.Api.Providers;

namespace PaymentGateway.Api.Webhooks;

public sealed class ProviderSecretsOptions
{
    /// <summary>Webhook signing secret per provider name.</summary>
    public Dictionary<string, string> WebhookSecrets { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public static class WebhookEndpoints
{
    public static IEndpointRouteBuilder MapWebhookEndpoints(this IEndpointRouteBuilder app)
    {
        // Providers call this. It reads the raw body, because the signature covers the exact bytes sent.
        app.MapPost("/webhooks/{provider}", async (
            string provider,
            HttpRequest request,
            IOptions<ProviderSecretsOptions> secrets,
            IOptions<JsonOptions> json,
            WebhookProcessor processor,
            TimeProvider time,
            ILoggerFactory loggers) =>
        {
            var logger = loggers.CreateLogger("Webhooks");

            if (!secrets.Value.WebhookSecrets.TryGetValue(provider, out var secret))
                return Results.NotFound();

            using var reader = new StreamReader(request.Body, Encoding.UTF8);
            var body = await reader.ReadToEndAsync(request.HttpContext.RequestAborted);

            if (!WebhookSignature.Verify(secret, request.Headers[WebhookSignature.HeaderName], body, time.GetUtcNow(), WebhookSignature.DefaultTolerance))
            {
                logger.LogWarning("Rejected webhook from {Provider}: invalid or expired signature.", provider);
                return Results.Unauthorized();
            }

            ProviderWebhookEvent? evt;
            try
            {
                evt = JsonSerializer.Deserialize<ProviderWebhookEvent>(body, json.Value.SerializerOptions);
            }
            catch (JsonException)
            {
                return Results.BadRequest();
            }
            if (evt is null || string.IsNullOrEmpty(evt.EventId) || string.IsNullOrEmpty(evt.ProviderPaymentId))
                return Results.BadRequest();

            var result = await processor.ProcessAsync(provider.ToLowerInvariant(), evt, request.HttpContext.RequestAborted);

            // 2xx tells the provider to stop retrying; 404 makes it retry later.
            return result switch
            {
                WebhookResult.UnknownPayment => Results.NotFound(),
                _ => Results.Ok(new { result = result.ToString() })
            };
        }).WithTags("Webhooks");

        return app;
    }

    /// <summary>
    /// Development only: pretends to be the provider and sends a signed webhook to this API,
    /// so a pending payment (amounts ending in 77) can be completed by hand.
    /// </summary>
    public static IEndpointRouteBuilder MapDevWebhookSimulator(this IEndpointRouteBuilder app)
    {
        app.MapPost("/dev/simulate-webhook", async (
            SimulateWebhookRequest request,
            HttpContext http,
            IOptions<ProviderSecretsOptions> secrets,
            IOptions<JsonOptions> json,
            IHttpClientFactory clients,
            TimeProvider time) =>
        {
            if (!secrets.Value.WebhookSecrets.TryGetValue(request.Provider, out var secret))
                return Results.NotFound();

            var evt = new ProviderWebhookEvent($"evt_{Guid.NewGuid():N}", request.Type, request.ProviderPaymentId, request.Reason);
            var body = JsonSerializer.Serialize(evt, json.Value.SerializerOptions);

            using var message = new HttpRequestMessage(HttpMethod.Post, $"{http.Request.Scheme}://{http.Request.Host}/webhooks/{request.Provider}")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            message.Headers.Add(WebhookSignature.HeaderName, WebhookSignature.Sign(secret, body, time.GetUtcNow()));

            using var response = await clients.CreateClient().SendAsync(message, http.RequestAborted);
            return Results.Ok(new
            {
                sentEvent = evt,
                webhookStatusCode = (int)response.StatusCode,
                webhookResponse = await response.Content.ReadAsStringAsync(http.RequestAborted)
            });
        }).WithTags("Development");

        return app;
    }
}

public sealed record SimulateWebhookRequest(string Provider, string ProviderPaymentId, string Type = "payment.succeeded", string? Reason = null);
