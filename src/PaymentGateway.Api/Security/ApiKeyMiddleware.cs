using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace PaymentGateway.Api.Security;

public sealed class ApiKeyOptions
{
    public List<string> Keys { get; set; } = [];
}

/// <summary>
/// Merchants authenticate with an X-Api-Key header on /payments.
/// Webhooks use signatures instead, and /health stays public for load balancers.
/// </summary>
public sealed class ApiKeyMiddleware(RequestDelegate next, IOptions<ApiKeyOptions> options)
{
    public const string HeaderName = "X-Api-Key";

    private readonly byte[][] _keys = [.. options.Value.Keys.Where(k => !string.IsNullOrEmpty(k)).Select(Encoding.UTF8.GetBytes)];

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Path.StartsWithSegments("/payments") && !IsAuthorized(context.Request.Headers[HeaderName].ToString()))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { title = "Missing or invalid X-Api-Key header", status = 401 });
            return;
        }

        await next(context);
    }

    private bool IsAuthorized(string provided)
    {
        if (string.IsNullOrEmpty(provided))
            return false;
        var bytes = Encoding.UTF8.GetBytes(provided);
        return _keys.Any(k => CryptographicOperations.FixedTimeEquals(k, bytes));
    }
}
