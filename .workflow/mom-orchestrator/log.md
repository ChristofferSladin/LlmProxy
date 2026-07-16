# Nightshift log: mom-orchestrator

Base: `feat/mom-orchestrator` (off `main` @ d3f7622). Integration is incremental —
each ticket branch is fast-forward/merged into `feat/mom-orchestrator` after its
acceptance-check goes green, so dependent tickets branch from a base that already
contains their blockers' code. The assembled `feat/mom-orchestrator` becomes the
QA branch at the end.

## Prerequisite
- Committed the previously-uncommitted stream peek/`200-err` failover
  (`ProxyService.cs`) + the workflow docs as base commit `5a1d5e3`. `main` untouched.
  `system-prompt.md` left as a local edit (unrelated to the feature).

## Iteration 1 — ready-set {T0}
- **T0 done** (green). `dotnet test --filter ~FailoverTests` → 2 passed.
  Added `LlmProxy.Tests` (xUnit) + `FakeUpstream` + `TestHost`, the `RoutingState`
  skeleton (inert stubs), the `Proxy` config surface (`CooldownSeconds`,
  `IdentityAnchor`, `RoutingRules`), DI registration, and the `ProxyService` ctor seam.
  Decisions: pin `ForceModels` for offline determinism; test project nests under the
  web project so it's excluded from the app compile glob + declares the AspNetCore
  FrameworkReference; failover test uses `MaxAttemptsPerModel=1` for an exact call chain.
  Integrated to `feat/mom-orchestrator` @ 64e818f.

## Iteration 2 — ready-set {T1, T3} (parallel)
- **T3 done** (green, 4 tests). New `PromptComposer.Compose`; widened injection guard so
  the anchor injects even with no provider base prompt; both-blank leaves client system
  messages byte-identical to today. Integrated @ 770b534.
- **T1 done** (green, 3 tests). Cooldown half of `RoutingState`; benches on `peek.IsError`
  and HTTP 429 (not generic 5xx); `DropCoolingDown` filters both candidate paths before the
  cap, full-list fallback = never dead-end. Wiring choice: `RegisterCooldown(model, window)`
  with ProxyService supplying the window, so RoutingState stays options-free (no DI/TestHost
  change). Integrated @ d24ad62 (clean 3-way merge with T3; disjoint ProxyService regions).
- Integrated suite: **9/9 green** (`dotnet test LlmProxy.Tests/LlmProxy.Tests.csproj`).
  NOTE: run tests against the test csproj explicitly — `dotnet test` at repo root resolves
  to the web project (no .sln) and vacuously passes 0 tests.

## Iteration 3 — ready-set {T2}
- dispatched…
