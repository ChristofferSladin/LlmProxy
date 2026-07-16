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
- **T2 done** (green, 4 tests; 13/13 full). New `RequestClassifier.HasTools`; capability half
  of `RoutingState`; demote on a narrow tool/function error match at both the `peek.IsError`
  and non-2xx sites, gated on `hasTools`; `DropToolIncapable` hard filter mirrors T1's
  `DropCoolingDown`, both applied before the cap, never dead-ends. Test isolation: tool error
  delivered as HTTP 400 (not a cooldown trigger) so capability behavior is isolated from
  cooldown. Integrated @ 230c86c.

## Iteration 4 — ready-set {T4}
- **T4 done** (green, 5 tests; 18/18 full). Extended `RequestClassifier` with `CharCount` +
  `Matches`; new `RoutingRuleSet.PreferOverride` (first-match-wins over ordered `RoutingRules`);
  soft stable prefer-reorder applied in `BuildCandidatesAsync` composed with the hard
  cooldown/tool filters (filter excludes, reorder biases; an explicit rule outranks `_lastGood`
  for that request). Empty `RoutingRules` reproduces today's ordering (backward-compat test).
  Classification runs over the client's original messages BEFORE anchor injection, so char/content
  reflect the user's request not the constant anchor. First dispatch hit the session token limit
  after the agent committed `ticket/T4`; verified independently via a no-commit test-merge
  (clean, 18/18) then integrated @ 298a162.

## Merge-night — qa/mom-orchestrator
- Assembly was incremental: each ticket fast-forward/no-ff merged into `feat/mom-orchestrator`
  after its own acceptance-check went green (T0 → {T1,T3} → T2 → T4), so dependent tickets always
  branched from a base containing their blockers.
- **Merged clean, in DAG order:** T0, T1, T3, T2, T4. **Auto-resolved conflicts:** none (T1/T3
  touched disjoint `ProxyService` regions; board.md auto-merged). **Held back:** none.
- **QA branch:** `qa/mom-orchestrator` (worktree `.claude/worktrees/qa-mom-orchestrator`), off the
  assembled `feat/mom-orchestrator`. `main` untouched; nothing pushed.
- **Integration suite:** `dotnet test LlmProxy.Tests/LlmProxy.Tests.csproj` →
  **18/18 passed** (2 failover + 3 cooldown + 4 anchor + 4 capability + 5 router).
- **Consolidated diffstat** (`git diff main...qa/mom-orchestrator --stat`): 21 files, +1963/-38 —
  new `LlmProxy.Tests` project (7 test/harness files), `RoutingState`, `RequestClassifier`,
  `RoutingRuleSet`, `PromptComposer`, and the `ProxyService`/`ProxyOptions`/`Program`/config wiring.
  (The `ProxyService` delta vs `main` also includes the previously-uncommitted stream peek/`200-err`
  failover, which was committed as the feature base.)
- **DAG note for next time:** T1/T2/T4 all edit `BuildCandidatesAsync`; sequencing them (rather than
  parallelizing) was correct — no spine collisions occurred. T3 parallel with T1 was safe.
- Next: **/qa-plan** to review `qa/mom-orchestrator`.
