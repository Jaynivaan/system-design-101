# Study guide: System Design 101 → Agency Harness

Each row takes a topic from the [System Design 101 README](../../README.md), says what it teaches,
and points to the code in this harness where it is applied. Read the diagram first, then the code.

## Communication protocols

| SD101 topic | Lesson | Where it lives |
|---|---|---|
| REST API vs. GraphQL | Resource-oriented REST suits CRUD plus async jobs | `Api/Endpoints.cs`: `/v1/clients`, `/v1/projects/{id}/jobs` |
| What is a webhook? | Push events instead of making clients poll | `Workers/OutboxDispatcher.cs`, with HMAC signing in `Infrastructure/Security.cs` |
| How to improve API performance? | Pagination, caching, async processing, connection reuse | Keyset cursors (`ToPage`), `ClientBriefCache`, `202 Accepted` with workers, `IHttpClientFactory` |
| HTTP status codes | Use the precise code | 202 for queued work, 409 for state conflicts, 422 when an idempotency key is reused with a different body, 429 with Retry-After |
| What does API gateway do? | Auth, rate limiting, request IDs, routing in one layer | `Api/Auth.cs` middleware chain and `AddRateLimiter` in `Program.cs` |
| How do we design effective and safe APIs? | Versioning, idempotency, validation, pagination | `/v1` prefix, `Idempotency-Key` handling in `CreateJobAsync`, `ValidationProblem` |
| Load-balancing algorithms | Spread work across workers | Competing consumers pull from the queue (`JobQueue.TryClaimAsync`), so idle workers take the next job |

## Architecture patterns

| SD101 topic | Lesson | Where it lives |
|---|---|---|
| 18 key design patterns | Strategy, Registry, Template | `IAgentRunner` (strategy: Claude or scripted), `ToolRegistry`, `AgentRoster` (roles as configuration) |
| MVC & friends | Keep transport separate from domain logic | `Domain/` has no ASP.NET dependencies. `JobProcessor` is separate from `AgentWorker`, so tests can drive it directly |

## Database

| SD101 topic | Lesson | Where it lives |
|---|---|---|
| 8 data structures that power your databases | B-tree indexes serve specific queries | `HarnessDbContext.OnModelCreating`: one index per hot query, e.g. `(Status, Priority, NextAttemptAt)` for dequeue |
| How is an SQL statement executed? | Check what SQL the ORM generates | `ExecuteUpdateAsync` compiles to a single conditional `UPDATE … WHERE Id=@id AND Version=@v` |
| CAP theorem | On a single primary, choose consistency | The queue claim is a compare-and-swap, so a job can never be double-claimed. Webhooks are at-least-once and receivers dedupe |
| Visualizing a SQL query | Filter first, then sort, then limit | Dequeue: `WHERE … ORDER BY Priority, NextAttemptAt LIMIT 5` |

## Cache

| SD101 topic | Lesson | Where it lives |
|---|---|---|
| Top caching strategies | Cache-aside with TTL and explicit invalidation | `ClientBriefCache.GetAsync` / `InvalidateClientAsync`. Auth lookups are cached for 60s |
| Data is cached everywhere | The LLM has a cache too | The system prompt and tools are a stable prefix that the Claude API can prompt-cache. Keep dynamic data in the user turn |
| How can Redis be used? | Shared cache and rate-limit state across nodes | Production upgrade path: `IDistributedCache` (see README) |

## Microservice architecture

| SD101 topic | Lesson | Where it lives |
|---|---|---|
| Typical microservice architecture | Gateway, services, message broker, observability | API nodes and worker nodes are one binary split by `RunWorkers`. The DB-backed queue is the broker |
| Microservice best practices | Retries with backoff, circuit breakers, DLQ, idempotent consumers | `Infrastructure/Resilience.cs`, `JobProcessor.FailAsync`, `/v1/jobs/{id}/retry`, `SaveDeliverableTool` upsert |
| Why is Kafka fast? | Append-only log, consumer offsets | `/v1/events?after=<id>` is an offset-based read over the append-only outbox |
| (pattern) Transactional outbox | Never lose an event, never emit a phantom one | `Outbox.Add` + `SaveChangesAsync` in the same unit of work as the state change |

## Payment systems

| SD101 topic | Lesson | Where it lives |
|---|---|---|
| How to learn payment systems | Immutable ledger; derive balances; idempotency | `UsageRecord` append-only ledger, `/v1/usage` roll-up, `EnforceBudgetAsync` budget guardrail |

## DevOps / Cloud native

| SD101 topic | Lesson | Where it lives |
|---|---|---|
| What is cloud native? | Stateless processes, health probes, config from environment | `/health/live` and `/health/ready`. All state is in the DB. Every setting can come from env vars (`Harness__Worker__Concurrency=8`) |
| k8s / Docker | Liveness vs readiness, graceful shutdown | Readiness checks the DB and reports the circuit state. When a worker is cancelled, its lease simply expires and another worker takes the job |
| CI/CD pipeline | Tests that run without external services | The scripted runner plus SQLite let `dotnet test` cover the whole pipeline offline |

## Security

| SD101 topic | Lesson | Where it lives |
|---|---|---|
| How to store passwords safely | Store hashes, never raw secrets | `Tenant.ApiKeyHash`. The raw key is shown once at creation |
| Session, cookie, JWT, token, SSO, OAuth | Pick the right credential for machine-to-machine | Per-tenant API keys. Add OAuth2 client credentials when you need scopes |
| How does HTTPS work? / webhooks | Integrity and authenticity of pushed data | HMAC over the timestamp and body, constant-time compare, https-only webhook URLs |
| Top 4 authentication mechanisms | Separate the operator plane from the tenant plane | `/admin` uses `X-Admin-Key`. `/v1` uses tenant keys |
| (multi-tenancy) | Every query is scoped to the tenant | Every endpoint and tool filters on `TenantId`. Covered by the `Tenants_cannot_see_each_others_data` test |

## Real-world case studies

| Case study | Takeaway applied |
|---|---|
| Amazon Prime Video: serverless → monolith | Start as a modular monolith. One deployable with clear seams (`IAgentRunner`, `JobQueue`, `Outbox`) that can be split out later |
| Discord storing trillions of messages | Choose storage for the access pattern. Production moves to Postgres with `SKIP LOCKED` |
| Disney Hotstar emojis | Absorb bursts with a queue. Jobs are accepted instantly (202) and processed at worker speed |
| Stack Overflow design | Vertical scaling of a well-indexed relational DB goes a long way |

## Agent-specific patterns (beyond SD101)

- **The model is an unreliable dependency.** It gets the same timeouts, retries, circuit breaker and DLQ as any other upstream (`ClaudeAgentRunner.CallAsync`).
- **Least-privilege tools.** Each role is granted an explicit tool list, and the runner refuses tools outside it.
- **Bounded autonomy.** Each job has a turn cap and a per-job delegation cap, and each tenant has a monthly budget.
- **Idempotent side effects.** A retried run never duplicates deliverables or delegated tasks.
- **Fencing tokens.** A worker whose lease expired cannot overwrite the result of the worker that re-claimed the job.

## Exercises

1. Add a `reviewer` pass that automatically queues after every copywriter job (hint: in `JobProcessor` on success, or a tool).
2. Replace SQLite with Postgres and change `TryClaimAsync` to `FOR UPDATE SKIP LOCKED`.
3. Add an SSE endpoint `/v1/projects/{id}/stream` that tails the outbox for live dashboards.
4. Add per-role model routing (e.g., `claude-haiku-5-5` for a cheap classifier role) and compare cost per completed job.
