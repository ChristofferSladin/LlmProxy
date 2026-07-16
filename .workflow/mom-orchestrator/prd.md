# PRD: mom-orchestrator

## Problem statement
The proxy fronts NVIDIA NIM's free tier, whose per-model worker pools saturate
under bursty use (`ResourceExhausted … (48/48)`). Failover across models now
works, but the proxy is otherwise memoryless per request: it re-probes a model
that just failed, it re-sends `tools` requests to models that can't do tool calls,
it lets whichever model answers confabulate its identity ("I'm Claude 4 Sonnet"),
and it orders candidates the same way regardless of what the request actually
needs. The result is wasted attempts against a scarce request budget and a
disjointed multi-model experience.

## Solution
Add a thin, in-memory orchestration layer that decides *which* model serves each
request from local state — never by spending extra upstream calls. Four capabilities:

1. **Cooldown registry** — a model that returns `200-err` or `429` is benched for a
   configurable window; candidate building skips benched models so failover doesn't
   walk back into the same wall. Never dead-ends: if everything is cooling down, it
   tries anyway.
2. **Learned tool-capability map** — when a request carrying `tools` draws an
   explicit tool/function error from a model, that model is remembered as
   tool-incapable; future `tools` requests skip it. Unknown models are optimistically
   capable; a model merely *not calling* a tool is never demoted.
3. **Identity / continuity anchor** — a proxy-owned, model-agnostic instruction
   appended to the system prompt that tells the model it is one of several open
   models served behind a failover proxy, to maintain continuity across turns, and
   not to claim to be a specific commercial model.
4. **Declarative heuristic router** — an ordered rule set matching on request shape
   (`hasTools`, `minChars`, `contentMatches`) that biases candidate prefer-ordering,
   so tool requests, huge prompts, and code-heavy prompts each favour suitable models.

All state is in-memory and concurrency-safe (the existing `_lastGood` pattern).
Everything is verifiable offline through a new xUnit project that scripts the
upstream via a fake message handler.

## User stories
1. As a heavy LM Studio user, I want the proxy to stop retrying a model that just
   hit its worker limit, so my next prompt is answered by a healthy model instead
   of failing again.
2. As a user in an agent/tool loop, I want `tools` requests routed only to models
   that actually support tool calls, so the loop doesn't break on a model that
   ignores or rejects `tools`.
3. As a user, I want the model to stop pretending to be Claude/GPT and to behave
   consistently as models swap underneath a conversation, so a long chat feels like
   one coherent assistant.
4. As the proxy operator, I want to express routing policy (tools/big-prompt/code →
   preferred models) in config, so I can tune it without recompiling and add new
   providers later without touching code.
5. As the operator, I want a cooled-down or incapable pool to never leave a request
   unanswered, so availability never regresses below today's behavior.
6. As a night agent / future maintainer, I want every routing decision covered by
   deterministic offline tests, so behavior can be verified without hitting live
   NVIDIA.

## Acceptance criteria (definition of done)
- [ ] **T0 harness** — a fake upstream message handler scripts responses (200,
      200-err, 429, tool-reject) into the `"upstream"` named client via test DI; one
      test proves the existing `200-err → 200` failover works offline.
      -- verified by: `dotnet test` green, including the failover test.
- [ ] **Cooldown** — after model A returns `200-err`, a second request within the
      cooldown window sends **zero** calls to A and is answered by B; a `429` feeds
      the registry the same way; when *all* candidates are cooling down the proxy
      ignores cooldowns and still returns a response.
      -- verified by: xUnit asserting no A call on request 2, and an all-cooled-down
      test that still gets answered.
- [ ] **Capability map** — a `tools` request in which A returns a tool/function-
      referencing error demotes A; the next `tools` request skips A; a **non-tools**
      request may still use A; if every candidate is known-incapable, a `tools`
      request still attempts (no dead-end).
      -- verified by: xUnit across all four cases.
- [ ] **Identity anchor** — the captured outgoing upstream body's system message
      contains the anchor appended after the base prompt when `IdentityAnchor` is
      set, and omits it when blank; injection works whether or not the provider has
      a base system prompt.
      -- verified by: xUnit inspecting the captured upstream request body.
- [ ] **Routing rules** — a `hasTools` / `minChars` / `contentMatches` rule provably
      changes which candidate is tried first; an empty rule set reproduces today's
      ordering exactly.
      -- verified by: xUnit asserting first-attempted model per rule and the empty
      baseline.
- [ ] **No regression** — the streaming peek/relay + `200-err` classification behave
      as before.
      -- verified by: existing behavior covered by a test in the new suite.

## Deep-module map
- **RoutingState** — interface: `RegisterCooldown(model)`, `IsCoolingDown(model)`,
  `MarkToolIncapable(model)`, `IsToolCapable(model)`; hides: the concurrent
  timestamp/flag dictionaries, expiry-by-timestamp, and the "never dead-end"
  fallback bookkeeping. A single in-memory component owning all new per-model
  memory. tested: yes — the orchestrator's whole value is here.
- **RequestClassifier** — interface: `Classify(requestBody) → {hasTools, charCount,
  matchedPatterns}`; hides: JSON traversal of the messages/tools arrays and content
  concatenation. tested: yes — pure, cheap, high-leverage.
- **RoutingRuleSet** — interface: `PreferOverride(classification) → prefer-patterns`;
  hides: ordered rule evaluation and composition of matches. tested: yes.
- **CandidatePipeline** (extension of today's `BuildCandidatesAsync` + catalog
  ordering) — interface: `Build(route, classification) → ordered candidate ids`;
  hides: how cooldown skipping, hard tool-capability filtering, soft rule-based
  reordering, `_lastGood` stickiness, and the never-dead-end fallbacks combine into
  one ordered list. tested: yes — the integration point.
- **PromptComposer** (extension of today's system-prompt injection) — interface:
  `Compose(providerSystemPrompt, anchor) → systemMessage`; hides: append semantics
  and the empty-base / empty-anchor cases. tested: yes (small).
- **FakeUpstream (test)** — interface: `Enqueue(match, status, body)` +
  captured-request inspection; hides: an `HttpMessageHandler` and request log wired
  into the `"upstream"` named client via test DI. tested: it *is* the test seam.

## Data model / schema changes
No database. New in-memory structures on the proxy singleton (reset on restart):
- cooldown map: model id → cooldown-until timestamp.
- capability map: model id → tool-capable flag (absent = optimistically capable).

New configuration under `Proxy` (all backward-compatible defaults):
- `CooldownSeconds` (default 60) — bench duration after `200-err`/`429`.
- `IdentityAnchor` (string, ships with a sensible default; blank disables) — appended
  after the provider system prompt.
- `RoutingRules` — ordered list; each rule has a `when` (`hasTools` bool,
  `minChars` int, `contentMatches` string list) and a `prefer` pattern list. Empty
  list = today's ordering.

## Vertical slices (preview of tickets)
- **T0 walking skeleton** — introduce `LlmProxy.Tests` (xUnit), the FakeUpstream
  seam wired into DI, and one green test asserting the existing `200-err → 200`
  failover. Establishes how every later ticket observes routing decisions offline.
- **T1 cooldown registry** — config → `RoutingState` cooldown → skip in candidate
  build → never-dead-end fallback → tests. (blocks on T0)
- **T2 tool-capability map** — config-free learning → classify `hasTools` → demote
  on tool-referencing error → hard-filter incapable on `tools` requests →
  never-dead-end → tests. (blocks on T0)
- **T3 identity anchor** — `IdentityAnchor` config → `PromptComposer` append →
  captured-body assertion tests. (blocks on T0)
- **T4 declarative router** — `RoutingRules` config → `RequestClassifier` +
  `RoutingRuleSet` → soft prefer-override composed with the hard tool filter →
  first-attempted-model tests + empty-baseline test. (blocks on T0; composes with T2)

## Constraints & standing rules
- **Never multiply upstream calls per user turn** — all orchestration decided from
  local state; no hedging, ensembles, or LLM-based routing.
- **No hardcoded per-model magic numbers** (windows, RPM); providers slot in via
  config. New provider (e.g. opencode) must need no code change to route.
- **In-memory, concurrency-safe** — the proxy is a singleton under concurrent load.
- **Backward-compatible** — empty `RoutingRules` = today's ordering; cooldown and
  capability are pure availability wins; `IdentityAnchor` is the one intentional
  behavior change (on by default, blankable).
- **Never dead-end** — cooldown skipping and tool filtering must always fall through
  to trying *something* rather than erroring on an empty pool.
- **Stack:** C# / .NET 10, ASP.NET minimal API, `System.Text.Json.Nodes`, xUnit.

## Out of scope (non-goals)
- #7 LLM-as-router / trained classifier / fan-out-and-judge.
- #2 local RPM throttle / any hardcoded rate ceiling.
- #5 conversation fingerprint + rolling summary / window-fitting compression.
- Cross-conversation topic memory.
- On-disk / DB state; persisting the capability map across restarts.
- Reworking or regressing the streaming peek/relay + `200-err` failover.

## Risks & open questions
- **Tool-error detection is provider-specific** (message wording varies). → Mitigate
  with a narrow, case-insensitive match on tool/function-calling phrasing on a
  request that carried `tools`; optimistic default means a missed match only costs
  one wasted attempt, never a false demotion of a capable model.
- **Cooldown could shrink the pool under sustained load.** → Timestamp expiry +
  the all-cooled-down fallback guarantee a request is always attempted.
- **`contentMatches` on large bodies** could be costly. → Classify once per request
  over concatenated message content; keep matching simple substring/pattern checks.
- **Testing observability** — `ForwardJsonAsync` writes to `HttpContext` and returns
  void. → T0 must establish how tests read the answered model (response body /
  announce line / captured upstream request), and later tickets reuse that.

## Blast radius
- **Fair game:** `ProxyService.cs`, `ProxyOptions.cs`, `ModelCatalog.cs` (ordering),
  `ProviderRegistry.cs`, `Program.cs` (DI), `appsettings.json`, new `LlmProxy.Tests`
  project, `README.md`.
- **Off limits:** the streaming peek/relay logic (`PeekBodyAsync` / `ClassifyBody` /
  `RelayAsync` — must not regress), API-key/secrets handling, the *content* of
  `system-prompt.md`.
