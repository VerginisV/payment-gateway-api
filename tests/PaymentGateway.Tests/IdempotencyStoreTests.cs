using PaymentGateway.Api.Idempotency;

namespace PaymentGateway.Tests;

public class IdempotencyStoreTests
{
    private readonly ManualTimeProvider _time = new(Harness.Start);
    private readonly IdempotencyStore _store;

    public IdempotencyStoreTests() => _store = new IdempotencyStore(_time);

    [Fact]
    public void First_request_starts_and_a_retry_replays_the_saved_response()
    {
        Assert.Equal(IdempotencyStatus.Started, _store.TryBegin("k1", "hash-a").Status);
        _store.Complete("k1", new StoredResponse(201, """{"id":"pay_1"}"""));

        var retry = _store.TryBegin("k1", "hash-a");

        Assert.Equal(IdempotencyStatus.Replay, retry.Status);
        Assert.Equal(201, retry.Response!.StatusCode);
        Assert.Equal("""{"id":"pay_1"}""", retry.Response.Body);
    }

    [Fact]
    public void Same_key_with_a_different_body_is_a_mismatch()
    {
        _store.TryBegin("k1", "hash-a");

        Assert.Equal(IdempotencyStatus.Mismatch, _store.TryBegin("k1", "hash-b").Status);
    }

    [Fact]
    public void Same_key_while_the_first_request_runs_is_in_progress()
    {
        _store.TryBegin("k1", "hash-a");

        Assert.Equal(IdempotencyStatus.InProgress, _store.TryBegin("k1", "hash-a").Status);
    }

    [Fact]
    public void Abandoned_request_can_be_retried()
    {
        _store.TryBegin("k1", "hash-a");
        _store.Abandon("k1");

        Assert.Equal(IdempotencyStatus.Started, _store.TryBegin("k1", "hash-a").Status);
    }

    [Fact]
    public void Keys_expire_after_24_hours()
    {
        _store.TryBegin("k1", "hash-a");
        _store.Complete("k1", new StoredResponse(201, "{}"));

        _time.Advance(IdempotencyStore.Lifetime + TimeSpan.FromSeconds(1));

        Assert.Equal(IdempotencyStatus.Started, _store.TryBegin("k1", "hash-a").Status);
    }

    [Fact]
    public void Hash_depends_on_scope_and_body()
    {
        var a = IdempotencyStore.Hash("POST /payments", "{}");

        Assert.Equal(a, IdempotencyStore.Hash("POST /payments", "{}"));
        Assert.NotEqual(a, IdempotencyStore.Hash("POST /payments", "{\"x\":1}"));
        Assert.NotEqual(a, IdempotencyStore.Hash("POST /refunds", "{}"));
    }
}
