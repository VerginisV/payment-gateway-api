using System.Text.Json.Serialization;
using PaymentGateway.Api.Concurrency;
using PaymentGateway.Api.Domain;
using PaymentGateway.Api.Idempotency;
using PaymentGateway.Api.Payments;
using PaymentGateway.Api.Persistence;
using PaymentGateway.Api.Providers;
using PaymentGateway.Api.Resilience;
using PaymentGateway.Api.Security;
using PaymentGateway.Api.Webhooks;

namespace PaymentGateway.Api;

public static class PaymentGatewayApp
{
    /// <param name="configure">Lets tests replace services or settings before the app is built.</param>
    public static WebApplication Create(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.Services.ConfigureHttpJsonOptions(o =>
            o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        builder.Services.AddProblemDetails();
        builder.Services.AddHealthChecks();
        builder.Services.AddHttpClient();

        builder.Services.Configure<RetryOptions>(builder.Configuration.GetSection("Retry"));
        builder.Services.Configure<ApiKeyOptions>(builder.Configuration.GetSection("ApiKeys"));
        builder.Services.Configure<ProviderSecretsOptions>(builder.Configuration.GetSection("Providers"));

        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<IRetryDelay, TaskRetryDelay>();
        builder.Services.AddSingleton<RetryPolicy>();
        builder.Services.AddSingleton<KeyedLock>();
        builder.Services.AddSingleton<IPaymentRepository, InMemoryPaymentRepository>();
        builder.Services.AddSingleton<IdempotencyStore>();
        builder.Services.AddSingleton<IdempotentRequest>();
        builder.Services.AddSingleton<PaymentService>();
        builder.Services.AddSingleton<WebhookProcessor>();

        // Registration order is routing priority: the first provider that supports a payment is primary.
        builder.Services.AddSingleton<IPaymentProvider>(new MockPaymentProvider("acmepay", [PaymentMethod.Card], isPrimary: true));
        builder.Services.AddSingleton<IPaymentProvider>(new MockPaymentProvider("novapay", [PaymentMethod.Card, PaymentMethod.Wallet], isPrimary: false));
        builder.Services.AddSingleton<ProviderRouter>();

        configure?.Invoke(builder);

        var app = builder.Build();

        app.UseExceptionHandler();
        app.UseStatusCodePages();
        app.UseMiddleware<ApiKeyMiddleware>();

        app.MapHealthChecks("/health");
        app.MapPaymentEndpoints();
        app.MapWebhookEndpoints();

        if (app.Environment.IsDevelopment())
            app.MapDevWebhookSimulator();

        return app;
    }
}
