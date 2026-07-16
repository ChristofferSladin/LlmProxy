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
- dispatched…
