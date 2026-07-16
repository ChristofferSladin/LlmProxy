# QA: mom-orchestrator   (night of 2026-07-16, branch qa/mom-orchestrator off main)

Turns the proxy into a Mixture-of-Models orchestrator: cooldown-aware failover,
learned tool-capability routing, a proxy-owned identity anchor, and a declarative
request-shape router. All state in-memory. Verified offline against a fake upstream.

**Snapshot:** 5/5 tickets `done`, 0 flagged, 0 held back. Integration suite **18/18 green**.
`main` untouched; nothing pushed.

---

## Decisions the night made  (bless or redirect)

- **T2 — tool-error ends the first request** (the UX edge). A genuine tool-incapable
  model that rejects a `tools` request with an HTTP **400** still **surfaces that error to
  the client and ends the request** — no failover on non-2xx/non-429/404/5xx (pre-existing
  control flow). The model is demoted and skipped only on *subsequent* `tools` requests. So
  the first hit on an incapable model is a visible error, not a silent reroute.
  [ ] OK   [ ] Change: __ (e.g. make a tool-capability 400 fail over in-request)
- **T2 — demotion markers are broad** (`"tool"`, `"function call"`, `"function_call"`,
  case-insensitive). Only consulted on an *error* body of a *tools* request, and the
  optimistic default means a false match costs one wasted attempt — never a false demotion
  of a capable model. But the bare `"tool"` substring is the loosest guess at NVIDIA's real
  wording.  [ ] OK   [ ] Change: __
- **T1 — only HTTP 429 benches** (not generic 5xx). 5xx stays a plain transient retry.
  Cooldown default 60s.  [ ] OK   [ ] Change: __
- **T1 — cooldown window is passed by the caller** (`RegisterCooldown(model, window)`), so
  `RoutingState` stays options-free and DI/TestHost were untouched.  [ ] OK   [ ] Change: __
- **T4 — first-match-wins** rule evaluation, and an explicit routing rule **outranks
  `_lastGood` stickiness** for that request.  [ ] OK   [ ] Change: __
- **T4 — classification runs over the client's original messages BEFORE anchor injection**,
  so `minChars`/`contentMatches` reflect the user's request, not the constant anchor.
  [ ] OK   [ ] Change: __
- **T4 — `RoutingRules` ships empty**; content matching is case-insensitive substring (no
  regex).  [ ] OK   [ ] Change: __
- **T3 — `IdentityAnchor` ships ON** with a default anti-confabulation/continuity string —
  the one intentional behavior change. Blank it to disable.  [ ] OK   [ ] Change: __
- **T0 — offline harness by direct construction** (no `WebApplicationFactory`); candidates
  pinned via `ForceModels` for determinism; test project nested under the web project so its
  sources are excluded from the app's compile glob.  [ ] OK   [ ] Change: __

---

## Review  (newest first)

### T4 -- declarative heuristic router   [afk]   [check green]
- **Claims:** ordered `RoutingRules` bias candidate order by request shape
  (`hasTools`/`minChars`/`contentMatches`); soft reorder only, never excludes; empty rules =
  today's ordering.
- **Diff:** `RoutingRuleSet.cs` (new), `RequestClassifier.cs` (+`CharCount`/`Matches`/`Classify`),
  `ProxyService.cs` (`ApplyPreferOverride`/`PreferRank`), `RoutingRulesTests.cs` — first-match-wins
  override composed with the hard filters.  (commit d1f1376)
- **Acceptance:** `dotnet test LlmProxy.Tests/LlmProxy.Tests.csproj --filter ~RoutingRulesTests` -> 5 passed.
- **Decisions:** first-match-wins; rule outranks last-good; classify before anchor injection; rules empty by default.
- **Manual QA:** add a rule `{ when:{hasTools:true}, prefer:["nemotron"] }` to appsettings, send a
  tools request from LM Studio, confirm the proxy log's first attempt is a nemotron model.
- [ ] Approve   [ ] Request changes: ____

### T2 -- learned tool-capability map + hard filter   [afk]   [check green]
- **Claims:** demote a model to tool-incapable on an explicit tool/function error to a `tools`
  request; skip incapable models on future `tools` requests; unknown = capable; silence never demotes.
- **Diff:** `RequestClassifier.cs` (`HasTools`), `RoutingState.cs` (capability half),
  `ProxyService.cs` (`DropToolIncapable`, `IsToolCapabilityError`, demote at peek + non-2xx sites),
  `CapabilityTests.cs`.  (commit d1c1201)
- **Acceptance:** `dotnet test ... --filter ~CapabilityTests` -> 4 passed.
- **Decisions:** broad-but-gated markers; hard filter never dead-ends, composes with cooldown;
  tool-error 400 surfaces-and-ends first request (see Decisions).
- **Manual QA:** point a `tools` request at a model you know can't do tool calls; confirm it errors
  once, then a second tools request skips it (proxy log shows a different first model).
- [ ] Approve   [ ] Request changes: ____

### T1 -- cooldown registry   [afk]   [check green]
- **Claims:** bench a model on `200-err`/`429` for `CooldownSeconds`; candidate build skips benched
  models; never dead-ends.
- **Diff:** `RoutingState.cs` (cooldown half), `ProxyService.cs` (`DropCoolingDown`, register at
  peek-error + 429), `appsettings.json` (`CooldownSeconds:60`), `CooldownTests.cs`.  (commit b894010)
- **Acceptance:** `dotnet test ... --filter ~CooldownTests` -> 3 passed.
- **Decisions:** 429-only benches; window passed by caller; expiry pruned on read.
- **Manual QA:** hammer one model until `200-err`, confirm the *next* prompt's proxy log skips it
  (no attempt line for that model) and answers on another.
- [ ] Approve   [ ] Request changes: ____

### T3 -- identity / continuity anchor   [afk]   [check green]
- **Claims:** append a model-agnostic `IdentityAnchor` after the provider system prompt; works with
  no base prompt; blank = today's behavior.
- **Diff:** `PromptComposer.cs` (new), `ProxyService.cs` (injection block widened),
  `IdentityAnchorTests.cs`.  (commit 6bb1e0e)
- **Acceptance:** `dotnet test ... --filter ~IdentityAnchorTests` -> 4 passed.
- **Decisions:** `base + "\n\n" + anchor`; anchor injected even without a base prompt; both-blank
  leaves client messages byte-identical to today.
- **Manual QA:** ask "what model are you?" in LM Studio — confirm it no longer claims to be Claude/GPT.
- [ ] Approve   [ ] Request changes: ____

### T0 -- test harness + seams (walking skeleton)   [afk]   [check green]
- **Claims:** xUnit project + `FakeUpstream` + `TestHost`; `RoutingState` skeleton, config surface,
  DI wiring; one failover regression test.
- **Diff:** `LlmProxy.Tests/*` (csproj, FakeUpstream, TestHost, FailoverTests), `RoutingState.cs`
  (stubs), `ProxyOptions.cs` (config+types), `Program.cs` (DI), `ProxyService.cs` (ctor seam),
  `LlmProxy.csproj` (glob exclude).  (commit 64e818f)
- **Acceptance:** `dotnet test ... --filter ~FailoverTests` -> 2 passed.
- **Decisions:** direct construction over fakes; `ForceModels` for determinism; nested test project
  excluded from the web compile glob + AspNetCore FrameworkReference.
- **Manual QA:** `dotnet test LlmProxy.Tests/LlmProxy.Tests.csproj` from a clean checkout — 18 green.
- [ ] Approve   [ ] Request changes: ____

---

## Integration
- **Full suite on qa/mom-orchestrator:** `dotnet test LlmProxy.Tests/LlmProxy.Tests.csproj` →
  **18/18 passed** (2 failover + 3 cooldown + 4 anchor + 4 capability + 5 router), 0 skipped.
  Run against the test csproj explicitly — `dotnet test` at repo root resolves to the web project
  and vacuously passes 0 tests.
- **Held back from merge (manual):** none. All ticket branches merged clean; no conflicts.
- **Coverage caveat:** every check is **offline** against the fake upstream — proves routing *logic*,
  not live NVIDIA behavior. The T2 tool-error phrasing is an untested guess at NVIDIA's real error
  wording; verify against a live tool-reject/exhausted response before trusting the demotion path.

## After approval
Merge `qa/mom-orchestrator` -> `main` (you do this — agents never push). Then strip the scaffolding:
`git rm -r .workflow/mom-orchestrator/` and commit (code + tests are the living docs; git history keeps
the spec & the *why*). Consider documenting the new `Proxy` config keys (`CooldownSeconds`,
`IdentityAnchor`, `RoutingRules`) in the README's config table. No PRODUCT.md in this repo to reconcile.
