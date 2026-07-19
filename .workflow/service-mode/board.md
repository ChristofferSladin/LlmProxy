# Board: service-mode   (base: main)

Spine rule for this board: **`ProxyOptions.cs`, `ProxyService.cs`, and `PromptComposer.cs` are
edited by T0a only; `Program.cs` is edited by T0b then T0c (sequenced by blocked-by).** T0a
establishes the seams (config surface, effective-policy flow, prompt-application hook, candidate-
seed hook) with behaviour-identical defaults; T0b establishes the integration harness; T0c wires
the pipeline. Feature tickets fill in their own module file + tests and do **not** touch spine
files. That is what makes every ready-set collision-free.

The skeleton is split into three gates so a failure is localised before anything stacks on it:
T0a's gate proves the refactor is safe (old tests unmodified), T0b's gate proves the harness
works, T0c's gate proves the wiring works.

| id  | title                                                         | type | status | blocked-by |
|-----|---------------------------------------------------------------|------|--------|------------|
| T0a | effective-policy seam over the forwarding path (behaviour frozen) | afk | done | -        |
| T0b | integration harness over the real pipeline                    | afk  | done   | -          |
| T0c | pipeline wiring: auth happy path + limiter/validation seams   | afk  | done   | T0a, T0b   |
| T1  | inbound authority edges (401/400, omission, rotation, no-leak)| afk  | done   | T0c        |
| T2  | per-application rate limiting                                 | afk  | done   | T0c        |
| T3  | prompt modes: passthrough + anchor (own preserved)            | afk  | done   | T0a        |
| T4  | alias model targeting: pin-first + per-alias prefer           | afk  | done   | T0a        |
| T5  | per-alias attempt timeout                                     | afk  | done   | T0a, T4    |
| T6  | startup validation (prod-requires-keys, config consistency)   | afk  | done   | T0c        |
| T7  | production configuration layer                                | afk  | done   | -          |
| T8  | infrastructure definition (Bicep, F1, params)                 | afk  | done   | -          |
| T9  | deploy + keep-warm workflows (OIDC)                           | afk  | done   | T8         |
| T10 | README: service-mode operations + migration map               | afk  | done   | T1, T2, T3, T4, T5, T6 |
| T11 | deployed smoke: script + runbook (human executes)             | hitl | done   | T6, T7, T9 |

DAG (arrows = blocked-by):

```
  T0a ──┬──────────────► T3 ─────────────────► T10
        ├──────────────► T4 ──► T5 ──────────►(T10)
        └──► T0c ──┬───► T1 ─────────────────►(T10)
  T0b ──────┘      ├───► T2 ─────────────────►(T10)
                   └───► T6 ─────────────────►(T10, T11)
  T7 ────────────────────────────────────────► T11
  T8 ──► T9 ─────────────────────────────────► T11
```

Ready-set at start: **T0a, T0b, T7, T8** (disjoint files). After T0a: **+T3, +T4**. After
T0a+T0b: **+T0c**. After T0c: **+T1, +T2, +T6**.

---

### T0a -- effective-policy seam over the forwarding path (behaviour frozen)
- **type:** afk
- **status:** done
- **blocked-by:** -
- **module:** the extended options surface + alias policy resolution (`AliasPolicy.cs`) — one
  `EffectivePolicy` record (prompt mode, pinned model, prefer patterns, attempt timeout) consumed
  by the forwarding path; hides the alias→provider→global precedence chain. Pure/static like
  `RoutingRuleSet` — constructed inline, no DI registration, so `Program.cs` is untouched.
- **slice:** `ProxyOptions` gains the FULL new surface, inert by default (`InboundKeys` records
  with App/Aliases/RequestsPerMinute; alias PromptMode/ModelPrefer/AttemptTimeoutSeconds) →
  `AliasPolicy.Resolve` returns today's defaults for every unset field → `ProxyService` reads
  timeout, candidate seed (null pin = no-op), and prompt application from the policy →
  message mutation moves behind `PromptComposer.Apply(mode, ...)` with mode `Own` reproducing
  today's strip-and-inject exactly.
- **acceptance-check:** `dotnet test LlmProxy.Tests` → ALL pre-existing tests green **with zero
  modifications to existing test files** + new `AliasPolicyTests` green (unset alias fields
  resolve to provider/global values; full precedence chain covered).
- **files-likely-touched:** `ProxyOptions.cs`, `ProxyService.cs`, `PromptComposer.cs`, new
  `AliasPolicy.cs`, new `LlmProxy.Tests/AliasPolicyTests.cs`.
- **decisions:** `ModelAlias.UpstreamModel` **reused** (not forked into a second field) for the
  future dynamic-provider pin, but `AliasPolicy.Resolve` hardcodes `EffectivePolicy.UpstreamModel
  = null` unconditionally in T0a regardless of the alias's value — asserted by a dedicated tripwire
  test (`Alias_upstream_model_pin_stays_absent_in_T0a_regardless_of_alias_value`) so **T4 must
  delete/update that specific test when it wires pin-first-then-failover**, making the intended
  behaviour change impossible to land silently. `ModelPrefer`/`AttemptTimeoutSeconds` DO compute
  real alias→provider/global precedence already (no legacy meaning to conflict with, so correct
  data costs nothing now and both are inert until an alias sets them). `PromptComposer.Apply`'s
  `Anchor` branch throws `NotSupportedException` — deliberately unreachable in T0a (no alias sets
  non-Own mode yet); **T3 replaces this throw with the real implementation.** `Program.cs`
  untouched. Commit `438a625` on `worktree-agent-ad394954a5aaccaa8`. Full suite: 27/27 green
  (20 pre-existing unmodified + 7 new `AliasPolicyTests`).
- **notes:** PRD §Deep-module map (alias policy resolution, prompt composition). This is the
  refactor ticket: if any existing test needs editing, the seam leaked — stop and flag. No
  behaviour change is permitted here, including the pin fix (that lands in T4).

---

### T0b -- integration harness over the real pipeline
- **type:** afk
- **status:** done
- **blocked-by:** -
- **module:** integration harness (`LlmProxy.Tests/IntegrationHost.cs`) — start the real
  application over the scripted `FakeUpstream` and drive HTTP at it; hides host construction,
  per-test configuration injection, upstream substitution, and log capture.
- **slice:** `Microsoft.AspNetCore.Mvc.Testing` package + `public partial class Program` (the one
  `Program.cs` line this ticket owns) → `WebApplicationFactory` wiring that swaps the upstream
  `HttpClient` for `FakeUpstream` and injects config per test → smoke: an unauthenticated chat
  request through the REAL pipeline reaches the scripted upstream and returns 200 (today's open
  behaviour); `/health` answers 200.
- **acceptance-check:** `dotnet test LlmProxy.Tests --filter "FullyQualifiedName~IntegrationSmokeTests"` → green,
  AND `dotnet test LlmProxy.Tests` → full suite green.
- **files-likely-touched:** `Program.cs` (one line), `LlmProxy.Tests/LlmProxy.Tests.csproj`, new
  `LlmProxy.Tests/IntegrationHost.cs`, new `LlmProxy.Tests/IntegrationSmokeTests.cs`.
- **decisions:** Full `IHttpClientFactory` singleton replacement (not per-client handler swap) —
  avoids HttpClientFactory's handler-lifetime rotation disposing a shared handler mid-test. Config
  injection via `ConfigureAppConfiguration` + `AddInMemoryCollection` as the last source (overrides
  file config); `IntegrationHost.DefaultConfig()` gives a ready-made minimal config so tests don't
  depend on the personal `system-prompt.md` or a live catalog fetch. Log capture exposed as a flat
  `LogLines : List<string>` via a tiny custom `ILoggerProvider` — minimal on purpose, for T1's
  no-key-leak test to consume. Host explicitly pinned to `Development` environment so it won't trip
  T6's future prod fail-fast check. Commit `4891424` on `worktree-agent-a8d48788e59cca914`.
  Full suite: 22/22 green (20 pre-existing + 2 new), zero existing tests modified.
- **notes:** PRD §Testing strategy — this harness is new ground in the repo and everything
  auth/rate-limit shaped depends on it; proving it against TODAY's app (no service-mode features
  needed) is the point of splitting it out. Log capture (for T1's no-leak test) belongs here.

---

### T0c -- pipeline wiring: auth happy path + limiter/validation seams
- **type:** afk
- **status:** done
- **blocked-by:** T0a, T0b
- **module:** `Program.cs` pipeline composition — auth middleware scoped to the versioned API
  paths, rate limiter registered (delegating to a no-op stub partition), startup-validation call
  site (no-op routine), DI unchanged otherwise.
- **slice:** config with one key granting one alias → keyed request resolves app + alias into the
  request context → forwarding path routes via the alias → scripted upstream → 200. No keys
  configured → request without a header → 200 (open, local behaviour). Health stays outside the
  auth scope by construction.
- **acceptance-check:** `dotnet test LlmProxy.Tests --filter "FullyQualifiedName~ServiceModeSkeletonTests"` → green,
  AND `dotnet test LlmProxy.Tests` → full suite green.
- **files-likely-touched:** `Program.cs`, new `InboundAuth.cs` (happy path only), new
  `RateLimitPartition.cs` (no-op stub), new `StartupValidation.cs` (empty routine + call site),
  new `LlmProxy.Tests/ServiceModeSkeletonTests.cs`.
- **decisions:** Auth middleware scoped via `app.Use` conditional on
  `Path.StartsWithSegments("/v1")`, registered before endpoint mapping — `/` and `/health` never
  reach `InboundAuth.TryResolve`. Resolved caller stashed on `HttpContext.Items[InboundAuth.
  CallerItemKey]` only when a key actually resolves (constant key — **T1 must reuse this
  convention, not invent its own**). `TryResolve` is a narrow `bool` seam — T0c only distinguishes
  allow/deny; **T1 owns all rejection-shape distinctions** (malformed/wrong/missing-when-configured).
  **No rate limiter registered at all** (not even a stub) — avoids prejudging T2's registration
  shape; `RateLimitPartition.KeyFor(ResolvedCaller)` exists as the agreed by-`App` partitioning
  rule for T2 to consume. Commit `8401da4` on `nightshift/t0c`. Full suite: 34/34 green
  (5 new `ServiceModeSkeletonTests` + 29 carried from T0a+T0b).
- **notes:** Happy path ONLY — rejection shapes are T1, real partitioning is T2, real validation
  is T6. This ticket exists so those three fan out onto proven wiring without touching
  `Program.cs` again.

---

### T1 -- inbound authority edges
- **type:** afk
- **status:** done
- **blocked-by:** T0c
- **module:** inbound authority (`InboundAuth.cs`) — "who is this caller, may they use this alias";
  hides bearer parsing, rotation (N keys → one app), omission rules, and rejection-message
  construction that never echoes key material.
- **slice:** wrong/missing/malformed key → 401 in the existing error envelope; `/` + `/health`
  reachable unkeyed; valid key + out-of-grant alias → 400 naming the allowed aliases; single-grant
  key may omit `model`, multi-grant omission → 400; two live keys, same app, both accepted; log
  capture proves key material absent from logs and bodies on every path.
- **acceptance-check:** `dotnet test LlmProxy.Tests --filter "FullyQualifiedName~InboundAuthTests"` → green.
- **files-likely-touched:** `InboundAuth.cs`, new `LlmProxy.Tests/InboundAuthTests.cs`.
- **decisions:** `TryResolve` returns a richer `InboundAuthResult` enum (`Open`/`Ok`/
  `NoKeyProvided`/`MalformedHeader`/`UnknownKey`) instead of bool; all three failure reasons
  collapse to the SAME generic 401 body deliberately, so a client can't probe for valid key shapes
  (the enum still lets tests/logs distinguish internally). Added `InboundAuth.CheckAliasGrant` as a
  separate pure method. **Alias-grant enforcement lives in `ProxyService.ForwardJsonAsync`, NOT in
  the `/v1/*` middleware** — the middleware never parses the body, and `ForwardJsonAsync` is the
  one call site with both the resolved caller and the parsed model already in hand; avoids a
  second JSON parse. **⚠ Touched `ProxyService.cs`**, outside this ticket's listed files, branched
  from T0c's (i.e. pre-T4-pin-first) version — **T4 independently modified `ProxyService.cs` too,
  from a different branch off T0a. These are two independent edits to the same file with no shared
  history past T0a; expect a real merge conflict at /merge-night, not a mechanical one.** `Program.
  cs` touch kept minimal (bool→enum switch only). Commit `b73b2a5` on `nightshift/t1`. Full suite:
  58/58 green (34 + 24 new); `InboundAuthTests` filter: 24/24.
- **notes:** PRD criteria: Authentication and authorization (all six). Unit-test the pure rules;
  integration-test the wire shapes (401 body, health reachability, log absence) via T0b's harness.

---

### T2 -- per-application rate limiting
- **type:** afk
- **status:** done
- **blocked-by:** T0c
- **module:** rate-limit partitioning (`RateLimitPartition.cs`) — request → partition + window;
  hides the app-not-key rule and unconfigured-means-unlimited.
- **slice:** key with `RequestsPerMinute` budget → burst past it → 429 + `Retry-After`; two keys of
  one app share one budget; app A exhausted ⇏ app B throttled; rejection happens before any
  upstream call (FakeUpstream captured zero requests) and benches nothing in `RoutingState`;
  unconfigured → unlimited.
- **acceptance-check:** `dotnet test LlmProxy.Tests --filter "FullyQualifiedName~RateLimitTests"` → green.
- **files-likely-touched:** `RateLimitPartition.cs`, new `LlmProxy.Tests/RateLimitTests.cs`.
- **decisions:** Hand-rolled fixed-window counter (`RateLimitCounter` in `RateLimitPartition.cs`)
  over .NET's `AddRateLimiter` — nullable per-partition budgets (unlimited when unset) are simpler
  as a ~30-line `ConcurrentDictionary`-backed counter than as a custom `PartitionedRateLimiter`,
  and it gives full control over the 429 body to match `ProxyService.WriteErrorAsync`'s existing
  envelope. `Retry-After` = ceiling of window-remaining, floored at 1s. Ordering (auth-before-limit)
  enforced by gating the limiter middleware on `HttpContext.Items[InboundAuth.CallerItemKey]` being
  present — absent (open path or non-`/v1` route) skips entirely, which doubles as the
  unconfigured-means-unlimited rule. **⚠ Touched `ProxyOptions.cs`** (new
  `RateLimitWindowSeconds`, default 60, lets tests compress the window) **and `Program.cs`**
  (additive second `app.Use` block after auth) — both outside this ticket's listed files.
  `ProxyOptions.cs` was independently touched by T4 (different branch off T0a — expect a
  three-way-mergeable field-addition conflict). `Program.cs` was independently touched by T1
  (same T0c ancestor, different regions of the file — likely but not guaranteed mechanical).
  Commit `ace1115` on `nightshift/t2`. Full suite: 39/39 green (34 + 5 new); `RateLimitTests`
  filter: 5/5, stable across 3 repeated runs.
- **notes:** PRD criteria: Rate limiting (all four). Partition by **application** — the
  two-keys-one-budget test is the regression pin for the auth-before-limiter pipeline ordering.
  Use compressed windows; no real-time sleeps beyond the window length.

---

### T3 -- prompt modes: passthrough + anchor
- **type:** afk
- **status:** done
- **blocked-by:** T0a
- **module:** prompt composition (`PromptComposer.cs`) — apply a mode to the client's message list;
  hides strip-vs-preserve, insertion position, multi-system-message and nothing-to-inject cases.
- **slice:** alias `PromptMode: Passthrough` → captured upstream messages byte-identical (none
  removed, none added, order preserved); `Anchor` → all client messages preserved + exactly one
  system message carrying the anchor after the client's last system message (first if none);
  unset mode → provider behaviour; `Own` → pre-existing `IdentityAnchorTests` still green
  unmodified; non-message body fields (response_format, temperature) survive every mode.
- **acceptance-check:** `dotnet test LlmProxy.Tests --filter "FullyQualifiedName~PromptModeTests"` → green,
  AND `dotnet test LlmProxy.Tests --filter "FullyQualifiedName~IdentityAnchorTests"` → green unmodified.
- **files-likely-touched:** `PromptComposer.cs`, new `LlmProxy.Tests/PromptModeTests.cs`.
- **decisions:** `Anchor` inserts one system message after the client's LAST system message (or as
  `messages[0]` if none) — tested explicitly for the multi-system-message case (appends after the
  last of 4). `Passthrough` was already a correct no-op from T0a's stub — left untouched, only
  tested. Anchor text threads through `Apply`'s existing parameter (T0a already wired it for `Own`)
  — no signature change needed. `IdentityAnchorTests.cs` confirmed zero diff via `git diff --stat`.
  Commit `fd290fe` on `nightshift/t3`. Full suite: 36/36 green (27 pre-existing + 9 new); filtered
  `IdentityAnchorTests`: 4/4 unmodified.
- **notes:** PRD criteria: Alias routing profiles (prompt-mode items + body-fields item). Tests
  drive the existing `TestHost` (no pipeline needed — aliases resolve in `ProviderRegistry`
  today). Prior art: `IdentityAnchorTests` asserts on captured upstream message arrays.

---

### T4 -- alias model targeting: pin-first + per-alias prefer
- **type:** afk
- **status:** done
- **blocked-by:** T0a
- **module:** alias policy resolution (`AliasPolicy.cs`) — alias + provider + globals → effective
  pin and prefer patterns; hides the alias→provider→global precedence chain.
- **slice:** alias with `UpstreamModel` on a dynamic provider → that exact id attempted first
  (captured attempt order), failure → failover proceeds into normal dynamic candidates (never
  dead-ends); alias `ModelPrefer` reorders candidates for that alias's requests only; non-aliased
  requests keep provider ordering byte-identical.
- **acceptance-check:** `dotnet test LlmProxy.Tests --filter "FullyQualifiedName~AliasRoutingTests"` → green.
- **files-likely-touched:** `AliasPolicy.cs`, new `LlmProxy.Tests/AliasRoutingTests.cs`.
- **decisions:** Pin **promoted only if already present** in the live/filtered dynamic candidate
  set — a stale/renamed pin is silently skipped rather than forced in as a guaranteed-fail first
  attempt (never-dead-end guardrail; test: `Alias_pin_absent_from_live_catalog_is_not_forced_in`).
  Pin inserted before `_lastGood` in `BuildCandidatesAsync`'s ranked-list construction, with dedup
  if they're equal — pin strictly outranks last-good stickiness. `ModelPrefer` gated on `alias is
  not null` before applying, since it falls back to the provider's own value when unaliased, and
  applying unconditionally risked perturbing the non-aliased byte-identical baseline. Reused
  `ApplyPreferOverride`/`PreferRank` unchanged, no duplicated reorder logic. **Known edge case, not
  tested:** a routing-rule prefer-override can still reorder the pin's position — documented inline,
  not exercised (out of this ticket's required scope). Updated T0a's tripwire test
  (`..._pin_stays_absent_in_T0a...` → `..._pin_resolves_from_alias_value`) per its own doc comment
  flagging it as temporary; added two companion tests for the no-pin cases. Commit `3e8b4ea` on
  `nightshift/t4`. Full suite: 33/33 green; `AliasRoutingTests` filter: 4/4.
- **notes:** PRD criteria: pin-first + per-alias prefer. Pin is a **seed at the front of the
  candidate list, never a filter** (PRD §Risks). This un-inerts the existing `fast` alias — the
  one intended behaviour change to untouched config; assert it explicitly.

---

### T5 -- per-alias attempt timeout
- **type:** afk
- **status:** done
- **blocked-by:** T0a, T4
- **module:** alias policy resolution (`AliasPolicy.cs`) — the timeout leg of the same precedence
  chain (sequenced after T4: same file).
- **slice:** alias `AttemptTimeoutSeconds` overrides the global for that request only; unit test on
  effective-policy resolution + behavioural proof with compressed values (global ~1s, alias ~3s,
  upstream delayed ~2s → succeeds only because the alias timeout applied; a non-aliased request
  against the same slow upstream times out and fails over).
- **acceptance-check:** `dotnet test LlmProxy.Tests --filter "FullyQualifiedName~AliasTimeoutTests"` → green.
- **files-likely-touched:** `AliasPolicy.cs`, new `LlmProxy.Tests/AliasTimeoutTests.cs`.
- **decisions:** `AliasPolicy.Resolve`'s `AttemptTimeoutSeconds` precedence was already correct
  from T0a — no changes needed there. **Found and fixed a real pre-existing bug**: `ProxyService`
  clamped the effective timeout with `Math.Max(5, ...)` (a floor predating this feature, confirmed
  via `git log -p`), which would have silently overridden ANY alias timeout configured under 5s.
  Lowered to `Math.Max(1, ...)` — still guards against 0/negative misconfiguration. Added
  `delayMs` to `FakeUpstream.Enqueue` for behavioral proof, implemented as a cancellable
  `Task.Delay` so it participates in the real timeout path rather than just blocking. Timing:
  global 1s / alias 3s / upstream delay 2s — aliased request succeeds, non-aliased request cancels
  and fails over (ending in 502, matching `FailoverTests.cs`'s all-exhausted pattern). Sequenced
  after T4 (same file) — built on T4's pin-first logic without disturbing it. Commit `9e45ad3` on
  `nightshift/t5`. Full suite: 37/37 green (33 + 4 new); `AliasTimeoutTests` filter: 4/4 (~4s).
- **notes:** PRD criteria: per-alias timeout. The bca use-case (180s reasoning-model calls). Keep
  the behavioural test's wall-clock cost ≤ ~5s.

---

### T6 -- startup validation
- **type:** afk
- **status:** done
- **blocked-by:** T0c
- **module:** startup validation (`StartupValidation.cs`) — options + hosting environment →
  pass/throw with an actionable message; hides every cross-field consistency rule. (T0c wired the
  call site as a no-op; this ticket fills it.)
- **slice:** Production environment + zero inbound keys → host refuses to start; key granting a
  nonexistent alias → startup failure naming the key's app and the bad alias; alias naming an
  unknown provider → startup failure; valid config + Development-no-keys → starts clean.
- **acceptance-check:** `dotnet test LlmProxy.Tests --filter "FullyQualifiedName~StartupValidationTests"` → green.
- **files-likely-touched:** `StartupValidation.cs`, new `LlmProxy.Tests/StartupValidationTests.cs`.
- **decisions:** Collect-all-violations (not fail-fast-on-first) — a human fixing config benefits
  from seeing every problem in one startup attempt; checks are cheap to run exhaustively. No
  `Program.cs` changes needed — T0c had already placed the call site with the right signature;
  only `StartupValidation.cs`'s body was filled in. Dedicated test proves the raw key string never
  appears in any exception message (only the `App` name does). No validation beyond the three
  specified rules. Commit `d121034` on `nightshift/t6`. Full suite: 41/41 green (34 + 7 new).
- **notes:** PRD criteria: Startup validation (all three). Error messages must name the offending
  key's **App**, never the key string.

---

### T7 -- production configuration layer
- **type:** afk
- **status:** todo
- **blocked-by:** -
- **module:** the Production config layer — one file expressing "what differs when hosted": no
  loopback pin, no announce banner, no prompt file.
- **slice:** new `appsettings.Production.json` (auto-included by the Web SDK) → unit test builds
  the configuration stack (base + Production) and asserts bound options: `AnnounceModel` false, no
  provider has a `SystemPromptFile`, no Kestrel loopback endpoint pin survives the layer.
- **acceptance-check:** `dotnet test LlmProxy.Tests --filter "FullyQualifiedName~ProductionConfigTests"` → green.
- **files-likely-touched:** new `appsettings.Production.json`, new `LlmProxy.Tests/ProductionConfigTests.cs`.
- **decisions:** `SystemPromptFile: ""` (empty string, not omission) correctly short-circuits
  `Program.cs`'s `IsNullOrWhiteSpace` fail-fast check and skips loading the personal prompt.
  Kestrel loopback pin resolved as an **explicit override** to `http://0.0.0.0:8080`, not omission
  — ASP.NET's `Kestrel:Endpoints` config takes precedence over `ASPNETCORE_URLS`/platform env vars
  once any `Kestrel:Endpoints` section exists at all, so relying on the platform to override an
  unset value would be fragile; explicit is deterministic. Commit `98dd7b2` on
  `worktree-agent-a9a0752fd2b874fdf`. Full suite: 25/25 green.
- **notes:** PRD criteria: Deployment (config-layer item). Pure config-binding test — needs no web
  host, hence no skeleton dependency. Keys/NVIDIA key arrive via environment, not this file.

---

### T8 -- infrastructure definition
- **type:** afk
- **status:** todo
- **blocked-by:** -
- **module:** `infra/` — Bicep expressing the free-tier environment; hides plan/app/settings
  resource shapes behind parameters (app name, region with swedencentral default + westeurope
  fallback documented).
- **slice:** `infra/main.bicep` (+ `main.bicepparam`): F1 Linux plan + web app, HTTPS-only, app
  settings placeholders for `NVIDIA_API_KEY` + inbound-key config via env, no Always On (F1),
  self-contained deploy compatible. Compiles clean.
- **acceptance-check:** `az bicep build --file infra/main.bicep` exits 0 (fallback if az absent:
  `bicep build infra/main.bicep`).
- **files-likely-touched:** new `infra/main.bicep`, new `infra/main.bicepparam`.
- **decisions:** Inbound keys via **post-deploy `az webapp config appsettings set`**, not a
  flattened Bicep loop — key rotation is operator data that changes far more often than infra
  shape; baking it into the template would make every rotation a redeploy. Verified
  `NVIDIA_API_KEY` (not `Proxy__Providers__nvidia__ApiKey`) is the literal required env var name
  by reading `ProviderOptions.ResolveApiKey()` — matches `appsettings.json`'s `ApiKeyEnv` setting.
  `alwaysOn:false` (F1 rejects true), `linuxFxVersion:''` (self-contained publish needs no
  `DOTNETCORE|X.Y` stack). Commit `fc45e5f` on `worktree-agent-a4e721d37ce7eab39`.
  `az bicep build` exit 0.
- **notes:** PRD criteria: Deployment (infra item), §Risks (region param). Inbound keys as App
  Service settings using .NET's `Proxy__InboundKeys__...` env-var convention — document the
  mapping in a comment.

---

### T9 -- deploy + keep-warm workflows
- **type:** afk
- **status:** done
- **blocked-by:** T8
- **module:** `.github/workflows/` — CI→deploy on push to main via OIDC federated credentials (no
  publish-profile secret), plus the scheduled health ping.
- **slice:** `deploy.yml`: build → `dotnet test LlmProxy.Tests` → publish self-contained linux-x64
  → `azure/login` (OIDC) → deploy to the T8 web app; `keepwarm.yml`: cron `*/10` curl of
  `/health`. Plus a one-time `infra/setup-oidc.sh` (az commands the human runs once to create the
  federated credential).
- **acceptance-check:** `ruby -ryaml -e "YAML.load_file('.github/workflows/deploy.yml'); YAML.load_file('.github/workflows/keepwarm.yml')" && dotnet publish LlmProxy.csproj -c Release -r linux-x64 --self-contained true -o obj/publish-check` exits 0.
- **files-likely-touched:** new `.github/workflows/deploy.yml`, new `.github/workflows/keepwarm.yml`,
  new `infra/setup-oidc.sh`.
- **decisions:** `dotnet-version: '10.0.x'` floating patch (flagged: may need tightening to an exact
  preview build if that's what first resolves). `azure/webapps-deploy@v3` zip-deploy, matching T8's
  `WEBSITE_RUN_FROM_PACKAGE`. **`setup-oidc.sh` grants `Website Contributor` on the resource group**,
  not subscription/RG `Contributor` — narrower blast radius, sufficient for code/config push to an
  existing App Service. Repo needs GitHub **variables** (not secrets) `AZURE_CLIENT_ID`,
  `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`, `AZURE_WEBAPP_NAME` — no long-lived secret, trust
  comes from the federated credential. Commit on `nightshift/t9` (parented on T8's `fc45e5f`).
  YAML valid, `dotnet publish -r linux-x64 --self-contained true` exit 0.
- **notes:** PRD criteria: Deployment (workflow item). The workflow can't be end-to-end verified
  offline — the check proves YAML validity + that the publish command it runs actually works.

---

### T10 -- README: service-mode operations + migration map
- **type:** afk
- **status:** done
- **blocked-by:** T1, T2, T3, T4, T5, T6
- **module:** README service-mode section — operating both modes, key issuance/rotation/revocation,
  alias profile reference (all four new alias fields + key record fields), rate-limit budgets, the
  consumer migration map carried from the brief.
- **slice:** docs match implemented behaviour (written after the behaviour tickets are terminal);
  config reference table for every new option; explicit "local mode unchanged" statement.
- **acceptance-check:** `rg -q "## Service mode" README.md && rg -q "news-digest" README.md && rg -q "Rotation" README.md` exits 0.
- **files-likely-touched:** `README.md`.
- **decisions:** New `## Service mode` section placed after `## Configure`, before `## Secrets`;
  also extended the existing `## Configure` table with the new `InboundKeys.<key>` and
  `RateLimitWindowSeconds` rows (existed in code but were missing from the table). Deployment
  subsection is a brief forward-pointer rather than a link to T7/T8/T9/T11's runbook — this
  ticket's worktree lineage (T1–T6 off T0a/T0b/T0c) never merged those infra tickets, so
  `infra/`/`.github/workflows/`/`scripts/smoke.sh` genuinely don't exist on this branch; the agent
  correctly avoided fabricating a reference to files it couldn't see rather than guessing.
  **Noticed, correctly left unfixed** (docs-only ticket, out of scope): board.md's status table
  looked inconsistent with individual ticket sections when read via `git show main:...` — root
  cause was the orchestrator's live edits to this file never being committed mid-run (see this
  entry now landing in the same commit that fixes it). Commit `99a9e10` on `nightshift/t10`. Full
  suite: 89/89 green (docs-only, no-op on tests as expected).
- **notes:** PRD user story 21 / brief's migration map. Do not document consumer-repo changes as
  tasks — reference only.

---

### T11 -- deployed smoke: script + runbook
- **type:** hitl
- **status:** done
- **blocked-by:** T6, T7, T9
- **module:** `scripts/smoke.sh` + runbook section — the human-executed verification of the live
  instance (the night agent writes and lints it; the human runs it after first deploy).
- **slice:** script takes base URL + key: asserts `/health` 200 unkeyed; keyed chat completion via
  alias returns a completion; wrong key → 401; over-budget burst → 429; greps the deployed payload
  listing for the personal prompt file (must be absent). Runbook: first-deploy order (bicep →
  OIDC setup → secrets → push → smoke).
- **acceptance-check:** `bash -n scripts/smoke.sh` exits 0 (the live run is the HITL part — needs
  the human's Azure login and real key).
- **files-likely-touched:** new `scripts/smoke.sh`, `README.md` (runbook subsection).
- **decisions:** Accepts both positional args and env-var fallback (`SMOKE_BASE_URL`/`SMOKE_API_
  KEY`/`SMOKE_ALIAS`/`SMOKE_BUDGET`); no-args prints usage. No `jq` dependency — presence/absence
  checks via `grep -q` against buffered response bodies, for portability. Rate-limit burst check is
  optional (needs a budget argument the script has no way to discover) and soft-fails to a skip
  message rather than a hard failure when no 429 appears — window timing is out of the script's
  control. **"Personal prompt file absent" checked indirectly**: inspects the completion response
  for the local-mode announce banner (`_[model]_...`); its absence is a proxy signal that the
  Production config layer (which sets `AnnounceModel:false` + empty `SystemPromptFile` together,
  per T7) is actually active — there's no real file-listing HTTP surface to check directly, and
  this reasoning is documented in both the script header and inline. Every parameter name, output
  name, and command shape in the script/runbook was read from the real `infra/main.bicep`,
  `infra/setup-oidc.sh`, `deploy.yml`, `keepwarm.yml`, `appsettings.Production.json`, and
  `ProxyService.cs` — not guessed. Commit `0b281c0` on `nightshift/t11`. `bash -n scripts/smoke.sh`
  → silent, exit 0.
- **notes:** PRD criteria: Deployment (HITL item). Tagged hitl because the meaningful check cannot
  run unattended; the night deliverable is the script + runbook, flagged for the human.
