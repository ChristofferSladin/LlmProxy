# LlmProxy

A local reverse proxy that makes NVIDIA NIM's free-tier cloud models look like a local OpenAI
server — so the **LM Studio GUI** can talk to them, retries and failover happen automatically,
and every knob (model selection, system prompt, retry policy) is owned by the proxy instead of
scattered across a client UI.

## Why this exists

I use Claude day to day, but there are two situations where I still want a model on tap:

- **I've run out of tokens** on a paid plan, or a task is small enough that burning premium
  tokens on it doesn't make sense.
- **My machine can't reliably host a good local model.** I like LM Studio's GUI and its tooling
  — model management, chat UI, MCP/tool support — and I've used it for local inference on
  Apple Silicon. But a laptop-class machine hits real ceilings on model quality and speed.

The gap: **LM Studio is built to be a local-model server, not a client for cloud models.**
There's no built-in way to point its chat GUI at a remote OpenAI-compatible endpoint — the
closest thing is a community plugin that forwards requests to *a* configurable base URL, but it
gives you exactly one text field for the model name and nothing else: no failover, no retries,
no way to keep a system prompt out of every chat's per-conversation settings.

So the proxy exists to close that gap and then go further: it fronts **NVIDIA NIM's free
inference API** (a large rotating catalog of hosted open models, no cost) as a local OpenAI
server, and it owns every piece of configuration that LM Studio's plugin can't — which model
answers, what happens when that model is down, and what system prompt every chat runs with.

```
LM Studio ──▶ http://localhost:5001/v1/chat/completions ──▶ ASP.NET Minimal API ──▶ NVIDIA NIM
              (via the openai-compat-endpoint plugin)          (this proxy)
```

## What it does

**Server-side auth.** The NVIDIA API key lives in a git-ignored local config file, never in the
client. LM Studio sends no credentials at all.

**Dynamic model selection with automatic failover.** NVIDIA's free catalog changes — models go
down, get renamed, get added — so the proxy doesn't hardcode a model id. On each request it
pulls the live `/v1/models` list, filters out non-chat models (embeddings, rerankers, guard/
safety/reward models — inferred by name, since the API exposes no capability field), and tries
candidates in order until one responds:

- A configurable **per-attempt timeout** (default 30s) bounds how long a hung model can block a
  request before the proxy moves on — critical because a dead model otherwise just hangs
  the connection indefinitely.
- **Retries** on transient failures (5xx / 429 / network errors) before abandoning a model;
  immediate failover on hard failures (timeout, 404).
- A **soft preference list** (`ModelPrefer`) biases which models are tried first without ever
  dead-ending — if every preferred model is unavailable, it falls through to the rest of the
  live catalog instead of erroring.
- **Sticky "last good" model per provider** — once a model answers, it's tried first on
  subsequent requests, so healthy traffic doesn't pay a re-probing cost every time.

**Proxy-owned system prompt.** LM Studio's plugin only exposes a per-chat model field — there's
no global system prompt. The proxy strips whatever system message the client sends (if any) and
injects its own, loaded from a plain Markdown file (`system-prompt.md`) rather than crammed into
JSON. One edit, restart, and every chat — regardless of what LM Studio's UI has configured —
runs with it.

**Model announcement.** Since the model is chosen dynamically, every response can be prefixed
with a line naming which model actually answered (`AnnounceModel`) — for both streaming and
non-streaming responses, since dynamic failover means it isn't always the same one.

**Streaming passthrough.** NVIDIA NIM already emits OpenAI-shaped SSE events, so streamed
responses are piped straight through with no re-encoding — failover is decided on the response
headers before any bytes reach the client, so a stream is never started on one model and
switched mid-flight.

**`GET /v1/models`** — merges and caches each configured provider's real model list
(`ModelsCacheMinutes`, default 30 min) so LM Studio's model picker reflects the live catalog.
`POST /v1/models/refresh` forces a re-fetch without restarting the server.

**Multi-provider ready.** NVIDIA is the active provider; OpenRouter, Groq, Cerebras, and
Together AI are pre-wired in config (base URL + key env var) so any of them can become the
default, or be exposed as a named alias, without touching code.

## Run

```bash
cd ~/RiderProjects/LlmProxy
dotnet run
```

The NVIDIA key lives in `appsettings.Local.json` (git-ignored — see [Secrets](#secrets)), so a
plain `dotnet run` picks it up. Then, in LM Studio, install the
[`ankh/openai-compat-endpoint`](https://lmstudio.ai/ankh/openai-compat-endpoint) plugin from the
Hub and point its **Base URL** at `http://localhost:5001/v1` (any placeholder API key and model
— the proxy overrides both).

## Configure

Everything lives under `Proxy` in [appsettings.json](appsettings.json):

| Key | Meaning |
|-----|---------|
| `DefaultProvider` | Provider used when a model id isn't an alias. |
| `ModelsCacheMinutes` | TTL for the `/v1/models` cache. |
| `LogRequests` | Per-request log line (model, provider, attempt, outcome, elapsed). |
| `AttemptTimeoutSeconds` | Max wait per upstream attempt before failing over (default 30). |
| `MaxAttemptsPerModel` | Retries per model on transient failures before moving to the next (default 2). |
| `MaxDynamicCandidates` | Cap on how many live models to try per request (default 10). |
| `AnnounceModel` | Prefix each response with the answering model's name. |
| `ModelAnnounceFormat` | Format string for the announce line (`{model}` substituted). |
| `Providers.<name>.BaseUrl` | OpenAI-compatible base URL incl. `/v1`. |
| `Providers.<name>.ApiKeyEnv` / `ApiKey` | Key source — env var (preferred) or literal. |
| `Providers.<name>.HideFromModels` | Exclude this provider from the merged `/v1/models` list. |
| `Providers.<name>.DynamicModels` | Pull chat-capable candidates live instead of a fixed list. |
| `Providers.<name>.ModelPrefer` | Soft ordering bias for dynamic candidates. |
| `Providers.<name>.ModelExclude` | Override the built-in non-chat filter keywords. |
| `Providers.<name>.ForceModel` / `ForceModels` | Pin to one model, or an explicit fallback chain (skips dynamic selection). |
| `Providers.<name>.SystemPromptFile` / `SystemPrompt` | Global system prompt — file path (preferred) or inline string. |
| `ModelAliases.<id>` | Map a client-facing id → `{ Provider, UpstreamModel }`. |

### Add another provider

The other four are already listed with `HideFromModels: true`. To make Groq the default:

```jsonc
"DefaultProvider": "groq"
```

…and set `GROQ_API_KEY`. Or expose a stable alias regardless of default:

```jsonc
"ModelAliases": {
  "fast": { "Provider": "groq", "UpstreamModel": "llama-3.3-70b-versatile" }
}
```

## Secrets

Real API keys go in **`appsettings.Local.json`** (git-ignored, layered on top of
`appsettings.json` at startup) — never in the committed config:

```json
{
  "Proxy": { "Providers": { "nvidia": { "ApiKey": "nvapi-..." } } }
}
```

`ApiKeyEnv` (an environment variable) is checked first and takes precedence if set. Either way,
the key never reaches LM Studio or any client — it's injected server-side per request.

## Notes

- Port is set in `appsettings.json` under `Kestrel:Endpoints:Http:Url`.
- Get a free NVIDIA NIM key at [build.nvidia.com](https://build.nvidia.com).
