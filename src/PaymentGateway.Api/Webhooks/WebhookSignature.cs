using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace PaymentGateway.Api.Webhooks;

/// <summary>
/// Signs and verifies webhook payloads with HMAC-SHA256, in the common
/// "t=&lt;unix time&gt;,v1=&lt;hex signature&gt;" header format.
/// The timestamp is part of the signed data, so an old captured request can't be replayed later.
/// </summary>
public static class WebhookSignature
{
    public const string HeaderName = "X-Signature";
    public static readonly TimeSpan DefaultTolerance = TimeSpan.FromMinutes(5);

    public static string Sign(string secret, string payload, DateTimeOffset timestamp)
    {
        var unix = timestamp.ToUnixTimeSeconds();
        return $"t={unix},v1={Compute(secret, unix, payload)}";
    }

    public static bool Verify(string secret, string? header, string payload, DateTimeOffset now, TimeSpan tolerance)
    {
        if (string.IsNullOrWhiteSpace(header))
            return false;

        long? unix = null;
        string? signature = null;
        foreach (var part in header.Split(',', StringSplitOptions.TrimEntries))
        {
            var kv = part.Split('=', 2);
            if (kv.Length != 2) continue;
            if (kv[0] == "t" && long.TryParse(kv[1], NumberStyles.None, CultureInfo.InvariantCulture, out var t)) unix = t;
            if (kv[0] == "v1") signature = kv[1];
        }

        if (unix is null || signature is null)
            return false;

        var age = now - DateTimeOffset.FromUnixTimeSeconds(unix.Value);
        if (age.Duration() > tolerance)
            return false;

        var expected = Encoding.ASCII.GetBytes(Compute(secret, unix.Value, payload));
        var actual = Encoding.ASCII.GetBytes(signature.ToLowerInvariant());

        // Constant-time comparison, so response timing reveals nothing about the correct signature.
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    private static string Compute(string secret, long unix, string payload)
    {
        var data = Encoding.UTF8.GetBytes($"{unix}.{payload}");
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), data);
        return Convert.ToHexStringLower(hash);
    }
}
