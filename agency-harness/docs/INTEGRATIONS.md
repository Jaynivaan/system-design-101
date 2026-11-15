# Connect the harness to OpenCode, Claude Code, Cursor and other apps

The harness is an **MCP server** at `/mcp`. MCP (Model Context Protocol) is the standard way AI apps
call external tools, so any MCP client can use the harness. From inside your coding agent you can
list clients, queue work for agents, check on jobs and pull the deliverables.

```
OpenCode ─┐
Claude Code ─┼── MCP over HTTP ──▶ /mcp ─┐
Cursor ───┘                          ├─▶ HarnessService ─▶ queue ─▶ agents
Your scripts ─── REST ──────▶ /v1 ───┘
```

Both front doors share one backend. MCP calls get the same API-key auth, per-tenant rate limit,
idempotency and audit events as REST calls.

## 1. Run the harness and get a key

```bash
cd agency-harness/src/AgencyHarness
ASPNETCORE_ENVIRONMENT=Development dotnet run --urls http://localhost:5077
curl -s -X POST http://localhost:5077/admin/tenants -H "X-Admin-Key: dev-admin-key-change-me" \
     -H 'Content-Type: application/json' -d '{"name":"My Agency"}'
export AGENCY_HARNESS_API_KEY=ak_...     # the apiKey from the response
```

The endpoint accepts the key as `X-Api-Key: <key>` or `Authorization: Bearer <key>`.

## 2. Connect your app

### OpenCode

Copy [`integrations/opencode.jsonc`](../integrations/opencode.jsonc) into your project as `opencode.jsonc`,
or merge it into your existing config:

```jsonc
{
  "$schema": "https://opencode.ai/config.json",
  "mcp": {
    "servers": {
      "agency-harness": {
        "type": "remote",
        "url": "http://localhost:5077/mcp",
        "oauth": false,
        "headers": { "X-Api-Key": "{env:AGENCY_HARNESS_API_KEY}" }
      }
    }
  }
}
```

Or add it from the command line:

```bash
opencode mcp add agency-harness --url http://localhost:5077/mcp --header 'X-Api-Key={env:AGENCY_HARNESS_API_KEY}'
```

`"oauth": false` stops OpenCode from trying an OAuth sign-in, because the harness uses API keys.
`{env:NAME}` is OpenCode's syntax for reading an environment variable. This format comes from
OpenCode's own docs (`services/site/src/docs/content/mcp-servers.mdx` in its repo, Nov 2026).

### Claude Code

```bash
claude mcp add --transport http agency-harness http://localhost:5077/mcp \
  --header "X-Api-Key: $AGENCY_HARNESS_API_KEY"
```

### Cursor

Copy [`integrations/cursor.mcp.json`](../integrations/cursor.mcp.json) to `.cursor/mcp.json` in your project.

### Other MCP clients

Point the client at the Streamable HTTP endpoint `http://<host>:5077/mcp` with an `X-Api-Key` or
`Authorization: Bearer` header. The tool names and schemas are discovered automatically.

### Apps that don't speak MCP (n8n, Zapier, scripts)

Use the REST API under `/v1` with the same key. See the API table in the [README](../README.md).

## 3. Try it

Ask your coding agent things like:

- *"List my agency clients."*
- *"Add a client Northwind Bikes, plain-spoken voice, audience commuters; first project Summer campaign."*
- *"Queue a strategist job on Summer campaign to plan the launch, then show me the deliverables when it's done."*
- *"Which jobs are dead-lettered, and why? Retry the ones that failed on a timeout."*

## Tools

| Tool | Kind | What it does |
|---|---|---|
| `list_roles` | read | Lists the agent roles and each role's tools |
| `list_clients` | read | Lists clients, projects and job counts by status (call this first for ids) |
| `create_client` | write | Creates a client with a brief and a first project |
| `update_client_brief` | write, idempotent | Replaces a brief and invalidates the cache |
| `queue_job` | write | Queues an agent job. Optional `idempotencyKey` makes retries safe |
| `get_job` | read | Shows status, attempts, last error, summary, tokens and cost |
| `list_jobs` | read | Lists recent jobs, optionally filtered by status (e.g. `DeadLettered`) |
| `get_deliverables` | read | Returns the Markdown deliverables for a project |
| `retry_job` | write | Puts a dead-lettered job back on the queue |
| `cancel_job` | destructive | Cancels a job that is queued or waiting to retry |
| `get_usage` | read | Shows this month's spend against the budget |

Read-only and destructive tools carry MCP annotations, so clients can ask you before running writes.

## Using it from anywhere (not just localhost)

To reach the harness from a cloud app or another machine, deploy it behind HTTPS (any .NET host or
container platform) and use `https://your-host/mcp` as the URL. The MCP transport is **stateless**,
so you can run several instances behind a load balancer with no sticky sessions. Keep the API key
in each app's secret or env settings, never in a committed config file.
