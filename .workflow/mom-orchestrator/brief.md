# Brief: mom-orchestrator   (repo: LlmProxy, base: main)

## What the human asked (raw)
Turn the proxy into a Mixture-of-Models (MoM) orchestrator with cross-model
continuity, so uptime approaches 100% against NVIDIA NIM's per-model exhaustion.
In scope from the brainstorm:
1. **Model cooldown registry** — bench a model for N seconds after `200-err`/`429`
   so failover skips it instead of re-probing the wall.
3. **Learned capability map** — remember per-model whether tool calls are
   supported; route `tools`-containing requests only to tool-capable candidates.
4. **Identity / continuity anchor** — injected into the system prompt so model
   switches feel seamless and the "I'm Claude" confabulation stops.
6. **Heuristic MoM router** — conditional prefer-ordering by request shape
   (tools / huge-prompt / code-heavy → different candidate lists).

Explicitly OUT: #7 LLM-as-router / trained classifier / fan-out-judge (spends
too many requests against a scarce budget); #2 local RPM throttle (a hardcoded
~40 RPM is janky and varies per model / over time); #5 conversation fingerprint
+ rolling summary (dropped — see the reframe below).

## Current state vs desired (the diff)
- **Now:** the proxy is stateless between requests except for one in-memory
  `_lastGood` dict ([ProxyService.cs:21](../../ProxyService.cs)). Candidates are
  rebuilt per request in `BuildCandidatesAsync` and ordered only by
  `ModelCatalog.Rank` (ModelPrefer patterns, then instruct/chat, then rest). The
  system prompt is injected once *before* the candidate loop, so it can't reflect
  the winning model or add proxy-owned continuity text. Tool support is never
  tracked — a tool-incapable model is retried on every `tools` request. Failover
  reacts only to the current response (incl. the new `200-err` body-peek); a model
  that just failed is immediately eligible again. **There is no test project and
  no seam to fake the NVIDIA upstream.**
- **Want:** a per-request orchestration layer that (a) skips recently-exhausted
  models via a cooldown registry, (b) avoids routing `tools` requests to models
  learned to be tool-incapable, (c) injects a proxy-owned, model-agnostic identity/
  continuity anchor, and (d) biases candidate ordering by declarative rules keyed
  on request shape. All state in-memory. All behavior verifiable offline through a
  new xUnit project with a scripted fake upstream.

### Reframe that shaped the scope
Context does **not** evaporate between model calls: the chat API is stateless and
LM Studio resends the full history every request, which the proxy forwards to
whichever model wins. Basic continuity across a switch already works for free.
That removed the original motivation for feature 5 (summary) — its real jobs would
have been window-fitting and prompt-shrinking, neither of which is a felt problem
yet — so feature 5 is deferred.

## Acceptance criteria (testable)
All verified offline via the T0 fake upstream; `dotnet test` is the single gate.
- [ ] **T0 harness** — a fake `HttpMessageHandler` scripts upstream responses
      (200, 200-err, 429, tool-reject) into the `"upstream"` named client via test
      DI; one test proves the existing `200-err → 200` failover works offline.
      -- verified by: `dotnet test` (green, incl. the failover test)
- [ ] **Cooldown** — after model A returns `200-err`, a second request within
      `CooldownSeconds` sends **zero** calls to A and is answered by B; when *all*
      candidates are cooling down, the proxy ignores cooldowns and still attempts.
      -- verified by: xUnit asserting the fake handler received no A call on
      request 2, and a separate all-cooled-down test still gets a response.
- [ ] **Capability map** — a `tools` request where A returns a tool/function-
      referencing error demotes A; the next `tools` request skips A, but a
      **non-tools** request may still use A; if every candidate is known-incapable,
      a `tools` request still attempts (no dead-end).
      -- verified by: xUnit on candidate selection across the three cases.
- [ ] **Identity anchor** — the captured outgoing upstream body's system message
      contains the anchor text appended after the base prompt when `IdentityAnchor`
      is set, and omits it when blank.
      -- verified by: xUnit inspecting the captured upstream request body.
- [ ] **Routing rules** — a `hasTools` / `minChars` / `contentMatches` rule
      provably reorders which candidate is tried first.
      -- verified by: xUnit asserting first-attempted model per rule.

## Non-goals / out of scope
- #7 LLM-as-router / trained classifier / fan-out-and-judge.
- #2 local RPM throttle / any hardcoded per-model rate ceiling.
- #5 conversation fingerprint + rolling summary / window-fitting compression.
- Cross-*conversation* topic memory (persistent memory between chats).
- Any on-disk / DB state — everything in-memory for v1.
- Persisting the capability map across restarts (optimistic re-learn is fine).
- Regressing or reworking the streaming peek/relay + `200-err` failover just built.

## Blast radius
- **Fair game:** `ProxyService.cs`, `ProxyOptions.cs`, `ModelCatalog.cs`
  (candidate ordering), `ProviderRegistry.cs`, `Program.cs` (DI), `appsettings.json`,
  a new `LlmProxy.Tests` project, `README.md`.
- **Off limits:** the streaming peek/relay logic (`PeekBodyAsync`/`ClassifyBody`/
  `RelayAsync` — must not regress), API-key/secrets handling, the *content* of
  `system-prompt.md` (the anchor is separate config, not an edit to that file).

## Vertical slice (core flow, top to bottom)
For a proxy with no UI, "the stack" is: **config (ProxyOptions) → orchestration
component / registry → candidate pipeline (`BuildCandidatesAsync`) → observable
behavior (which upstream model is called, response, announce line, logs) → xUnit
test through the public `ForwardJsonAsync` seam.** Each feature is one such
vertical slice. T0 threads the thinnest path: fake upstream wired into DI + one
passing failover test, establishing the seam every feature ticket plugs into.

## Constraints
- **Scarce request budget:** NVIDIA NIM free tier ~40 RPM (upgradeable, per-model,
  not an SLA). Nothing may multiply request count per user turn — rules out
  hedging/ensembles/LLM-routing. Orchestration must be decided from local state.
- **Provider-agnostic:** no hardcoded per-model magic numbers (windows, RPM). New
  providers (e.g. opencode later) must slot in via config, not code.
- **In-memory only, concurrency-safe:** follow the existing `_lastGood`
  `ConcurrentDictionary` pattern; the proxy is a singleton serving concurrent
  requests.
- **Backward-compatible defaults:** empty `RoutingRules` = today's ordering;
  cooldown/capability are safe pure-wins; `IdentityAnchor` ships on with a default
  string but can be blanked to disable.
- **Stack:** C# / .NET 10, ASP.NET minimal API, `System.Text.Json.Nodes`, xUnit.

## Edge cases & failure modes
- All candidates cooling down → ignore cooldowns, attempt anyway (never dead-end).
- All candidates known tool-incapable on a `tools` request → attempt anyway.
- `200-err` and `429` both feed the cooldown registry (both are exhaustion signals).
- Silence ≠ incapable: a model answering a `tools` request in prose without calling
  a tool must NOT be demoted (only an explicit tool/function error demotes).
- `RoutingRules` reorder is a **soft** prefer bias; the tool-capability filter is a
  **hard** exclusion — they compose (filter first, then reorder within survivors).
- Cooldown must not permanently shrink the pool: entries expire by timestamp.
- Anchor injection must not break when the provider has no base system prompt.

## Decisions log (resolved during grilling)
- Q: Given continuity already works, what triggers the rolling summary (feature 5)?
  → A: **Drop feature 5 for now** (revisit if real window-limit failures appear).
- Q: How do night agents verify routing features (no test project today)?
  → A: **xUnit + fake upstream (T0)** — scripted `HttpMessageHandler`, deterministic
  offline assertions; becomes the seam all tickets plug into.
- Q: Where does new routing state live?
  → A: **All in-memory** (`ConcurrentDictionary`), reset on restart.
- Q: What signal demotes a model to tool-incapable?
  → A: **Explicit upstream error** referencing tools/function calling on a request
  that carried `tools`; unknown = optimistically capable; silence ≠ incapable.
- Q: How is the identity anchor implemented?
  → A: **Generic, proxy-owned config** (`IdentityAnchor` string appended after the
  provider system prompt; model-agnostic; no per-attempt re-injection).
- Q: How is the heuristic router expressed?
  → A: **Declarative `RoutingRules` config** — ordered rules matching
  `hasTools` / `minChars` / `contentMatches`, contributing prefer-pattern lists;
  new conditions in code, new policies in config.
- Q: feature-slug? → A: **mom-orchestrator**.
- Q: Cooldown default / trigger? → A: configurable `CooldownSeconds` (default 60s),
  triggered on `200-err` and `429`; never dead-ends.

## Contradictions found vs the code
- Brainstorm framed feature 5 as fixing "context evaporating between models," but
  the code forwards the client's full resent history to every candidate
  ([ProxyService.cs:104-107](../../ProxyService.cs)) — continuity already holds.
  → Resolution: feature 5 dropped; anchor (4) handles the *felt* discontinuity
  (voice/identity), not context loss.
- Anchor as "You are {model}" assumes the model is known at injection time, but the
  system prompt is injected before the candidate loop
  ([ProxyService.cs:69](../../ProxyService.cs)). → Resolution: model-agnostic anchor
  string, no per-model substitution; the announce line already names the winner.
- Feature 3 ("route tools only to tool-capable") and feature 6 ("prefer-ordering")
  read as one knob, but capability is a **hard filter** and routing is **soft
  ordering**. → Resolution: implemented as two composing mechanisms.
