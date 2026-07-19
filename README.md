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

## Working around the LM Studio plugin

`openai-compat-endpoint` is the only bridge that lets LM Studio's chat GUI act as a *client* of
a remote OpenAI-compatible server at all, but it's rigid in a way that fights everything this
proxy is for: it requires a non-empty **Model** value per chat before it will send anything, and
that field has no default — every new chat starts broken with `"Missing model. Set a model ID in
plugin settings."` That's exactly the kind of per-chat manual config this project exists to
eliminate, since the proxy already decides the model dynamically.

The plugin's declared config (`src/config.ts`) *does* support a default value for that field —
but LM Studio doesn't run `src/`, it runs a prebuilt bundle (`.lmstudio/production.js`) that's
stale the moment you edit the source. `lms dev` rebuilds from source into a separate `dev.js`
for live development, but that only lives for as long as the dev server keeps running — closing
it (or restarting LM Studio) reverts you to the stale, broken bundle.

The workaround: build once with `lms dev`, then promote that output into the file LM Studio
actually loads, so the fix survives restarts without needing a dev server running forever.

```bash
cd ~/.lmstudio/extensions/plugins/ankh/openai-compat-endpoint
# 1. Edit src/config.ts — give "model" and "baseUrl" real defaults, e.g.:
#      baseUrl default: "http://localhost:5001/v1"
#      model default:   "some-model"   (any non-empty placeholder — the proxy overrides it)

# 2. Build from source
~/.lmstudio/bin/lms dev &     # compiles .lmstudio/dev.js, registers with LM Studio
sleep 5 && kill %1            # once it's built, the dev server has done its job

# 3. Promote the build LM Studio actually loads
cp .lmstudio/production.js .lmstudio/production.js.orig-bak   # keep a backup
cp .lmstudio/dev.js .lmstudio/production.js

# 4. In LM Studio: eject and reload the plugin (or restart the app)
```

After this, every new chat starts with a working model value pre-filled — the proxy's dynamic
selection and failover take over from there, and LM Studio's per-chat config becomes a dummy the
proxy ignores. The one thing to know: **reinstalling or updating the plugin from the Hub
overwrites `production.js`** with the stock build again, silently undoing the fix — if that
happens, repeat steps 2–4 (or restore from the `.orig-bak`).

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
| `CooldownSeconds` | Bench a model this long after it returns a 429 or a 200-wrapped error, so failover skips it on subsequent requests instead of re-probing the wall (default 60). Never dead-ends: if every candidate is benched, cooldowns are ignored. |
| `RoutingRules` | Ordered request-shape rules that softly bias candidate ordering. Each is `{ When: { HasTools?, MinChars?, ContentMatches? }, Prefer: [patterns] }`; first match wins, and the prefer list outranks the static `ModelPrefer` for that request. Empty = default ordering. Never excludes a candidate — that's the tool-capability filter's job. |
| `IdentityAnchor` | Model-agnostic text appended after the system prompt so a conversation stays continuous as models swap underneath it, and the answering model doesn't claim to be a specific commercial model (e.g. Claude, GPT). Ships with a sensible default; blank to disable. |
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

## Service mode: first deploy

Service mode runs the same binary as an authenticated, multi-consumer proxy on Azure App Service
(Free/F1 tier). This section is the first-deploy runbook, in order. Full operational docs (key
issuance/rotation reference, alias profile fields, the consumer migration map) land separately
once every behavioural ticket is done — this section only covers getting a first instance live
and verifying it.

Files referenced below: `infra/main.bicep`, `infra/main.bicepparam`, `infra/setup-oidc.sh`,
`.github/workflows/deploy.yml`, `.github/workflows/keepwarm.yml`, `scripts/smoke.sh`.

### 1. One-time OIDC setup (human, `az login` required)

Run `infra/setup-oidc.sh` once from a machine with the Azure CLI logged in as a user who can
create app registrations and assign roles. Edit the script's `RESOURCE_GROUP` (and
`GITHUB_REPO`/`GITHUB_BRANCH` if they differ) before running — see the script's header comment.
It creates an Azure AD app registration with a federated credential trusting GitHub Actions'
OIDC token for this repo's `main` branch (no long-lived client secret), and grants it
`Website Contributor` scoped to the resource group only.

It prints three values. Put them into the GitHub repo's **Settings → Secrets and variables →
Actions → Variables** tab (variables, not secrets — none of these are credentials; the federated
credential trust is what actually gates access):

```
AZURE_CLIENT_ID       = <printed appId>
AZURE_TENANT_ID       = <printed tenant id>
AZURE_SUBSCRIPTION_ID = <printed subscription id>
```

Also add a fourth variable, `AZURE_WEBAPP_NAME`, set to the site name the infra will produce —
`<appName>-app`, e.g. `llmproxy-app` for the default `appName=llmproxy` in
`infra/main.bicepparam`.

### 2. Deploy the infrastructure

From a machine with `az` logged in, create the resource group (matching `setup-oidc.sh`'s
`RESOURCE_GROUP`) if it doesn't exist yet, then deploy the Bicep template. The NVIDIA key is
passed at deploy time, not committed to `infra/main.bicepparam`:

```bash
az group create --name llmproxy-rg --location swedencentral

az deployment group create \
  --resource-group llmproxy-rg \
  --template-file infra/main.bicep \
  --parameters infra/main.bicepparam \
  --parameters nvidiaApiKey=$NVIDIA_API_KEY
```

If this fails with a quota/SKU-not-available error for the F1 tier in `swedencentral`, retry
with `--parameters location=westeurope` (the documented fallback — see `infra/main.bicep`'s
header comment).

The deployment output `defaultHostName` is the deployed base URL, e.g.
`llmproxy-app.azurewebsites.net`.

### 3. Set inbound keys (post-deploy, not in Bicep)

Inbound keys are operator data that rotates independently of infra, so they're set as App
Service settings after deploy, not baked into the template (see `infra/main.bicep`'s header
comment). Each key needs three settings following the
`Proxy__InboundKeys__<KeyId>__{App,Aliases__N,RequestsPerMinute}` convention. Worked example —
issuing a key for `news-digest` with a 10 requests/minute budget:

```bash
az webapp config appsettings set \
  --name llmproxy-app \
  --resource-group llmproxy-rg \
  --settings \
    "Proxy__InboundKeys__<random-32-byte-key>__App=news-digest" \
    "Proxy__InboundKeys__<random-32-byte-key>__Aliases__0=news-digest" \
    "Proxy__InboundKeys__<random-32-byte-key>__RequestsPerMinute=10"
```

Generate `<random-32-byte-key>` with, e.g., `openssl rand -hex 32`, and use the same generated
value as both the setting-name suffix and the key material the consumer app authenticates with
(`Authorization: Bearer <random-32-byte-key>`). Repeat per app/alias; two live keys may share one
`App` value for rotation without downtime.

Without at least one inbound key set, `ASPNETCORE_ENVIRONMENT=Production` (set by
`infra/main.bicep`) makes the host refuse to start — this is deliberate fail-fast behaviour, not
a bug (see startup validation).

### 4. Trigger the deploy workflow

Push to `main`, or manually run `.github/workflows/deploy.yml` via **Actions → Deploy → Run
workflow** (`workflow_dispatch`). The workflow builds, runs `dotnet test`, publishes
self-contained `linux-x64`, authenticates via OIDC (no secret), and zip-deploys to the app
created in step 2. It needs the four repo variables set in step 1 to be present, or the Azure
login step fails.

### 5. Smoke test the live instance

Run `scripts/smoke.sh` against the deployed URL with a key issued in step 3:

```bash
scripts/smoke.sh https://llmproxy-app.azurewebsites.net <your-inbound-key> news-digest 10
```

(base URL, key, alias, and the key's configured `RequestsPerMinute` budget — the last two are
optional; see `scripts/smoke.sh --help` / run with no args for full usage). This checks: `/health`
reachable without a key, a keyed completion via the alias succeeds, a wrong key is rejected with
401, a burst past the budget yields a 429 (skipped with a warning if no budget is supplied), and
an indirect check that the Production config layer — and therefore the absence of the personal
`system-prompt.md` from the deployed payload — is actually in effect.

This is the one step of T11 that cannot run unattended: it needs a real deployed URL and a real
key, so it's the human's step, not a night agent's.

### Ongoing: keep-warm

Once `AZURE_WEBAPP_NAME` is set and the app is deployed, `.github/workflows/keepwarm.yml` starts
pinging `/health` every ~10 minutes on its own cron schedule automatically — no separate action
needed after the first deploy.
