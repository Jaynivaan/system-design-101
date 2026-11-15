using System.Diagnostics.Metrics;

namespace AgencyHarness.Infrastructure;

/// <summary>
/// Metrics via System.Diagnostics.Metrics, which OpenTelemetry/Prometheus exporters
/// pick up with no code changes (SD101: observability in the microservice stack).
/// Inspect live with: dotnet-counters monitor -n AgencyHarness --counters AgencyHarness
/// </summary>
public static class Telemetry
{
    public const string MeterName = "AgencyHarness";
    private static readonly Meter Meter = new(MeterName);

    public static readonly Counter<long> JobsCompleted = Meter.CreateCounter<long>("jobs.completed");
    public static readonly Counter<long> JobsFailed = Meter.CreateCounter<long>("jobs.failed");
    public static readonly Counter<long> JobsDeadLettered = Meter.CreateCounter<long>("jobs.dead_lettered");
    public static readonly Histogram<double> JobDurationMs = Meter.CreateHistogram<double>("jobs.duration", "ms");
    public static readonly Counter<long> TokensUsed = Meter.CreateCounter<long>("llm.tokens");
    public static readonly Counter<long> WebhooksDelivered = Meter.CreateCounter<long>("webhooks.delivered");
    public static readonly Counter<long> CacheHits = Meter.CreateCounter<long>("cache.hits");
    public static readonly Counter<long> CacheMisses = Meter.CreateCounter<long>("cache.misses");
}
