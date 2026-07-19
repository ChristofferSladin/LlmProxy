# QA: service-mode   (night of 2026-07-19, branch qa/service-mode off main)

14 tickets, 0 flagged, 94/94 tests green on the assembled branch. One pre-existing bug found and
fixed along the way (T5); one merge-time test-fixture conflict found and fixed while assembling
the QA branch (not during the night itself — see Integration below).

## Decisions the night made  (bless or redirect)

None flagged — every decision below was made cleanly and logged with its rationale. Read top to
bottom; each is a place the night chose instead of asking.

- **T0a effective-policy seam** — reused `ModelAlias.UpstreamModel` for the future dynamic-provider
  pin instead of adding a second field, since it already had static-provider meaning. Guarded the
  reuse with a dedicated tripwire test forcing T4 to update it explicitly when wiring pin-first
  (rather than let the restriction lapse silently). `PromptComposer.Apply`'s `Anchor` branch
  deliberately throws until T3 implements it — unreachable in T0a since no alias sets that mode
  yet.
  [ ] OK  [ ] Change: __

- **T7 production config layer** — `SystemPromptFile: ""` (empty string, not omission) to
  short-circuit the existing fail-fast file check; verified this actually works via a dedicated
  test rather than assuming. Kestrel loopback pin resolved as an **explicit override** to
  `0.0.0.0:8080` rather than relying on the hosting platform's env vars taking precedence —
  researched and confirmed `Kestrel:Endpoints` config wins over `ASPNETCORE_URLS` once any
  endpoint section exists, so an implicit fallback would have been fragile.
  [ ] OK  [ ] Change: __

- **T8 infra Bicep** — inbound keys supplied via **post-deploy `az webapp config appsettings set`**,
  not baked into the Bicep template as a flattened loop. Rationale: key rotation is operator data
  that changes far more often than infra shape; templating it would make every rotation show up as
  an infra redeploy. Verified (by reading `ProviderOptions.ResolveApiKey()`) that the required env
  var is the literal `NVIDIA_API_KEY`, not a `Proxy__Providers__nvidia__ApiKey`-style path.
  [ ] OK  [ ] Change: __

- **T9 deploy workflows** — `setup-oidc.sh` grants `Website Contributor` scoped to the resource
  group, not subscription/RG `Contributor` — narrower blast radius, sufficient for pushing
  code/config to an existing App Service. GitHub repo needs **variables**, not secrets (no long-lived
  credential; trust comes from the federated OIDC credential).
  [ ] OK  [ ] Change: __

- **T0b integration harness** — full `IHttpClientFactory` singleton replacement rather than
  swapping the named client's handler, specifically to avoid HttpClientFactory's handler-lifetime
  rotation disposing a shared handler mid-test. Host pinned to `Development` environment
  explicitly, so this harness doesn't accidentally trip T6's later prod fail-fast check.
  [ ] OK  [ ] Change: __

- **T0c pipeline wiring** — auth middleware scoped to `/v1/*` via a path-prefix conditional; the
  resolved caller stashed on `HttpContext.Items` **only when a key actually resolves** (nothing to
  restrict on the open path). Deliberately registered **no rate limiter at all** (not even a stub)
  rather than guess at T2's shape — left a narrow `RateLimitPartition.KeyFor` seam instead.
  [ ] OK  [ ] Change: __

- **T1 inbound authority edges** — all three rejection reasons (missing/malformed/unknown key)
  collapse to one generic 401 body deliberately, so a client can't probe for which reason applied.
  Alias-grant enforcement placed inside `ProxyService.ForwardJsonAsync` rather than the header-only
  middleware, since that's the one place that already has both the resolved caller and the parsed
  body — avoids a second JSON parse. **This touched `ProxyService.cs`, outside the ticket's listed
  files** — flagged in the board as a likely merge collision with T4/T5's independent edits to the
  same file; it auto-merged cleanly (see Integration).
  [ ] OK  [ ] Change: __

- **T4 alias model targeting** — the pin is **promoted only if already present** in the live/
  filtered dynamic candidate set; a stale or renamed pin is silently skipped rather than forced in
  as a guaranteed-fail first attempt, preserving the never-dead-end guarantee. Pin outranks
  `_lastGood` stickiness via explicit dedup. `ModelPrefer` gated on `alias is not null` specifically
  to avoid perturbing the non-aliased byte-identical baseline. **Known, accepted gap**: a
  routing-rule prefer-override can still reorder the pin's position — documented inline, not
  covered by a test (judged out of this ticket's required scope).
  [ ] OK  [ ] Change: __

- **T2 rate limiting** — hand-rolled fixed-window counter chosen over .NET's `AddRateLimiter`,
  because expressing "sometimes unlimited, sometimes not" per partition against the framework's
  API would need more machinery than a ~30-line counter. Ordering (auth must resolve before the
  limiter partitions) enforced by gating the limiter middleware on the auth-populated
  `HttpContext.Items` key being present — doubles as the unconfigured-means-unlimited rule.
  **This touched `Program.cs` and `ProxyOptions.cs`**, outside the ticket's listed files — both
  flagged as likely collisions with T1 and T4 respectively; both auto-merged cleanly.
  [ ] OK  [ ] Change: __

- **T5 per-alias timeout** — verified T0a's precedence wiring was already correct (no change
  needed there), but **found and fixed a genuine pre-existing bug**: `ProxyService` clamped every
  effective timeout with a `Math.Max(5, ...)` floor (predates this feature, confirmed via `git log
  -p`) that would have silently overridden any alias timeout configured under 5 seconds — directly
  undermining the ticket's own purpose. Lowered to `Math.Max(1, ...)`.
  [ ] OK  [ ] Change: __

- **T6 startup validation** — collect-all-violations rather than fail-fast-on-first, so a human
  fixing a broken config sees every problem in one attempt. Dedicated test proves the raw key
  string never appears in any exception message (only the `App` name does).
  [ ] OK  [ ] Change: __

- **T11 deployed smoke script** — the "personal prompt file absent from deployment" check is
  **indirect**: it inspects a live completion response for the absence of the local-mode announce
  banner (`_[model]_...`), reasoning that the Production config layer clears both together. There
  is no real HTTP surface to check file absence directly — documented in the script.
  [ ] OK  [ ] Change: __

## Review  (newest first)

### T10 -- README: service-mode operations + migration map   [afk]   [check green]
- **Claims:** documents the fully-merged behavior — key issuance/rotation/revocation, the four
  alias profile fields, rate limiting, startup validation, and the consumer migration map framed
  explicitly as reference (not tasks for this repo).
- **Diff:** `README.md` (+190 in its own branch, folded into the larger merged diff below) — new
  `## Service mode` section. (commit `99a9e10`)
- **Acceptance:** `rg -q "## Service mode" README.md && rg -q "news-digest" README.md && rg -q "Rotation" README.md` → pass.
- **Decisions:** none beyond documentation choices; correctly declined to reference `infra/`/
  workflow files that didn't exist in its own worktree lineage, and correctly flagged (without
  fixing, docs-only scope) an apparent board.md inconsistency that turned out to be the
  orchestrator's own uncommitted-edits gap, not a real board defect.
- **Manual QA:** read `## Service mode` in `README.md` on `qa/service-mode` and confirm the
  `InboundKeys` JSON example's field names match `ProxyOptions.cs`.
- [ ] Approve   [ ] Request changes: ____

### T11 -- deployed smoke: script + runbook   [hitl]   [check green — script only; live run pending]
- **Claims:** `scripts/smoke.sh` + README runbook for first deploy. Checks `/health` unkeyed,
  keyed completion, wrong-key 401, budget burst 429 (soft-skips if no budget given), and the
  indirect prompt-file-absence check.
- **Diff:** new `scripts/smoke.sh` (187 lines), README runbook subsection. (commit `0b281c0`)
- **Acceptance:** `bash -n scripts/smoke.sh` → pass (silent, exit 0).
- **Decisions:** accepts both positional args and env vars; no `jq` dependency (grep-based checks,
  for portability); rate-limit check soft-fails to a skip rather than a hard failure since the
  script has no way to discover a key's configured budget on its own.
- **Manual QA — this is the actual HITL step, not optional:** after first deploy, run
  `scripts/smoke.sh <live-url> <real-key> <alias> <budget>` against the real Azure instance and
  confirm all checks pass. **Nobody has run this against a live deployment yet.**
- [ ] Approve   [ ] Request changes: ____

### T9 -- deploy + keep-warm workflows (OIDC)   [afk]   [check green]
- **Claims:** `deploy.yml` (test-gate → self-contained linux-x64 publish → OIDC login → zip-deploy),
  `keepwarm.yml` (10-min cron on `/health`), `setup-oidc.sh` (one-time human setup).
- **Diff:** `.github/workflows/deploy.yml` (+81), `.github/workflows/keepwarm.yml` (+24),
  `infra/setup-oidc.sh` (+89). (commit `3c4d101`)
- **Acceptance:** YAML parses for both workflows; `dotnet publish -c Release -r linux-x64
  --self-contained true` — the exact command the workflow runs — actually executed and succeeded.
- **Decisions:** `dotnet-version: '10.0.x'` floating patch (flagged as possibly needing tightening
  if only a preview SDK resolves on first real run); `Website Contributor` IAM scoping (see above).
- **Manual QA:** open `.github/workflows/deploy.yml` and confirm the four expected repo variables
  (`AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`, `AZURE_WEBAPP_NAME`) match what
  `setup-oidc.sh` prints and what the README runbook instructs setting.
- [ ] Approve   [ ] Request changes: ____

### T8 -- infrastructure definition (Bicep, F1, params)   [afk]   [check green]
- **Claims:** `infra/main.bicep` + `main.bicepparam` — F1 Linux plan + web app, HTTPS-only, no
  Always On (F1 rejects it), self-contained-publish-compatible (`linuxFxVersion: ''`).
- **Diff:** `infra/main.bicep` (+98), `infra/main.bicepparam` (+8). (commit `fc45e5f`)
- **Acceptance:** `az bicep build --file infra/main.bicep` → clean compile (re-verified during
  merge-night's integration check, not just at ticket time).
- **Decisions:** post-deploy key config over Bicep-flattened loop (see above); verified
  `NVIDIA_API_KEY` naming by reading the actual key-resolution code rather than assuming.
- **Manual QA:** confirm `location` param defaults to `swedencentral` with `westeurope` documented
  as the fallback in the header comment, matching the brief's hosting decision.
- [ ] Approve   [ ] Request changes: ____

### T7 -- production configuration layer   [afk]   [check green]
- **Claims:** `appsettings.Production.json` disables the announce banner, clears the personal
  prompt file, and rebinds Kestrel off loopback.
- **Diff:** `appsettings.Production.json` (+17), `LlmProxy.Tests/ProductionConfigTests.cs` (+115).
  (commit `98dd7b2`)
- **Acceptance:** `dotnet test --filter ProductionConfigTests` → 5/5 green at ticket time;
  **entered the assembled QA branch's 94-test count only via the T11 merge** (T10's branch alone
  ran 89 — see Integration).
- **Decisions:** empty-string `SystemPromptFile` override, explicit Kestrel rebind (see above,
  both researched rather than assumed).
- **Manual QA:** confirm `appsettings.Production.json` sets `AnnounceModel: false` and an empty
  `SystemPromptFile` for the `nvidia` provider, and binds Kestrel to `0.0.0.0:8080`.
- [ ] Approve   [ ] Request changes: ____

### T6 -- startup validation   [afk]   [check green]
- **Claims:** Production + zero inbound keys refuses to start; a key granting an unknown alias, or
  an alias naming an unknown provider, both fail startup naming the `App`/alias/provider — never
  the key string.
- **Diff:** `StartupValidation.cs` (filled in from T0c's no-op stub, +58 net), new
  `LlmProxy.Tests/StartupValidationTests.cs` (+127). (commit `d121034`)
- **Acceptance:** `dotnet test --filter StartupValidationTests` → 7/7 green.
- **Decisions:** collect-all-violations; no `Program.cs` change needed (T0c's call site was
  correctly placed).
- **Manual QA:** read the dedicated test asserting the raw key string never leaks into an
  exception message and confirm it actually checks message *content*, not just that an exception
  was thrown.
- [ ] Approve   [ ] Request changes: ____

### T5 -- per-alias attempt timeout   [afk]   [check green]
- **Claims:** an alias's `AttemptTimeoutSeconds` overrides the global for that request only;
  proven behaviorally (compressed timings) that the alias path succeeds where the non-aliased path
  fails over.
- **Diff:** `ProxyService.cs` (the `Math.Max` floor fix), `LlmProxy.Tests/FakeUpstream.cs` (delay
  support), new `LlmProxy.Tests/AliasTimeoutTests.cs` (+107). (commit `9e45ad3`)
- **Acceptance:** `dotnet test --filter AliasTimeoutTests` → 4/4 green (~4s wall clock).
- **Decisions:** **the pre-existing timeout-floor bug fix** — this is the one finding in the whole
  night that changes behavior beyond the ticket's own scope; worth explicit sign-off since it
  affects every request, not just aliased ones (floor dropped from 5s to 1s).
- **Manual QA:** confirm the new floor (`Math.Max(1, ...)`) still guards against a genuinely
  misconfigured 0 or negative timeout, per the code comment's stated intent.
- [ ] Approve   [ ] Request changes: ____

### T4 -- alias model targeting: pin-first + per-alias prefer   [afk]   [check green]
- **Claims:** an alias's pinned model is tried first (when live), failover proceeds normally if it
  fails; per-alias `ModelPrefer` reorders candidates for that alias's requests only.
- **Diff:** `AliasPolicy.cs`, `ProxyOptions.cs`, `ProxyService.cs`, new
  `LlmProxy.Tests/AliasRoutingTests.cs` (+140). Updated T0a's self-flagged tripwire test as
  instructed (`..._pin_stays_absent_in_T0a...` → `..._pin_resolves_from_alias_value`), adding two
  companion tests for the no-pin cases. (commit `3e8b4ea`)
- **Acceptance:** `dotnet test --filter AliasRoutingTests` → 4/4 green.
- **Decisions:** pin-only-if-live, pin-before-lastGood, prefer gated on `alias is not null` (all
  above); the un-tested pin+conflicting-routing-rule interaction is a known, accepted gap.
- **Manual QA:** confirm this ticket un-inerted the existing `fast` alias in `appsettings.json` —
  i.e. that a request with `model: "fast"` against the live `nvidia` provider now actually tries
  `meta/llama-3.1-8b-instruct` first, where before this feature it silently didn't.
- [ ] Approve   [ ] Request changes: ____

### T3 -- prompt modes: passthrough + anchor   [afk]   [check green]
- **Claims:** `Passthrough` relays messages byte-identical; `Anchor` preserves client messages and
  appends the identity anchor after the last client system message (or first, if none); `Own`
  (unset) reproduces today's behavior exactly.
- **Diff:** `PromptComposer.cs` (+31 net, implementing the `Anchor` branch T0a left throwing), new
  `LlmProxy.Tests/PromptModeTests.cs` (+217). (commit `fd290fe`)
- **Acceptance:** `dotnet test --filter PromptModeTests` → 9/9 green; `dotnet test --filter
  IdentityAnchorTests` → 4/4 green, **confirmed zero diff via `git diff --stat`** — not just
  assumed unmodified.
- **Decisions:** anchor inserted after the *last* client system message (tested explicitly with a
  4-message case); `Passthrough` was already a correct no-op from T0a's stub, left untouched.
- **Manual QA:** send a request through an `Anchor`-mode alias with two client system messages and
  confirm the anchor lands after the second one, not the first.
- [ ] Approve   [ ] Request changes: ____

### T2 -- per-application rate limiting   [afk]   [check green — see Integration for a
  post-merge fixture fix]
- **Claims:** per-key `RequestsPerMinute` budget, partitioned by `App` (not key, so rotation shares
  one budget); 429 + `Retry-After`; unconfigured = unlimited; never touches upstream or
  `RoutingState` when throttled.
- **Diff:** `RateLimitPartition.cs` (filled in, +98 net), `Program.cs`, `ProxyOptions.cs`
  (`RateLimitWindowSeconds`), new `LlmProxy.Tests/RateLimitTests.cs` (+186, **one line fixed
  post-merge** — see Integration). (commit `ace1115`)
- **Acceptance:** `dotnet test --filter RateLimitTests` → 5/5 green at ticket time on its own
  branch; **4 of these 5 broke when merged against T1's work and needed a one-line fixture fix**
  (see Integration) — now 5/5 green on `qa/service-mode`.
- **Decisions:** hand-rolled counter over `AddRateLimiter` (see above); the `Program.cs`/
  `ProxyOptions.cs` file crossovers (flagged, both auto-merged clean).
- **Manual QA:** confirm the "two keys, one app, shared budget" test actually alternates between
  both key strings in its burst (this is the specific regression the ticket was pinning against a
  partition-by-key bug).
- [ ] Approve   [ ] Request changes: ____

### T1 -- inbound authority edges   [afk]   [check green]
- **Claims:** 401 on missing/malformed/unknown key (uniform body); 400 naming allowed aliases on
  out-of-grant; single-grant model omission succeeds, multi-grant omission is ambiguous (400); two
  keys per app both accepted; no key material in logs or bodies on any path.
- **Diff:** `InboundAuth.cs` (extended), `Program.cs`, `ProxyService.cs` (alias-grant call site),
  new `LlmProxy.Tests/InboundAuthTests.cs` (+377). (commit `b73b2a5`)
- **Acceptance:** `dotnet test --filter InboundAuthTests` → 24/24 green.
- **Decisions:** alias-grant enforcement placed in `ProxyService`, not middleware (see above); the
  `ProxyService.cs` file crossover (flagged, auto-merged clean against T4/T5's independent edits).
- **Manual QA:** trigger a request with a wrong key and grep the response body + captured logs by
  hand for the literal wrong key string, confirming the no-leak test's claim.
- [ ] Approve   [ ] Request changes: ____

### T6/T7/T9/T11's shared infra lineage, T0a/T0b/T0c   [afk]   [check green]
Already covered individually above; noting here only that `nightshift/t11`'s branch (T6+T7+T8+T9+
T11) shared T0a/T0b/T0c/T6 ancestry with `nightshift/t10`'s branch (T0a+T0b+T0c+T1-T6), which is
why merging both into `qa/service-mode` only applied each ticket's diff once.

## Integration

- **Full suite on `qa/service-mode`: green — 94/94.** (Up from `nightshift/t10`'s own 89; the +5
  is T7's `ProductionConfigTests`, which only entered the merged tree via the T11 branch.)
- **`az bicep build --file infra/main.bicep`** → clean.
- **Both workflow YAML files parse.**
- **`bash -n scripts/smoke.sh`** → clean.
- **`dotnet publish -c Release -r linux-x64 --self-contained true`** — the literal command
  `deploy.yml` runs — executed directly and succeeded.
- **Held back from merge: none.** Every ticket branch merged.
- **Fixes applied during merge-night** (not part of any ticket's own commit, done while assembling
  the branch — see `log.md`'s merge report for full detail):
  1. **`LlmProxy.Tests/RateLimitTests.cs`** (commit `3073624`, on the way to assembling
     `nightshift/t10`) — 4 of T2's 5 tests broke when merged against T1's alias-grant enforcement:
     every test hardcoded `model: "auto"` against a key granting only `"fast"`, written before T1's
     check existed. One-line fixture fix (`"auto"` → `"fast"`), no assertion weakened.
  2. **`README.md`** (commit `cd020b8`, on `qa/service-mode` itself) — T10 and T11 each added a
     correct-in-isolation section that referenced the other as not-yet-existing. Relocated one
     section and fixed two stale cross-references; no content removed.
- **Two other flagged collision risks did NOT need manual fixes** — `ProxyOptions.cs` (T2×T4) and
  `Program.cs` (T1×T2) both auto-merged with zero conflict markers, confirmed by re-running the
  full suite after each merge step rather than trusting git's silence alone.

## After approval

Merge `qa/service-mode` -> `main`; then **strip the scaffolding** — `git rm -r
.workflow/service-mode/` and commit (code + tests are the living docs; git history keeps the spec
& the *why*). This repo has no `PRODUCT.md`-style planning doc to reconcile into; `README.md`'s
new `## Service mode` section (T10) already serves that role going forward.

**Before merging, note the one thing this QA pass could not verify**: T11's smoke script has never
run against a live deployment. Approving the code is not the same as confirming the deployed
service works — that's the HITL step in T11's card above, and it needs a human with Azure access
after (or as part of) this merge.
