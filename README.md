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
| `ModelAliases.<id>` | Map a client-facing id → a routing profile (`Provider`, `UpstreamModel`, plus the service-mode fields below). See [Service mode](#service-mode) for the full alias reference. |
| `InboundKeys.<key>` | Per-application bearer key record. Absent/empty ⇒ authentication disabled (today's local behavior). See [Service mode](#service-mode). |
| `RateLimitWindowSeconds` | Length of one rate-limit window in seconds (default 60, matching `RequestsPerMinute`'s "per minute" semantics). See [Service mode](#service-mode). |

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

## Service mode

The same binary runs in two modes, selected entirely by configuration:

- **Local mode** — no `Proxy:InboundKeys` configured. This is the LM Studio setup described
  above: no inbound auth, no rate limiting, the provider owns the system prompt. **This is
  byte-identical to the proxy's original behavior — it is the backward-compatibility guarantee
  the whole feature was built around.** Nothing in this section changes how the local instance
  behaves; every rule below only activates once at least one key exists in `InboundKeys`.
- **Service mode** — one or more `InboundKeys` entries configured. The proxy becomes an
  authenticated, multi-consumer front end: each caller (a separate application, not a person)
  authenticates with a bearer key, is restricted to the aliases its key grants, and gets its own
  rate-limit budget. The existing failover engine (cooldown, tool-capability filtering, routing
  rules, never-dead-end) runs underneath both modes, completely unchanged.

The two modes are not separate deployments — the same `appsettings.json`/`appsettings.Local.json`
layering that already exists is where you add `InboundKeys` and per-alias fields to turn service
mode on.

### Inbound keys

`Proxy:InboundKeys` is a dictionary keyed by the secret key string itself. Each entry (an
`InboundKey`) is `{ App, Aliases, RequestsPerMinute }`:

```jsonc
"Proxy": {
  "InboundKeys": {
    // 32 bytes of CSPRNG entropy per key. Two live keys may share an App (see Rotation below).
    "<random-key-1>": { "App": "ai-news-digest",   "Aliases": ["news-digest"], "RequestsPerMinute": 10 },
    "<random-key-2>": { "App": "portfolio",        "Aliases": ["portfolio"],   "RequestsPerMinute": 20 },
    "<random-key-3>": { "App": "blockchain-agent", "Aliases": ["bca"],         "RequestsPerMinute": 5 }
  },
  "ModelAliases": {
    "news-digest": { "Provider": "nvidia", "PromptMode": "Passthrough",
                     "ModelPrefer": ["llama-3.1-8b", "flash"] },
    "portfolio":   { "Provider": "nvidia", "PromptMode": "Anchor",
                     "ModelPrefer": ["deepseek-ai/deepseek-v4-flash", "llama-3.3-70b", "nemotron-super"] },
    "bca":         { "Provider": "nvidia", "PromptMode": "Passthrough",
                     "UpstreamModel": "moonshotai/kimi-k2.6",
                     "ModelPrefer": ["kimi", "deepseek", "nemotron"],
                     "AttemptTimeoutSeconds": 180 }
  }
}
```

| `InboundKey` field | Meaning |
|---|---|
| `App` | Attribution name used in logs and as the rate-limit partition key. Never the key material itself. |
| `Aliases` | The only `ModelAliases` names this key may request. |
| `RequestsPerMinute` | Per-minute budget for this key's `App`. Null/omitted = unlimited. |

**Issuance.** There is no registration flow, no token endpoint, and no proxy-issued credential —
adding a key is a config edit: add an entry to `InboundKeys` (generate the key string yourself,
24 bytes of CSPRNG entropy, e.g. `openssl rand -hex 24` — hex, not base64, and not longer: the
key string becomes part of an App Service *setting name* on Azure, which forbids base64's
`+`/`/`/`=` characters and rejects names longer than ~100 characters), give it an `App` name,
grant it the
aliases it needs, redeploy. This is a deliberate decision, not an oversight: the consumers are
machine-to-machine calls with no end user, so a JWT/login-endpoint flow would add a hop and a
signing key without removing the underlying secret each app still has to hold at startup — see
the brief's decisions log for the full reasoning (and the trigger — end-user identity reaching the
proxy — that would revisit it in favor of Entra ID client-credentials).

**Rotation.** Two live keys may map to the same `App` at the same time — this is what makes
rotation a deploy instead of an outage: add the new key (same `App`, same `Aliases`) alongside the
old one, update the consuming app's configuration to the new key, redeploy the app, then remove
the old key entry from `InboundKeys` and redeploy the proxy. The rate-limit budget is shared by
`App`, not doubled by having two live keys (see [Rate limiting](#rate-limiting) below).

**Revocation.** Delete the key entry from `InboundKeys` and redeploy. The blast radius of a leaked
key is exactly one `App` and only the aliases it was granted — a leak doesn't expose any other
application's identity, prompt policy, or budget. If the app has a second live key (mid-rotation),
it keeps running on that one.

**What a key grants.** A key may only request the aliases listed in its `Aliases`:

- If the key grants exactly **one** alias, the request's `model` field may be omitted — it's
  routed to that alias automatically.
- If the key grants **more than one** alias, the request must specify `model` explicitly; omitting
  it is rejected with `400` (`"This key grants multiple aliases (...); the request must specify
  'model'."`).
- Requesting a `model` that isn't one of the key's granted aliases is rejected with `400`, naming
  the aliases that key may use (`"Model '<x>' is not permitted for this key. Allowed aliases:
  ..."`).
- A missing, malformed (not `Bearer <token>`), or unrecognized key is rejected with `401` and a
  generic body (`{"error":{"message":"Unauthorized","type":"authentication_error","code":401}}`).
  All three reasons return the identical body deliberately — the client is never told *which*
  reason applied, so a bad guess can't be used to probe for valid key shapes. Key material is
  never written to logs or echoed in any response, on any path, including rejection.

`/` and `/health` never require a key, in either mode — this is what the keep-warm ping depends
on.

### Alias routing profiles

`ModelAliases.<id>` maps a client-facing model id to a full routing profile, not just a provider +
model pair. Beyond the existing `Provider` and `UpstreamModel`, four fields exist purely to
support service mode; all are optional and each one's absence reproduces today's local behavior
exactly:

| Field | Meaning | Absent ⇒ |
|---|---|---|
| `PromptMode` | `Passthrough`, `Anchor`, or `Own` — see below. | `Own` (today's provider-owned prompt behavior) |
| `UpstreamModel` | On a **static** provider (`DynamicModels: false`), the forced single candidate — unchanged, existing behavior. On a **dynamic** provider (`DynamicModels: true`), this is now a **pin-first-then-failover** seed: tried first, but *only if it's currently a live candidate* from the provider's catalog; if it fails or isn't live, normal dynamic failover proceeds behind it. It is never forced in and never a filter, so a stale/renamed pin can't dead-end a request. | Dynamic discovery only (today's behavior) — on a dynamic provider with no pin, candidates come purely from the live catalog. |
| `ModelPrefer` | Per-alias candidate ordering bias, overriding the provider's own `ModelPrefer` for this alias's requests only. Non-aliased requests keep the provider's ordering untouched. | The provider's `ModelPrefer` |
| `AttemptTimeoutSeconds` | Per-alias override of the global `AttemptTimeoutSeconds` for this alias's requests. | The global `AttemptTimeoutSeconds` (default 30) |

**`PromptMode` — the three values, precisely:**

- **`Passthrough`** — the client's `messages` array is relayed byte-identical: nothing removed,
  nothing inserted, order and content preserved exactly as sent. Use this when the caller owns its
  own system prompt (e.g. `news-digest`'s summarizer prompt, `bca`'s structured-output
  instructions) and the proxy must not interfere with it.
- **`Anchor`** — every client message is preserved (including any system message(s) the client
  sent — nothing is stripped), and exactly one additional system message carrying the proxy's
  `IdentityAnchor` text is inserted after the client's *last* system message (or as the first
  message if the client sent none). Use this when the caller has its own assistant persona but
  should still inherit the proxy's "don't claim to be a specific commercial model, maintain
  continuity across a failover" instruction (e.g. `portfolio`). A blank/unset `IdentityAnchor`
  injects nothing.
- **`Own` (the default when unset)** — today's original behavior: any system message(s) the client
  sent are stripped and replaced with one composed system message (the provider's base
  `SystemPrompt`/`SystemPromptFile` plus `IdentityAnchor`, joined by a blank line). This is what
  local/LM Studio mode uses and what an alias with no `PromptMode` set continues to use.

All non-`messages` body fields (`response_format`, `temperature`, sampling parameters, etc.) pass
through unmodified in every mode — only `messages` (per `PromptMode`) and `model` (rewritten to
the alias's upstream target) are ever touched.

### Rate limiting

`InboundKey.RequestsPerMinute` sets a per-minute budget, partitioned by the key's **`App`, not the
key string** — so two live keys rotated for the same application share one budget rather than
doubling it. `Proxy:RateLimitWindowSeconds` (default 60) sets the window length globally.

- **Unconfigured = unlimited.** A key with no `RequestsPerMinute` is never throttled. If no
  `InboundKeys` are configured at all, rate limiting doesn't run — local mode is unaffected.
- One application exhausting its budget never affects another application's.
- A rate-limit rejection is decided **before any upstream call** — it never counts against a
  model's cooldown/bench state in `RoutingState`.
- Rejections return `429` with a `Retry-After` header (seconds until the window resets, minimum
  1) and a generic JSON error body (`{"error":{"message":"Rate limit exceeded","type":
  "rate_limit_error","code":429}}`).

Per-application budgets should sum comfortably under the shared upstream provider's own rate
limit (NVIDIA NIM's free tier is roughly 40 requests/minute across every consumer, local
instance included) — staggering batch jobs against that shared ceiling is each consuming
application's responsibility, not the proxy's.

### Startup validation

The host validates configuration at startup and refuses to start — collecting every violation
into one error rather than failing on the first — when:

- The environment is `Production` and zero `InboundKeys` are configured (a misconfigured deploy
  must not come up wide open on a public URL).
- An `InboundKeys` entry grants an alias name that doesn't exist in `ModelAliases` (a config typo
  must surface at deploy time, not as a runtime `400` later).
- A `ModelAliases` entry names a provider that isn't configured under `Providers`.

Every validation error names the offending key's **`App`**, never the key string itself — so a
startup failure log is safe to paste anywhere.

### Consumer migration map

The following describes how each consumer application **would** integrate with the proxy in
service mode — it is reference material carried forward from the design brief, not a task list for
this repository. No code in any of these repositories is touched by this work; the config-only
swaps below happen later, in their own repos.

- **ai-news-digest** (daily batch job): swap its Gemini-shaped endpoint config to point at the
  proxy (`.../v1/`), set its model field to `news-digest`, and swap its API key setting to the
  app's inbound proxy key. Its existing 429-aware retry (honoring `Retry-After`) and
  fallback-to-description behavior already tolerate the proxy's failure modes without changes.
- **ChristofferSladinPortoflio**: the chat widget's backend calls the proxy with alias
  `portfolio`; the key stays server-side and is never shipped to the browser. A `429` from the
  proxy should surface as a friendly "busy, try again" message rather than a raw error.
- **BlockchainAgent**: point its NVIDIA endpoint constant at the proxy, set its model env var to
  `bca` (the model field carries the alias name), and swap its API key to the inbound proxy key.
  Because `bca`'s `AttemptTimeoutSeconds` (180s) can multiply by the retry count per model, the
  consuming application's own HTTP client timeout needs to tolerate the worst case, not just the
  per-attempt timeout alone.

### Deployment

Service mode runs the same binary as an authenticated, multi-consumer proxy on Azure App Service
(Free/F1 tier), via the infrastructure-as-code and CI/CD under `infra/` and `.github/workflows/`.
See "Service mode: first deploy" below for the runbook.

**Live since 2026-07-20**: `https://llmproxy-app.azurewebsites.net` (resource group
`llmproxy-rg`, swedencentral), deployed by `deploy.yml` on every push to `main`, kept warm by
`keepwarm.yml`. All three consumers from the migration map are wired: ai-news-digest
(`news-digest`), the portfolio chat backend (`portfolio`), and BlockchainAgent (`bca`). Inbound
keys are App Service settings — see step 3 of the runbook and the redeploy warning there.

## Service mode: first deploy

This is the first-deploy runbook, in order — getting a first instance live and verifying it.
Full operational reference (key issuance/rotation, alias profile fields, the consumer migration
map) is in "Service mode" above.

Files referenced below: `infra/main.bicep`, `infra/main.bicepparam`, `infra/setup-oidc.sh`,
`.github/workflows/deploy.yml`, `.github/workflows/keepwarm.yml`, `scripts/smoke.sh`.

### 1. One-time OIDC setup (human, `az login` required)

First create the resource group — the script's role assignment is scoped to it and fails if it
doesn't exist yet:

```bash
az group create --name llmproxy-rg --location swedencentral
```

Then run `infra/setup-oidc.sh` once from a machine with the Azure CLI logged in as a user who can
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

From a machine with `az` logged in, deploy the Bicep template into the resource group created in
step 1. The NVIDIA key is read from the `NVIDIA_API_KEY` environment variable at deploy time by
`infra/main.bicepparam` (`readEnvironmentVariable`), never committed — a separate
`--parameters nvidiaApiKey=...` override alongside a `.bicepparam` file does not work (Bicep
requires the params file to assign every declared parameter, error BCP258):

```bash
NVIDIA_API_KEY=nvapi-... az deployment group create \
  --resource-group llmproxy-rg \
  --template-file infra/main.bicep \
  --parameters infra/main.bicepparam
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
    "Proxy__InboundKeys__<random-24-byte-key>__App=news-digest" \
    "Proxy__InboundKeys__<random-24-byte-key>__Aliases__0=news-digest" \
    "Proxy__InboundKeys__<random-24-byte-key>__RequestsPerMinute=10"
```

Generate `<random-24-byte-key>` with `openssl rand -hex 24` (48 hex chars — longer keys push the
`__RequestsPerMinute` setting name past App Service's ~100-character name limit and the whole
`appsettings set` call fails with a bare `Bad Request`), and use the same generated
value as both the setting-name suffix and the key material the consumer app authenticates with
(`Authorization: Bearer <random-24-byte-key>`). Repeat per app/alias; two live keys may share one
`App` value for rotation without downtime.

Without at least one inbound key set, `ASPNETCORE_ENVIRONMENT=Production` (set by
`infra/main.bicep`) makes the host refuse to start — this is deliberate fail-fast behaviour, not
a bug (see startup validation).

**Redeploy warning:** every rerun of `az deployment group create` with `infra/main.bicep`
replaces the app's *entire* settings collection with the template's three entries, silently
deleting the inbound keys set in this step — and the app then fail-fasts as above. Keep the
settings JSON you issued keys with, and after any infra redeploy re-apply it
(`az webapp config appsettings set ... --settings @<file>`) and restart the app.

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
