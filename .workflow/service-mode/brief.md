# Brief: service-mode   (repo: LlmProxy, base: main)

## What the human asked (raw)

> is it possible to use this llm proxy as a service from my other projects? lets say i have an
> ai feature in news-digest repo (ranking news with ai), villa-kristina (an ai assistant for
> information about the villa), christoffersladinportfolio project (an assistant reading from a
> doc about me and answering the user about me and my experience). all of them are pivoting to
> using nvidia nim endpoints to use the inference models but the issue is that the models on the
> nim is sometimes taken off the models list or not available when needed. this proxy solves that
> problem. and i could tailor this proxy to be generic enough to be used by my other projects for
> this type of ai features in my apps. my question is if i can create a free service out of this?
> an app service free tier on azure? reroute all endpoints to the proxy instead of directly to nvidia?

Follow-up scope rulings after the cross-project survey (2026-07-19):
- VillaKristina: **dropped** — the AI feature is redundant and will be removed from that repo entirely.
- Portfolio: integrates with the proxy (chat only).
- BlockchainAgent: **added** to scope (already on NIM directly; needs per-alias timeout).
- News alias renamed `news-digest` (it summarizes; it does not rank).
- solar-m2m-platform stays on Claude — untouched.

## Current state vs desired (the diff)

- **Now:** a single-tenant localhost proxy for LM Studio. Zero inbound auth. Prompt ownership is
  per-PROVIDER (`nvidia.SystemPromptFile: system-prompt.md` = personal dev identity + global
  `IdentityAnchor`; every chat request gets client system messages stripped and this injected).
  `ModelAliases` are thin (`Provider` + `UpstreamModel`) and `UpstreamModel` is **dead config on
  dynamic providers** (ProxyService.BuildCandidatesAsync ignores `route.Models` when
  `DynamicModels: true`). Unknown model ids silently fall through to the default provider.
  One global `AttemptTimeoutSeconds` (30s). Kestrel pinned to `http://localhost:5001` in
  appsettings.json. No CI/CD, no infra, `system-prompt.md` not in publish output.
- **Want:** the same binary also runs as a small authenticated service on Azure App Service F1,
  serving three consumer apps through named aliases that each carry their own prompt policy,
  model bias, and (for bca) timeout — with the existing failover engine (cooldown, tool-filter,
  routing rules, never-dead-end) untouched underneath. LM Studio keeps using the local instance;
  local no-keys behavior stays byte-identical. Deploy is Bicep + GitHub Actions (OIDC) with a
  keep-warm ping.

## Acceptance criteria (testable)

- [ ] **Local regression frozen:** with no `InboundKeys` configured, behavior is byte-identical to
      today — verified by: `dotnet test` (all pre-existing tests pass without modification) and a
      TestHost request without an `Authorization` header succeeding.
- [ ] **Auth on:** with `InboundKeys` configured, any `/v1/*` request with a missing or wrong
      Bearer key → 401 JSON error; `/` and `/health` respond 200 without a key — verified by:
      new TestHost tests.
- [ ] **Alias binding:** valid key + `model` not in that key's `Aliases` → 400 whose message names
      the allowed aliases — verified by: TestHost test.
- [ ] **Model omission:** single-alias key + no `model` field → routes to that alias; multi-alias
      key + no `model` → 400 — verified by: TestHost tests.
- [ ] **Production fail-fast:** `ASPNETCORE_ENVIRONMENT=Production` + no inbound keys → host
      refuses to start — verified by: startup-validation test.
- [ ] **Per-key rate limiting:** requests beyond an app's configured per-minute budget → 429 with a
      `Retry-After` header, and one app exceeding its budget does not consume another app's —
      verified by: TestHost test with a compressed window (e.g. limit 2/window, third request 429,
      a second key's request still 200).
- [ ] **Rate limiting is off when unconfigured:** no configured limits → no throttling (local
      LM Studio path unaffected) — verified by: TestHost test.
- [ ] **Keys never logged:** request logs contain the key's `App` name and never the key material,
      including on the 401 path — verified by: log-capture test asserting the key string is absent.
- [ ] **Key rotation:** two live keys may map to the same app simultaneously, both accepted —
      verified by: TestHost test (this is what makes rotation a deploy rather than an outage).
- [ ] **Passthrough mode:** alias with `PromptMode: Passthrough` relays the client's `messages`
      byte-identical (no strip, no injection) — verified by: FakeUpstream capture test.
- [ ] **Anchor mode:** alias with `PromptMode: Anchor` preserves all client messages and appends
      one additional system message containing `IdentityAnchor` after the client's last system
      message (inserted first if the client sent none) — verified by: FakeUpstream capture test.
- [ ] **Pin-first:** alias `UpstreamModel` on a dynamic provider is attempted first; when it fails,
      failover proceeds into the normal dynamic candidates — verified by: FakeUpstream ordering test.
- [ ] **Per-alias prefer:** alias `ModelPrefer` reorders candidates for that alias's requests only;
      non-alias requests keep the provider-level bias — verified by: FakeUpstream ordering test.
- [ ] **Per-alias timeout:** alias `AttemptTimeoutSeconds` (bca: 180) overrides the global value
      for that request — verified by: unit test on effective-option resolution plus an integration
      test with compressed values (global 1s, alias 3s, upstream delayed 2s → succeeds only via alias).
- [ ] **Infra compiles:** `az bicep build --file infra/main.bicep` exits 0.
- [ ] **Production config safe:** the Production configuration layer sets `AnnounceModel: false`,
      references no `SystemPromptFile`, and does not pin Kestrel to localhost — verified by:
      config-layer test or explicit assertion test.
- [ ] **Deployed smoke (HITL — needs the human's Azure login):** `curl https://<app>.azurewebsites.net/health`
      → 200; a keyed chat completion via alias returns a completion; a wrong key → 401.

## Non-goals / out of scope

- VillaKristina anything — the villa AI feature is being **removed** in its own repo; no `villa` alias.
- Embeddings routing/aliases — proxy service mode is **chat-only** (v1). The `/v1/embeddings`
  passthrough endpoint stays as-is but no consumer uses it; embedding-aware aliases (pinned model,
  no chat-filter, no cross-model failover) are a possible fast-follow, not this feature.
- CORS / browser-direct access — portfolio's widget calls its own backend, which calls the proxy.
- Public/strangers access, signup, metering, billing.
- Token-based auth of any kind — no `/auth/login` endpoint, no proxy-issued JWTs, no Entra ID
  integration (see the auth decision below for why, and for the trigger that would revisit it).
- Multi-instance/distributed routing state (single F1 instance; in-memory state is fine).
- Activating groq/openrouter/cerebras/together (stay pre-wired, hidden).
- Key Vault, custom domains, deployment slots.
- Per-key-filtered `GET /v1/models`.
- **Any code changes in consumer repos** (ai-news-digest, portfolio, BlockchainAgent) — the
  migration map below is reference for later work in those repos, not tickets here.
- solar-m2m-platform's ClaudeContractGenerator — stays on the Anthropic API, untouched.

## Blast radius

- **Fair game:** `ProxyOptions.cs`, `ProviderRegistry.cs`, `ProxyService.cs` (prompt-policy hook,
  candidate seeding, per-request timeout resolution only), `PromptComposer.cs`, `Program.cs`
  (auth filter, rate-limiter registration, startup validation, config layering),
  `appsettings.json` (additive),
  new `appsettings.Production.json`, new `infra/`, new `.github/workflows/`, `README.md`,
  `LlmProxy.Tests/*`.
- **Off limits:** `RoutingState.cs`, `RoutingRuleSet.cs`, `RequestClassifier.cs`,
  `ModelCatalog.cs` — the routing engine is finished and stays untouched. `system-prompt.md`
  content. The LM Studio plugin workaround (README section) keeps working as documented.

## Vertical slice (core flow, top to bottom)

Inbound request with `Authorization: Bearer <app-key>` on `/v1/chat/completions`
→ key lookup (`InboundKeys`) → per-key rate-limit partition (429 + `Retry-After` when over budget)
→ alias authorization (model ∈ key.Aliases, or key's single alias when model omitted) → alias
policy resolution (PromptMode, UpstreamModel pin, ModelPrefer, AttemptTimeoutSeconds) → existing
candidate build + failover engine → upstream → relay.
Walking skeleton: one authenticated `news-digest`-style Passthrough request end-to-end through
TestHost/FakeUpstream, then fan out (auth edges, prompt modes, pin/prefer/timeout, infra files).

Target config shape (sketch — PRD finalizes names):

```jsonc
"Proxy": {
  "InboundKeys": {
    // 32-byte CSPRNG values. Two live keys may share an App (rotation without downtime).
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

## Constraints

- Shared NVIDIA NIM budget (~40 RPM free tier) across all consumers + local LM Studio; batch
  staggering is the consumers' responsibility, not the proxy's. (Submitting the free 40→200 RPM
  upgrade application is worth doing regardless.)
- App Service F1: no Always On (cold starts after ~20 min idle — mitigated by the keep-warm
  workflow, every consumer tolerates a slow first call), ~60 CPU-min/day, ~165 MB/day egress
  (all consumers are non-streaming JSON — trivially within budget).
- Secrets: NVIDIA key + inbound app keys live in App Service settings (env vars; `ApiKeyEnv`
  already supports this) locally in `appsettings.Local.json`. Never committed. The personal
  `system-prompt.md` never deploys.
- Key hygiene (the security budget for going internet-facing, in place of a token flow): 32 bytes
  of CSPRNG entropy per key; key material never written to logs or error responses (log the `App`
  name — the key record already carries one); two live keys per app supported so rotation is a
  deploy, not an outage; per-key request budgets summing under NIM's ~40 RPM.
- Rate limiting uses .NET 10's in-box `AddRateLimiter`, partitioned by inbound key. Unconfigured =
  no limiting, so the local no-keys path is unaffected.
- Publish self-contained linux-x64 (repo targets .NET 10; avoids platform runtime-availability wobble).
- Deploy: Bicep in `infra/`, GH Actions OIDC (no long-lived publish-profile secret; one-time
  `az` federated-credential setup script provided), region swedencentral (westeurope fallback if
  F1 quota is refused). Keep-warm: scheduled workflow curls `/health` every ~10 min.
- Own apps only — not a public service (abuse surface + NIM ToS posture).
- Domain language: keep the repo's vocabulary — "alias", "candidate", "bench/cooldown",
  "prefer bias", "pin".

## Edge cases & failure modes

- All candidates benched (cooldown) → existing never-dead-end fallback already ignores cooldowns;
  unchanged for aliased requests.
- Quota exhausted / 429 storm → benches + eventual 502 `All candidate models failed`; every
  consumer already degrades (digest → source description, bca → fail-open no-veto, portfolio →
  error message in chat).
- `InboundKeys` referencing a nonexistent alias name → fail fast at startup (config typo must not
  become a runtime 400).
- Anchor mode, client sent multiple system messages → all preserved; anchor appended as one
  additional system message after the last of them.
- bca strict-JSON contract: `response_format: {type: json_object}` and all other body fields pass
  through untouched (only `model` is rewritten, plus messages per PromptMode).
- bca timing: 180s per attempt × up to `MaxAttemptsPerModel` means the *caller's* HttpClient
  timeout must exceed the worst case it wants to tolerate — a BlockchainAgent-repo note (its
  fail-open design covers the rest; not this repo's problem).
- Alias → provider with no API key configured → existing 401 `authentication_error` behavior stands.
- F1 cold start mid-first-request → several-second stall; acceptable to all consumers.
- Proxy-side rate limit hit → 429 + `Retry-After`. Consumers already handle this shape:
  ai-news-digest has 429-aware retry honouring `Retry-After` (GeminiSummarizer), BlockchainAgent
  fails open on any non-2xx. Portfolio's new backend must surface it as a friendly "busy, try
  again" rather than a stack trace.
- Proxy 429 (own budget) vs NIM 429 (upstream quota) are different conditions with the same status
  code — the proxy's own limiter must respond before any upstream call, so its 429 never benches a
  model in `RoutingState`.
- A leaked key → revoke by deleting that key record and redeploying app settings; the app's other
  live key keeps it running. Blast radius is one app and only its allowed aliases.

## Decisions log (resolved during grilling)

- Q: Topology — does LM Studio move to Azure? → A: **No. Azure = the apps only**; LM Studio stays
  on the local proxy; personal prompt never deploys; local no-keys behavior frozen.
- Q: What does an inbound key grant? → A: **Key = { App, Aliases[] }**; model must be one of the
  key's aliases (single-alias keys may omit model); 401 bad key, 400 disallowed model; no keys
  configured → auth off; **Production + no keys → refuse to start**.
- Q: Should apps get a JWT from a proxy `/auth/login` endpoint instead of sending a static key,
  now that this is internet-facing? → A: **No — static per-app bearer keys + per-key rate
  limiting.** Reasoning, recorded so it is not relitigated: this is machine-to-machine with no end
  user, so each app must hold a long-lived secret in config to obtain any token — the JWT flow adds
  a hop and a signing key without removing the secret, and the proxy would be both issuer and
  validator (signing tokens for itself, crossing no trust boundary). Concrete costs: a public
  unauthenticated login endpoint is the one surface an attacker can hit uncredentialed; a leaked
  signing key is worse than a leaked app key (mints tokens for every app and alias); scopes cached
  in a token delay revocation, whereas the key table is instant; ai-news-digest reaches the proxy
  through `OpenAIClient(new ApiKeyCredential(...))` registered once in DI, so a rotating token turns
  its config-only migration into a custom pipeline policy; and BlockchainAgent would gain a
  mandatory pre-flight round-trip (and a new failure mode) in front of a fail-open daily sentinel
  designed never to block. Every service this proxy imitates (OpenAI, Anthropic, NIM) uses static
  bearer keys — drop-in compatibility is the product.
- Q: If tokens are ever wanted, how? → A: **Entra ID client credentials** with the proxy as a pure
  resource server (`AddJwtBearer`, or zero-code via App Service Easy Auth, available on Free tier)
  — never a homegrown issuer. **Trigger to revisit:** the proxy serving anything that isn't the
  human's own apps, or end-user identity needing to reach the proxy. Until then it is ceremony.
- Q: Prompt policy per alias? → A: Modes **Passthrough / Anchor / Own**; unspecified → today's
  provider-level behavior. news-digest = Passthrough; portfolio = Anchor. (villa was Anchor —
  dropped entirely on 2026-07-19.) bca = Passthrough.
- Q: Alias model targeting on dynamic providers? → A: **Pin-first-then-failover**: `UpstreamModel`
  = exact id tried first, normal dynamic failover behind it; per-alias `ModelPrefer` replaces the
  provider bias for that request.
- Q: Hosting shape? → A: **F1 swedencentral + Bicep + GH Actions OIDC + keep-warm cron**;
  self-contained linux-x64; env-layered config (localhost Kestrel pin stays local-only).
- Q: Per-alias timeouts? → A: originally a non-goal; **promoted to in-scope** after the survey
  found BlockchainAgent needs 180s for Kimi reasoning vs the global 30s (which would silently
  fail healthy Kimi calls over to a fast non-reasoning model).
- Q: Which apps ride the proxy? → A: **news-digest (chat, Passthrough, small/fast), portfolio
  (chat, Anchor, big-model), bca (chat, Passthrough, Kimi pin, 180s)**. Villa: feature removed in
  its repo. Solar: stays Claude.
- Q: Embeddings (Villa RAG / portfolio secretary)? → A: **Out of proxy v1** — Villa's exit removed
  the main need; portfolio drops embeddings for all-docs/keyword context over its tiny corpus.
- Q: Alias name for the news app? → A: **`news-digest`** — it summarizes (GeminiSummarizer);
  ranking/categorization there is deterministic keyword code, not AI.
- Q: Production announce line? → A: `AnnounceModel: false` in the Production layer — the
  `_[model]_` prefix is an LM Studio nicety, not something portfolio visitors should see.

## Contradictions found vs the code

- "Extend ModelAliases with per-alias settings" (prior session) vs `BuildCandidatesAsync` ignoring
  `route.Models` on dynamic providers → the alias mechanism is partially inert today (the existing
  `fast` alias never routes to its configured model). → Resolution: pin-first-then-failover makes
  `UpstreamModel` meaningful; aliases become real routing profiles.
- Unknown model id → silent default-provider fallthrough (ProviderRegistry.Resolve) vs service
  mode needing strictness → Resolution: key-bound aliases; anything outside the key's set is a 400.
- "news-ranker ranks news" (original ask) vs the code: only summarization is LLM-backed; ranking
  is keyword rules → Resolution: alias named `news-digest`; small/fast model bias is correct.
- Prior session's "per-alias prompt policy + ModelPrefer suffices" vs BlockchainAgent's reality
  (180s non-streaming reasoning calls) → Resolution: per-alias `AttemptTimeoutSeconds` in scope.
- **This brief's own earlier draft** scoped per-key rate limiting out as a non-goal, reasoning that
  the consumers are the human's own cooperative apps. That holds for accidents, not for an
  internet-reachable URL where a leaked key can drain the NIM quota unattended. → Resolution:
  rate limiting moved into scope; it is where the "we're going public" security budget is spent.
- `appsettings.json` pins Kestrel to `localhost:5001` and `system-prompt.md` is neither published
  (csproj has no Content include) nor wanted on Azure → Resolution: Production config layer
  overrides binding, sets `AnnounceModel: false`, and references no prompt file; the startup
  fail-fast file check then never fires in prod.

## Consumer migration map (reference only — work happens in those repos, later)

- **ai-news-digest** (daily 05:00 UTC GH Actions batch): config-only swap — `Gemini:Endpoint` →
  `https://<app>.azurewebsites.net/v1/`, `Gemini:Model` → `news-digest`, `GEMINI_API_KEY` secret →
  the app's inbound proxy key. Keep the 8s pacing initially (shared 40 RPM). Its
  fallback-to-description already makes it retry-tolerant.
- **ChristofferSladinPortoflio**: build the stubbed React ChatWidget's backend (CF Pages Function
  or the Razor backend) → proxy with alias `portfolio`; key stays server-side, never shipped to the
  browser. Drop the re-embed-everything RAG; corpus is small enough for all-docs/keyword context in
  the prompt. Handle a 429 from the proxy as a friendly "busy, try again" message.
- **BlockchainAgent**: change the `Endpoint` const in `NvidiaProposer`/`KimiAnalyst` to the proxy
  URL, set `NVIDIA_MODEL=bca` (the model field carries the alias), swap `NVIDIA_API_KEY` to the
  inbound key. Consider raising its 180s HttpClient timeout to tolerate one proxy-side failover.
- **VillaKristina**: nothing here — the AI feature (RagService/AzureOpenAiStore stack) is being
  removed in that repo.
- **solar-m2m-platform**: nothing — ClaudeContractGenerator stays on the Anthropic API.
