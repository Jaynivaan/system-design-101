namespace AgencyHarness.Domain;

// ---------------------------------------------------------------------------
// Multi-tenant model. Every row carries TenantId so one database can safely
// serve many agencies (SD101: "Security" + "Microservice best practices":
// isolate data per tenant, never trust the caller to scope queries).
// ---------------------------------------------------------------------------

/// <summary>An agency using the harness. Authenticates with an API key.</summary>
public class Tenant
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }

    /// <summary>SHA-256 hash of the API key. The raw key is shown once and never stored
    /// (SD101: "How to store passwords safely in the database").</summary>
    public required string ApiKeyHash { get; set; }

    /// <summary>Requests per minute for the token-bucket rate limiter.</summary>
    public int RateLimitPerMinute { get; set; } = 120;

    /// <summary>Monthly spend ceiling in USD for agent work (budget guardrail).</summary>
    public decimal MonthlyBudgetUsd { get; set; } = 100m;

    /// <summary>Optional URL that receives signed webhook events.</summary>
    public string? WebhookUrl { get; set; }
    public required string WebhookSecret { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>A customer of the agency (the agency's client).</summary>
public class Client
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public required string Name { get; set; }

    /// <summary>Brand voice, audience, goals: the context every agent gets.</summary>
    public string Brief { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class Project
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid ClientId { get; set; }
    public required string Name { get; set; }
    public string Goal { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public enum JobStatus
{
    Queued,
    Running,
    Succeeded,
    Failed,        // transient failure, will be retried after NextAttemptAt
    DeadLettered,  // exhausted retries or non-retryable error: needs a human
    Cancelled,
}

/// <summary>
/// A unit of agent work. Doubles as the durable queue message: workers claim
/// jobs with a lease (visibility timeout), exactly like SQS/Kafka consumer
/// semantics (SD101: "Why is Kafka fast", message queues in microservices).
/// </summary>
public class AgentJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid ProjectId { get; set; }

    /// <summary>Which specialist runs this job (e.g. "copywriter").</summary>
    public required string AgentRole { get; set; }
    public required string Instructions { get; set; }

    /// <summary>Lower number = higher priority.</summary>
    public int Priority { get; set; } = 5;

    public JobStatus Status { get; set; } = JobStatus.Queued;
    public int Attempts { get; set; }
    public int MaxAttempts { get; set; } = 4;
    public DateTimeOffset NextAttemptAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Worker that holds the lease, and when the lease expires.
    /// If a worker crashes, the lease lapses and another worker re-claims.</summary>
    public string? LeasedBy { get; set; }
    public DateTimeOffset? LeaseExpiresAt { get; set; }

    /// <summary>Client-supplied Idempotency-Key: retrying a POST never creates duplicates.</summary>
    public string? IdempotencyKey { get; set; }

    /// <summary>Optional parent job, so a strategist can fan work out to specialists.</summary>
    public Guid? ParentJobId { get; set; }

    public string? Output { get; set; }
    public string? LastError { get; set; }

    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public decimal CostUsd { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>Optimistic concurrency token.</summary>
    public Guid Version { get; set; } = Guid.NewGuid();
}

/// <summary>Durable, append-only record of what agents produced.</summary>
public class Deliverable
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid ProjectId { get; set; }
    public Guid JobId { get; set; }
    public required string Title { get; set; }
    public required string Content { get; set; }
    public string Kind { get; set; } = "text";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Transactional outbox (SD101: microservices / webhooks). Events are written in
/// the same DB transaction as the state change, then delivered asynchronously,
/// so we never lose an event or emit one for a change that rolled back.
/// </summary>
public class OutboxMessage
{
    public long Id { get; set; }
    public Guid TenantId { get; set; }
    public required string EventType { get; set; }
    public required string Payload { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? DeliveredAt { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Append-only usage ledger for metering/billing
/// (SD101: payment systems: keep an immutable ledger, derive balances from it).</summary>
public class UsageRecord
{
    public long Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid JobId { get; set; }
    public required string Model { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public decimal CostUsd { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Stored response for an Idempotency-Key (SD101: designing safe APIs).</summary>
public class IdempotencyRecord
{
    public Guid TenantId { get; set; }
    public required string Key { get; set; }
    public required string RequestHash { get; set; }
    public Guid ResourceId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
