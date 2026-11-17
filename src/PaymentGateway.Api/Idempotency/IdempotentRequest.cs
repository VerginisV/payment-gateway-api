using System.Text.Json;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Http.Json;

namespace PaymentGateway.Api.Idempotency;

/// <summary>
/// Runs an endpoint body at most once per Idempotency-Key and replays the saved response
/// for retries with the same key.
/// </summary>
public sealed class IdempotentRequest(IdempotencyStore store, IOptions<JsonOptions> jsonOptions)
{
    public const string HeaderName = "Idempotency-Key";
    public const string ReplayedHeaderName = "Idempotent-Replayed";

    private readonly JsonSerializerOptions _json = jsonOptions.Value.SerializerOptions;

    public async Task<IResult> ExecuteAsync<TRequest>(
        HttpContext http,
        string scope,
        TRequest request,
        Func<Task<(int StatusCode, object Body)>> handler)
    {
        var key = http.Request.Headers[HeaderName].ToString();
        if (string.IsNullOrWhiteSpace(key) || key.Length > 255)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Missing Idempotency-Key header",
                detail: "Send a unique Idempotency-Key header (max 255 characters), and reuse it when retrying the same request.");
        }

        var storeKey = $"{scope}:{key}";
        var requestHash = IdempotencyStore.Hash(scope, JsonSerializer.Serialize(request, _json));
        var begin = store.TryBegin(storeKey, requestHash);

        switch (begin.Status)
        {
            case IdempotencyStatus.Replay:
                http.Response.Headers[ReplayedHeaderName] = "true";
                return Results.Content(begin.Response!.Body, "application/json", statusCode: begin.Response.StatusCode);

            case IdempotencyStatus.InProgress:
                return Results.Problem(statusCode: StatusCodes.Status409Conflict,
                    title: "Request in progress",
                    detail: "A request with this Idempotency-Key is still being processed. Retry shortly.");

            case IdempotencyStatus.Mismatch:
                return Results.Problem(statusCode: StatusCodes.Status422UnprocessableEntity,
                    title: "Idempotency-Key reused",
                    detail: "This Idempotency-Key was already used with a different request body.");
        }

        try
        {
            var (statusCode, body) = await handler();
            var json = JsonSerializer.Serialize(body, body.GetType(), _json);
            store.Complete(storeKey, new StoredResponse(statusCode, json));
            return Results.Content(json, "application/json", statusCode: statusCode);
        }
        catch
        {
            store.Abandon(storeKey);
            throw;
        }
    }
}
