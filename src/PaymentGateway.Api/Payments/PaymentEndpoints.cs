using PaymentGateway.Api.Domain;
using PaymentGateway.Api.Idempotency;

namespace PaymentGateway.Api.Payments;

public static class PaymentEndpoints
{
    public static IEndpointRouteBuilder MapPaymentEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/payments").WithTags("Payments");

        group.MapPost("/", async (CreatePaymentRequest request, HttpContext http, PaymentService payments, IdempotentRequest idempotent) =>
        {
            var errors = PaymentValidation.Validate(request);
            if (errors.Count > 0)
                return Results.ValidationProblem(errors);

            return await idempotent.ExecuteAsync(http, "POST /payments", request, async () =>
            {
                var payment = await payments.CreateAsync(request, http.RequestAborted);
                http.Response.Headers.Location = $"/payments/{payment.Id}";
                return (StatusCodes.Status201Created, PaymentResponse.From(payment));
            });
        });

        group.MapGet("/{id}", async (string id, PaymentService payments, CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(PaymentResponse.From(await payments.GetAsync(id, ct)));
            }
            catch (PaymentNotFoundException ex)
            {
                return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Payment not found", detail: ex.Message);
            }
        });

        group.MapPost("/{id}/refunds", async (string id, CreateRefundRequest request, HttpContext http, PaymentService payments, IdempotentRequest idempotent) =>
        {
            if (request.AmountMinorUnits <= 0)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["amountMinorUnits"] = ["Must be positive."] });

            try
            {
                return await idempotent.ExecuteAsync(http, $"POST /payments/{id}/refunds", request, async () =>
                {
                    var key = http.Request.Headers[IdempotentRequest.HeaderName].ToString();
                    var payment = await payments.RefundAsync(id, request.AmountMinorUnits, key, http.RequestAborted);
                    return (StatusCodes.Status200OK, PaymentResponse.From(payment));
                });
            }
            catch (PaymentNotFoundException ex)
            {
                return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Payment not found", detail: ex.Message);
            }
            catch (InvalidPaymentStateException ex)
            {
                return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Refund not allowed", detail: ex.Message);
            }
        });

        return app;
    }
}
