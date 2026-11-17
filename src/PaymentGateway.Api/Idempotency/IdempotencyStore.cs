using System.Security.Cryptography;
using System.Text;

namespace PaymentGateway.Api.Idempotency;

public sealed record StoredResponse(int StatusCode, string Body);

public enum IdempotencyStatus
{
    /// <summary>First time this key is seen: run the request.</summary>
    Started,
    /// <summary>Same key and same request finished before: return the saved response.</summary>
    Replay,
    /// <summary>Same key, still running in another request.</summary>
    InProgress,
    /// <summary>Same key reused with a different request body: a client bug.</summary>
    Mismatch
}

public sealed record IdempotencyBegin(IdempotencyStatus Status, StoredResponse? Response = null);

/// <summary>
/// Makes POST requests safe to retry. A client that times out can resend the same request
/// with the same Idempotency-Key header and gets the original response back, instead of
/// creating a second payment. Keys expire after 24 hours.
/// </summary>
public sealed class IdempotencyStore(TimeProvider time)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public IdempotencyBegin TryBegin(string key, string requestHash)
    {
        lock (_gate)
        {
            RemoveExpired();

            if (!_entries.TryGetValue(key, out var entry))
            {
                _entries[key] = new Entry(requestHash, time.GetUtcNow());
                return new IdempotencyBegin(IdempotencyStatus.Started);
            }

            if (entry.RequestHash != requestHash)
                return new IdempotencyBegin(IdempotencyStatus.Mismatch);

            return entry.Response is null
                ? new IdempotencyBegin(IdempotencyStatus.InProgress)
                : new IdempotencyBegin(IdempotencyStatus.Replay, entry.Response);
        }
    }

    public void Complete(string key, StoredResponse response)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var entry))
                entry.Response = response;
        }
    }

    /// <summary>The request crashed before producing a response: let the client try again.</summary>
    public void Abandon(string key)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var entry) && entry.Response is null)
                _entries.Remove(key);
        }
    }

    public static string Hash(string scope, string body)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{scope}\n{body}"));
        return Convert.ToHexString(bytes);
    }

    private void RemoveExpired()
    {
        var cutoff = time.GetUtcNow() - Lifetime;
        foreach (var key in _entries.Where(e => e.Value.CreatedAt < cutoff).Select(e => e.Key).ToList())
            _entries.Remove(key);
    }

    private sealed class Entry(string requestHash, DateTimeOffset createdAt)
    {
        public string RequestHash { get; } = requestHash;
        public DateTimeOffset CreatedAt { get; } = createdAt;
        public StoredResponse? Response { get; set; }
    }
}
