# Choosing the models your agents run on

The agents (strategist, copywriter, SEO, analyst, reviewer) can run on **any model with an
OpenAI-compatible chat API**:

| Kind | Examples |
|---|---|
| Local on your PC | Ollama, llama.cpp server, LM Studio, vLLM |
| Cloud | Gemini, OpenAI, OpenRouter, Groq, DeepSeek, Mistral, Together, Fireworks, Cerebras |
| OpenCode | The providers and models already in your OpenCode config |
| Claude | Set `ANTHROPIC_API_KEY` |

Each role has a **fallback chain**. If a model is down, rate-limited, too slow, or rejects the request,
the next model takes the job. A model that keeps failing is paused for a minute (circuit breaker). If
every model is down, the job waits on the queue and retries. Nothing is lost.

Small local models that can't call tools natively still work. Set `Tools=false` on that model and the
agent switches to a simple JSON action protocol. If a model writes its work as its final answer
instead of saving it, the harness saves that answer as the deliverable.

With no models configured, the harness falls back to a placeholder "scripted" runner, so you can try
the pipeline without any AI.

## Recipe 1: a local model first, Gemini as backup

Add to `harness.env`:
```
Harness__Models__Profiles__local__BaseUrl=http://<ollama-host>:11434/v1
Harness__Models__Profiles__local__Model=gemma4:e4b
Harness__Models__Profiles__gemini__BaseUrl=https://generativelanguage.googleapis.com/v1beta/openai
Harness__Models__Profiles__gemini__Model=gemini-3.8-flash
Harness__Models__Profiles__gemini__ApiKeyEnv=GEMINI_API_KEY
Harness__Models__Profiles__gemini__ReasoningEffort=low
Harness__Models__Routing__default__0=local
Harness__Models__Routing__default__1=gemini
```
From inside the container, `<ollama-host>` is your PC's address that the Podman machine can reach,
not `127.0.0.1` (see HOSTING.md).

## Recipe 2: use your OpenCode models

Mount your OpenCode config into the container (read-only) and point the harness at it:
```
podman run ... -v C:\Users\<you>\.config\opencode\opencode.jsonc:/config/opencode.jsonc:ro ...
Harness__Models__OpenCodeConfig=/config/opencode.jsonc
```
The harness reads both the current format (`providers` → `settings.baseURL`, `env`, `models`;
`"model": "provider/model"`) and the older one (`provider` → `options`). Every model becomes a profile
named `opencode:<provider>/<model>`, and OpenCode's default model is also available as `opencode`.

- **Keys:** `{env:NAME}` is honoured. The harness reads the variable at run time and never stores the key.
- **Built-in providers without settings:** these are filled in with their usual endpoints (Ollama,
  LM Studio, vLLM, llama.cpp, OpenAI, OpenRouter, Groq, Google, DeepSeek, Mistral, Together,
  Fireworks, Cerebras).
- **Anthropic models in OpenCode:** use the built-in `claude` route instead.

If you set no routing, the order is: OpenCode's default model, then your own profiles, then Claude.

## Different models per role

```
Harness__Models__Routing__strategist__0=claude
Harness__Models__Routing__strategist__1=gemini
Harness__Models__Routing__copywriter__0=local
Harness__Models__Routing__copywriter__1=gemini
```
Roles without their own chain use `default`.

## Settings per model

| Setting | Default | Meaning |
|---|---|---|
| `BaseUrl` | (required) | Including the version segment, e.g. `.../v1` |
| `ChatPath` | `/chat/completions` | Appended to `BaseUrl` |
| `Model` | (required) | The model name the server expects |
| `ApiKeyEnv` | (none) | Environment variable(s) holding the key, comma-separated |
| `Tools` | `true` | `false` for models without native tool calling |
| `MaxTokens` | 4096 | Output limit per step |
| `ReasoningEffort` | (model default) | `none`, `low`, `medium` or `high`, sent as `reasoning_effort`. Thinking models (Gemini 3.x flash) can spend minutes per step at their default; `low` is usually plenty for agency work |
| `TimeoutSeconds` | 240 | Local models on a small GPU may need longer |
| `InputPerMillion` / `OutputPerMillion` | 0 | USD per million tokens, for the usage ledger |

## Seeing what's in use

- **Top bar:** shows the agents' first model and how many backups it has. Hover or long-press for each model's health.
- **Job details:** show which model did each job.
- **`GET /v1/models`:** returns the chains, each model's endpoint, local/cloud, health, and any configuration notes.
