using AgencyHarness.Infrastructure;

namespace AgencyHarness.Tests;

public class ManualClock(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;
    public override DateTimeOffset GetUtcNow() => Now;
    public void Advance(TimeSpan by) => Now += by;
}

public class BackoffTests
{
    [Theory]
    [InlineData(1, 1000)]
    [InlineData(3, 4000)]
    [InlineData(20, 60000)]   // capped
    public void Delay_is_within_full_jitter_window(int attempt, double maxMs)
    {
        var rng = new Random(42);
        for (var i = 0; i < 200; i++)
        {
            var d = Backoff.Compute(attempt, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1), rng);
            Assert.InRange(d.TotalMilliseconds, 0, maxMs);
        }
    }
}

public class CircuitBreakerTests
{
    [Fact]
    public void Opens_after_threshold_then_allows_single_probe_after_cooldown()
    {
        var clock = new ManualClock(DateTimeOffset.UnixEpoch);
        var cb = new CircuitBreaker(3, TimeSpan.FromSeconds(30), clock);

        for (var i = 0; i < 3; i++) { Assert.True(cb.TryAcquire()); cb.RecordFailure(); }
        Assert.Equal(CircuitState.Open, cb.State);
        Assert.False(cb.TryAcquire());

        clock.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal(CircuitState.HalfOpen, cb.State);
        Assert.True(cb.TryAcquire());    // the probe
        Assert.False(cb.TryAcquire());   // everyone else still fails fast

        cb.RecordSuccess();
        Assert.Equal(CircuitState.Closed, cb.State);
        Assert.True(cb.TryAcquire());
    }

    [Fact]
    public void Failed_probe_reopens_the_circuit()
    {
        var clock = new ManualClock(DateTimeOffset.UnixEpoch);
        var cb = new CircuitBreaker(1, TimeSpan.FromSeconds(10), clock);
        cb.RecordFailure();
        clock.Advance(TimeSpan.FromSeconds(11));
        Assert.True(cb.TryAcquire());
        cb.RecordFailure();
        Assert.Equal(CircuitState.Open, cb.State);
    }
}

public class SecurityTests
{
    [Fact]
    public void Webhook_signature_round_trips_and_detects_tampering()
    {
        var secret = Security.NewSecret();
        var sig = Security.SignWebhook(secret, 1700000000, "{\"a\":1}");
        Assert.True(Security.VerifyWebhook(secret, 1700000000, "{\"a\":1}", sig));
        Assert.False(Security.VerifyWebhook(secret, 1700000000, "{\"a\":2}", sig));
        Assert.False(Security.VerifyWebhook(secret, 1700000001, "{\"a\":1}", sig));
    }

    [Fact]
    public void Api_keys_are_unique_and_hash_deterministically()
    {
        var a = Security.NewApiKey();
        Assert.NotEqual(a, Security.NewApiKey());
        Assert.Equal(Security.HashApiKey(a), Security.HashApiKey(a));
        Assert.DoesNotContain(a, Security.HashApiKey(a));
    }
}
