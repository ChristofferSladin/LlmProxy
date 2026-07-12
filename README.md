# LlmProxy

A local OpenAI-compatible reverse proxy. LM Studio (or any OpenAI client) talks to
`http://localhost:5001/v1/...`; every request is executed on an upstream provider —
NVIDIA NIM by default — with the API key injected server-side.

```
LM Studio ──▶ http://localhost:5001/v1/chat/completions ──▶ ASP.NET Minimal API ──▶ NVIDIA NIM
```

## What it does

- **`GET /v1/models`** — fetches each provider's real model list, merges + caches it
  (`ModelsCacheMinutes`, default 30). LM Studio's model dropdown shows the live catalog.
  `POST /v1/models/refresh` clears the cache.
- **`POST /v1/chat/completions`**, `/v1/completions`, `/v1/embeddings` — passthrough to
  the resolved provider. Streaming (`"stream": true`) is piped straight through as SSE;
  NVIDIA NIM already emits OpenAI-shaped events, so no translation is needed.
- **API key injection** — the key never leaves the proxy; clients send no auth.
- **Model routing / aliases** — a requested model id maps to a provider + upstream model
  via `ModelAliases`; anything unknown passes through to `DefaultProvider`.
- **Request logging** — one line per request (model, provider, status, elapsed).
- **Multi-provider ready** — NVIDIA, OpenRouter, Groq, Cerebras, Together AI are pre-wired
  in `appsettings.json`; add a key and flip `DefaultProvider`/aliases to use them.

## Run

```bash
export NVIDIA_API_KEY="nvapi-..."   # get a free key at build.nvidia.com
cd ~/RiderProjects/LlmProxy
dotnet run
```

Then in **LM Studio → Settings → point the OpenAI base URL at** `http://localhost:5001/v1`
(leave the API key blank or any placeholder — the proxy supplies the real one).

## Configure

Everything lives under `Proxy` in [appsettings.json](appsettings.json):

| Key | Meaning |
|-----|---------|
| `DefaultProvider` | Provider used when a model id isn't an alias. |
| `ModelsCacheMinutes` | TTL for the `/v1/models` cache. |
| `LogRequests` | Per-request log line on/off. |
| `Providers.<name>.BaseUrl` | OpenAI-compatible base URL incl. `/v1`. |
| `Providers.<name>.ApiKeyEnv` | Env var holding the key (preferred over `ApiKey`). |
| `Providers.<name>.HideFromModels` | Exclude this provider from the merged model list. |
| `ModelAliases.<id>` | Map a client-facing id → `{ Provider, UpstreamModel }`. |

### Add another provider

The other four are already listed with `HideFromModels: true`. To make Groq the default:

```jsonc
"DefaultProvider": "groq"
```

…and `export GROQ_API_KEY=...`. Or expose a stable alias regardless of default:

```jsonc
"ModelAliases": {
  "fast":     { "Provider": "nvidia", "UpstreamModel": "meta/llama-3.1-8b-instruct" },
  "big-brain":{ "Provider": "groq",   "UpstreamModel": "llama-3.3-70b-versatile" }
}
```

## Notes

- Port is set in `appsettings.json` under `Kestrel:Endpoints:Http:Url`.
- Keys are read from env vars by default so they stay out of the config file. Never commit a real key.
