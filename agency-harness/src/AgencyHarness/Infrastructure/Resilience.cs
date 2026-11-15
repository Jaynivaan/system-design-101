namespace AgencyHarness.Infrastructure;

/// <summary>
/// Exponential backoff with full jitter (SD101: "How to improve API performance",
/// microservice best practices). Jitter prevents a thundering herd of retries
/// when an upstream (the LLM API) recovers.
/// </summary>
public static class Backoff
{
    public static TimeSpan Compute(int attempt, TimeSpan baseDelay, TimeSpan maxDelay, Random? rng = null)
    {
        rng ??= Random.Shared;
        var exp = baseDelay.TotalMilliseconds * Math.Pow(2, Math.Max(0, attempt - 1));
        var capped = Math.Min(exp, maxDelay.TotalMilliseconds);
        return TimeSpan.FromMilliseconds(rng.NextDouble() * capped);
    }
}

public enum CircuitState { Closed, Open, HalfOpen }

/// <summary>
/// Circuit breaker around the model provider. After N consecutive failures the
/// circuit opens and calls fail fast (jobs are rescheduled instead of hammering a
/// struggling dependency). After a cool-down, one probe call is allowed through.
/// </summary>
public class CircuitBreaker(int failureThreshold, TimeSpan openDuration, TimeProvider clock)
{
    private readonly object _gate = new();
    private int _failures;
    private DateTimeOffset _openedAt;
    private bool _probeInFlight;

    public CircuitState State
    {
        get
        {
            lock (_gate) return CurrentState();
        }
    }

    private CircuitState CurrentState()
    {
        if (_failures < failureThreshold) return CircuitState.Closed;
        return clock.GetUtcNow() - _openedAt >= openDuration ? CircuitState.HalfOpen : CircuitState.Open;
    }

    /// <summary>Returns false if the caller must not call the dependency right now.</summary>
    public bool TryAcquire()
    {
        lock (_gate)
        {
            switch (CurrentState())
            {
                case CircuitState.Closed: return true;
                case CircuitState.Open: return false;
                default:
                    if (_probeInFlight) return false;
                    _probeInFlight = true;
                    return true;
            }
        }
    }

    public void RecordSuccess()
    {
        lock (_gate) { _failures = 0; _probeInFlight = false; }
    }

    public void RecordFailure()
    {
        lock (_gate)
        {
            _failures++;
            _probeInFlight = false;
            if (_failures >= failureThreshold) _openedAt = clock.GetUtcNow();
        }
    }
}

public class CircuitOpenException() : Exception("Model provider circuit is open; failing fast.");
