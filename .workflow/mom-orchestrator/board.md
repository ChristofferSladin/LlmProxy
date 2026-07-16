## Board: mom-orchestrator   (base: main)

| id | title                                   | type | status | blocked-by |
|----|-----------------------------------------|------|--------|------------|
| T0 | test harness + seams (walking skeleton) | afk  | todo   | -          |
| T1 | cooldown registry                       | afk  | todo   | T0         |
| T2 | learned tool-capability map + filter    | afk  | todo   | T1         |
| T3 | identity / continuity anchor            | afk  | todo   | T0         |
| T4 | declarative heuristic router            | afk  | todo   | T2         |

**DAG / ready-set flow**

```
T0 ──┬──> T1 ──> T2 ──> T4
     └──> T3
```
- After **T0** terminal: ready-set = {**T1**, **T3**} run in parallel — T1 edits the
  candidate-pipeline region of `ProxyService`, T3 edits the disjoint system-prompt
  injection region, so their worktrees merge cleanly.
- **T2** waits on T1, **T4** waits on T2: all three touch the same
  `BuildCandidatesAsync` / error-branch region of `ProxyService` and the same
  `RoutingState` / `RequestClassifier` files, so they are sequenced to keep the
  ready-set collision-free (and T4 composes with T2's hard tool filter).
- **T3** is independent after T0 and may finish any time.

All spine edits that every feature would otherwise share — the new `Proxy` config
properties, the `RoutingState` DI registration, and injecting `RoutingState` into
`ProxyService`'s constructor — are folded into **T0** so feature tickets plug into
existing seams instead of racing the DI container / options class.

---

### T0 -- test harness + seams (walking skeleton)
- **type:** afk
- **status:** todo
- **blocked-by:** -
- **module:** FakeUpstream test seam (scripted `HttpMessageHandler` + captured-request
  log wired into the `"upstream"` named client) and a `TestHost` that constructs a
  real `ProxyService` over fakes; plus the empty seams every feature plugs into —
  a DI-registered `RoutingState` component (skeleton interface, no logic) injected
  into `ProxyService`, and the unused `Proxy` config surface (`CooldownSeconds`,
  `IdentityAnchor`, `RoutingRules` + rule types).
- **slice:** new `LlmProxy.Tests` xUnit project → `FakeUpstream` handler that matches
  on the request's `model` field and returns scripted `(status, body)` while logging
  every outgoing request → `TestHost` helper that builds `ProxyService` with a fake
  `IHttpClientFactory`, in-memory `IOptions<ProxyOptions>`, `ProviderRegistry`,
  `ModelCatalog`, and the new `RoutingState` → drive `ForwardJsonAsync` with a
  `DefaultHttpContext`, read the answered model from the captured upstream requests
  and the response body → one green test asserting the existing `200-err → 200`
  failover (deepseek scripted to a ResourceExhausted 200 body, llama scripted to a
  good completion; assert llama answered).
- **acceptance-check:** `dotnet test --filter FullyQualifiedName~FailoverTests` -> green
- **files-likely-touched:** `LlmProxy.Tests/LlmProxy.Tests.csproj` (new, ProjectReference
  to `LlmProxy.csproj`), `LlmProxy.Tests/FakeUpstream.cs` (new), `LlmProxy.Tests/TestHost.cs`
  (new), `LlmProxy.Tests/FailoverTests.cs` (new), `ProxyOptions.cs` (add config props +
  `RoutingRule`/`RoutingWhen` types, unused), `RoutingState.cs` (new skeleton),
  `Program.cs` (register `RoutingState` singleton), `ProxyService.cs` (ctor-inject
  `RoutingState` field only).
- **decisions:** Confirmed `ForceModels` makes `BuildCandidatesAsync` return `route.Models` (the fixed chain) and skip the dynamic /v1/models fetch — `TestHost` pins `ForceModels:["deepseek","llama"]` so candidates are offline & deterministic. The test project nests under the web project, so its sources are removed from `LlmProxy.csproj`'s compile glob and it declares `FrameworkReference Microsoft.AspNetCore.App` (it's a non-Web SDK project) to see `HttpContext`/`IHttpClientFactory`. `FailoverTests.First_model_...` sets `MaxAttemptsPerModel=1` so the recorded chain is exactly `[deepseek, llama]` (no per-model retry noise) while still exercising the 200-err peek/classify/failover path.
- **notes:** Do NOT use `WebApplicationFactory` — construct `ProxyService` directly over
  fakes (chosen approach). Must not regress `PeekBodyAsync`/`ClassifyBody`/`RelayAsync`;
  the failover test exercises that path. Establishes how later tickets observe routing
  decisions: captured upstream request order/bodies + response body. PRD acceptance
  criterion: "T0 harness" and "No regression".

### T1 -- cooldown registry
- **type:** afk
- **status:** todo
- **blocked-by:** T0
- **module:** `RoutingState` cooldown half — `RegisterCooldown(model)` /
  `IsCoolingDown(model)` over a concurrent model→expiry-timestamp map; hides expiry
  and thread-safety. Consumed by the candidate pipeline.
- **slice:** `CooldownSeconds` config (default 60) → on a `200-err` peek result and on
  a `429` upstream status, call `RegisterCooldown(model)` → in `BuildCandidatesAsync`
  skip models still cooling down → if that empties the candidate list, ignore cooldowns
  and use the full ordered list (never dead-end) → tests: (a) after A returns `200-err`,
  request 2 within the window makes zero A calls and B answers; (b) a `429` benches A
  the same way; (c) all-cooled-down still returns a response.
- **acceptance-check:** `dotnet test --filter FullyQualifiedName~CooldownTests` -> green
- **files-likely-touched:** `RoutingState.cs` (implement cooldown), `ProxyService.cs`
  (register cooldown in the `200-err` + `429` branches; skip in `BuildCandidatesAsync`),
  `ProxyOptions.cs` (already has `CooldownSeconds` from T0 — read it), `appsettings.json`
  (document default), `LlmProxy.Tests/CooldownTests.cs` (new).
- **decisions:**
- **notes:** Cooldown skip is a filter on the *already-ordered* candidate list, applied
  before the `MaxDynamicCandidates` cap. PRD criterion: "Cooldown". Never-dead-end is
  mandatory.

### T2 -- learned tool-capability map + filter
- **type:** afk
- **status:** todo
- **blocked-by:** T1
- **module:** `RoutingState` capability half — `MarkToolIncapable(model)` /
  `IsToolCapable(model)` (absent = optimistically capable); plus `RequestClassifier`
  (new) exposing `hasTools` for a request body. Hides the demotion bookkeeping and the
  JSON probe for a non-empty `tools` array.
- **slice:** classify `hasTools` from the request body → when an upstream error (any
  status, or the `200-err` peek) on a `tools`-carrying request has a body matching
  tool/function-calling phrasing (case-insensitive), call `MarkToolIncapable(model)` →
  in `BuildCandidatesAsync`, when the request `hasTools`, hard-filter out known-incapable
  models → if that empties the list, ignore the filter (never dead-end) → tests:
  (a) A returns a tool-referencing error → A demoted → next `tools` request skips A;
  (b) a non-tools request may still use A; (c) a model answering a `tools` request in
  prose (no error) is NOT demoted; (d) all-incapable `tools` request still attempts.
- **acceptance-check:** `dotnet test --filter FullyQualifiedName~CapabilityTests` -> green
- **files-likely-touched:** `RoutingState.cs` (implement capability), `RequestClassifier.cs`
  (new, `hasTools`), `ProxyService.cs` (classify once per request; demote on
  tool-referencing error; hard-filter in candidate build), `LlmProxy.Tests/CapabilityTests.cs`
  (new).
- **decisions:**
- **notes:** Silence ≠ incapable — only an explicit tool/function error demotes. The hard
  filter composes with T1's cooldown skip (both applied before the cap). PRD criterion:
  "Capability map".

### T3 -- identity / continuity anchor
- **type:** afk
- **status:** todo
- **blocked-by:** T0
- **module:** `PromptComposer` — `Compose(providerSystemPrompt, anchor) → systemMessage`;
  hides append semantics and the empty-base / empty-anchor cases. Edits the disjoint
  system-prompt-injection region of `ProxyService` (not the candidate pipeline), so it is
  parallel-safe with T1.
- **slice:** `IdentityAnchor` config (ships with the default anti-confabulation +
  continuity string; blank disables) → at system-prompt injection, append the anchor after
  the provider's base prompt via `PromptComposer` → tests: (a) with `IdentityAnchor` set,
  the captured upstream body's system message ends with the anchor after the base prompt;
  (b) with it blank, the system message is unchanged from today; (c) with no provider base
  prompt, the anchor still becomes the system message.
- **acceptance-check:** `dotnet test --filter FullyQualifiedName~IdentityAnchorTests` -> green
- **files-likely-touched:** `PromptComposer.cs` (new), `ProxyService.cs` (call composer in
  the injection block ~ the existing system-message replacement), `ProxyOptions.cs` (already
  has `IdentityAnchor` from T0 — read it; set the default string), `appsettings.json`
  (document the default), `LlmProxy.Tests/IdentityAnchorTests.cs` (new).
- **notes:** Model-agnostic — no `{model}` substitution (injection precedes the candidate
  loop; the announce line already names the winner). Off limits: editing `system-prompt.md`
  content. PRD criterion: "Identity anchor".

### T4 -- declarative heuristic router
- **type:** afk
- **status:** todo
- **blocked-by:** T2
- **module:** `RequestClassifier` extension (`charCount`, `contentMatches`) + `RoutingRuleSet`
  — `PreferOverride(classification) → prefer-patterns` over the ordered `RoutingRules`;
  hides rule evaluation/composition. Feeds a soft prefer-override into the candidate
  ordering, composed with (not replacing) T2's hard tool filter.
- **slice:** `RoutingRules` config (ordered; each `when` = `hasTools`/`minChars`/
  `contentMatches`, each `prefer` = pattern list) → extend `RequestClassifier` with
  char count over concatenated message content and `contentMatches` → `RoutingRuleSet`
  produces a per-request prefer override → apply it as the ordering bias in candidate
  build (over the provider's static `ModelPrefer`) while cooldown-skip + tool-filter
  still apply → tests: (a) a `hasTools` rule makes a preferred model first-attempted;
  (b) a `minChars` rule reorders on a large prompt; (c) a `contentMatches` rule reorders
  on code-like content; (d) empty `RoutingRules` reproduces today's ordering exactly.
- **acceptance-check:** `dotnet test --filter FullyQualifiedName~RoutingRulesTests` -> green
- **files-likely-touched:** `RequestClassifier.cs` (extend), `RoutingRuleSet.cs` (new),
  `ProxyService.cs`/`ModelCatalog.cs` (apply the per-request prefer override in ordering),
  `ProxyOptions.cs` (already has `RoutingRules` + types from T0), `appsettings.json`
  (example rules, commented/empty by default), `LlmProxy.Tests/RoutingRulesTests.cs` (new).
- **decisions:**
- **notes:** Soft reorder only — must not exclude candidates (that's the tool filter's job)
  and must not multiply upstream calls. Empty-baseline test guards backward-compat. PRD
  criterion: "Routing rules".
