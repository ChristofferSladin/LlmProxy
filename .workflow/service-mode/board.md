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
| T0a | effective-policy seam over the forwarding path (behaviour frozen) | afk | todo | -        |
| T0b | integration harness over the real pipeline                    | afk  | todo   | -          |
| T0c | pipeline wiring: auth happy path + limiter/validation seams   | afk  | todo   | T0a, T0b   |
| T1  | inbound authority edges (401/400, omission, rotation, no-leak)| afk  | todo   | T0c        |
| T2  | per-application rate limiting                                 | afk  | todo   | T0c        |
| T3  | prompt modes: passthrough + anchor (own preserved)            | afk  | todo   | T0a        |
| T4  | alias model targeting: pin-first + per-alias prefer           | afk  | todo   | T0a        |
| T5  | per-alias attempt timeout                                     | afk  | todo   | T0a, T4    |
| T6  | startup validation (prod-requires-keys, config consistency)   | afk  | todo   | T0c        |
| T7  | production configuration layer                                | afk  | todo   | -          |
| T8  | infrastructure definition (Bicep, F1, params)                 | afk  | todo   | -          |
| T9  | deploy + keep-warm workflows (OIDC)                           | afk  | todo   | T8         |
| T10 | README: service-mode operations + migration map               | afk  | todo   | T1, T2, T3, T4, T5, T6 |
| T11 | deployed smoke: script + runbook (human executes)             | hitl | todo   | T6, T7, T9 |

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
- **status:** todo
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
- **decisions:**
- **notes:** PRD §Deep-module map (alias policy resolution, prompt composition). This is the
  refactor ticket: if any existing test needs editing, the seam leaked — stop and flag. No
  behaviour change is permitted here, including the pin fix (that lands in T4).

---

### T0b -- integration harness over the real pipeline
- **type:** afk
- **status:** todo
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
- **decisions:**
- **notes:** PRD §Testing strategy — this harness is new ground in the repo and everything
  auth/rate-limit shaped depends on it; proving it against TODAY's app (no service-mode features
  needed) is the point of splitting it out. Log capture (for T1's no-leak test) belongs here.

---

### T0c -- pipeline wiring: auth happy path + limiter/validation seams
- **type:** afk
- **status:** todo
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
- **decisions:**
- **notes:** Happy path ONLY — rejection shapes are T1, real partitioning is T2, real validation
  is T6. This ticket exists so those three fan out onto proven wiring without touching
  `Program.cs` again.

---

### T1 -- inbound authority edges
- **type:** afk
- **status:** todo
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
- **decisions:**
- **notes:** PRD criteria: Authentication and authorization (all six). Unit-test the pure rules;
  integration-test the wire shapes (401 body, health reachability, log absence) via T0b's harness.

---

### T2 -- per-application rate limiting
- **type:** afk
- **status:** todo
- **blocked-by:** T0c
- **module:** rate-limit partitioning (`RateLimitPartition.cs`) — request → partition + window;
  hides the app-not-key rule and unconfigured-means-unlimited.
- **slice:** key with `RequestsPerMinute` budget → burst past it → 429 + `Retry-After`; two keys of
  one app share one budget; app A exhausted ⇏ app B throttled; rejection happens before any
  upstream call (FakeUpstream captured zero requests) and benches nothing in `RoutingState`;
  unconfigured → unlimited.
- **acceptance-check:** `dotnet test LlmProxy.Tests --filter "FullyQualifiedName~RateLimitTests"` → green.
- **files-likely-touched:** `RateLimitPartition.cs`, new `LlmProxy.Tests/RateLimitTests.cs`.
- **decisions:**
- **notes:** PRD criteria: Rate limiting (all four). Partition by **application** — the
  two-keys-one-budget test is the regression pin for the auth-before-limiter pipeline ordering.
  Use compressed windows; no real-time sleeps beyond the window length.

---

### T3 -- prompt modes: passthrough + anchor
- **type:** afk
- **status:** todo
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
- **decisions:**
- **notes:** PRD criteria: Alias routing profiles (prompt-mode items + body-fields item). Tests
  drive the existing `TestHost` (no pipeline needed — aliases resolve in `ProviderRegistry`
  today). Prior art: `IdentityAnchorTests` asserts on captured upstream message arrays.

---

### T4 -- alias model targeting: pin-first + per-alias prefer
- **type:** afk
- **status:** todo
- **blocked-by:** T0a
- **module:** alias policy resolution (`AliasPolicy.cs`) — alias + provider + globals → effective
  pin and prefer patterns; hides the alias→provider→global precedence chain.
- **slice:** alias with `UpstreamModel` on a dynamic provider → that exact id attempted first
  (captured attempt order), failure → failover proceeds into normal dynamic candidates (never
  dead-ends); alias `ModelPrefer` reorders candidates for that alias's requests only; non-aliased
  requests keep provider ordering byte-identical.
- **acceptance-check:** `dotnet test LlmProxy.Tests --filter "FullyQualifiedName~AliasRoutingTests"` → green.
- **files-likely-touched:** `AliasPolicy.cs`, new `LlmProxy.Tests/AliasRoutingTests.cs`.
- **decisions:**
- **notes:** PRD criteria: pin-first + per-alias prefer. Pin is a **seed at the front of the
  candidate list, never a filter** (PRD §Risks). This un-inerts the existing `fast` alias — the
  one intended behaviour change to untouched config; assert it explicitly.

---

### T5 -- per-alias attempt timeout
- **type:** afk
- **status:** todo
- **blocked-by:** T0a, T4
- **module:** alias policy resolution (`AliasPolicy.cs`) — the timeout leg of the same precedence
  chain (sequenced after T4: same file).
- **slice:** alias `AttemptTimeoutSeconds` overrides the global for that request only; unit test on
  effective-policy resolution + behavioural proof with compressed values (global ~1s, alias ~3s,
  upstream delayed ~2s → succeeds only because the alias timeout applied; a non-aliased request
  against the same slow upstream times out and fails over).
- **acceptance-check:** `dotnet test LlmProxy.Tests --filter "FullyQualifiedName~AliasTimeoutTests"` → green.
- **files-likely-touched:** `AliasPolicy.cs`, new `LlmProxy.Tests/AliasTimeoutTests.cs`.
- **decisions:**
- **notes:** PRD criteria: per-alias timeout. The bca use-case (180s reasoning-model calls). Keep
  the behavioural test's wall-clock cost ≤ ~5s.

---

### T6 -- startup validation
- **type:** afk
- **status:** todo
- **blocked-by:** T0c
- **module:** startup validation (`StartupValidation.cs`) — options + hosting environment →
  pass/throw with an actionable message; hides every cross-field consistency rule. (T0c wired the
  call site as a no-op; this ticket fills it.)
- **slice:** Production environment + zero inbound keys → host refuses to start; key granting a
  nonexistent alias → startup failure naming the key's app and the bad alias; alias naming an
  unknown provider → startup failure; valid config + Development-no-keys → starts clean.
- **acceptance-check:** `dotnet test LlmProxy.Tests --filter "FullyQualifiedName~StartupValidationTests"` → green.
- **files-likely-touched:** `StartupValidation.cs`, new `LlmProxy.Tests/StartupValidationTests.cs`.
- **decisions:**
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
- **decisions:**
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
- **decisions:**
- **notes:** PRD criteria: Deployment (infra item), §Risks (region param). Inbound keys as App
  Service settings using .NET's `Proxy__InboundKeys__...` env-var convention — document the
  mapping in a comment.

---

### T9 -- deploy + keep-warm workflows
- **type:** afk
- **status:** todo
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
- **decisions:**
- **notes:** PRD criteria: Deployment (workflow item). The workflow can't be end-to-end verified
  offline — the check proves YAML validity + that the publish command it runs actually works.

---

### T10 -- README: service-mode operations + migration map
- **type:** afk
- **status:** todo
- **blocked-by:** T1, T2, T3, T4, T5, T6
- **module:** README service-mode section — operating both modes, key issuance/rotation/revocation,
  alias profile reference (all four new alias fields + key record fields), rate-limit budgets, the
  consumer migration map carried from the brief.
- **slice:** docs match implemented behaviour (written after the behaviour tickets are terminal);
  config reference table for every new option; explicit "local mode unchanged" statement.
- **acceptance-check:** `rg -q "## Service mode" README.md && rg -q "news-digest" README.md && rg -q "Rotation" README.md` exits 0.
- **files-likely-touched:** `README.md`.
- **decisions:**
- **notes:** PRD user story 21 / brief's migration map. Do not document consumer-repo changes as
  tasks — reference only.

---

### T11 -- deployed smoke: script + runbook
- **type:** hitl
- **status:** todo
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
- **decisions:**
- **notes:** PRD criteria: Deployment (HITL item). Tagged hitl because the meaningful check cannot
  run unattended; the night deliverable is the script + runbook, flagged for the human.
