using PaymentGateway.Api.Concurrency;

namespace PaymentGateway.Tests;

public class KeyedLockTests
{
    [Fact]
    public async Task Same_key_runs_one_at_a_time()
    {
        var locks = new KeyedLock();
        var inside = 0;
        var maxInside = 0;

        await Task.WhenAll(Enumerable.Range(0, 20).Select(async _ =>
        {
            using (await locks.AcquireAsync("pay_1", CancellationToken.None))
            {
                var now = Interlocked.Increment(ref inside);
                maxInside = Math.Max(maxInside, now);
                await Task.Delay(5);
                Interlocked.Decrement(ref inside);
            }
        }));

        Assert.Equal(1, maxInside);
    }

    [Fact]
    public async Task Different_keys_do_not_block_each_other()
    {
        var locks = new KeyedLock();

        using var first = await locks.AcquireAsync("pay_1", CancellationToken.None);
        var second = locks.AcquireAsync("pay_2", CancellationToken.None);

        Assert.True(second.IsCompleted);
        (await second).Dispose();
    }
}
