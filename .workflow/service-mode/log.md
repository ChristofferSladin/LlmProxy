# Nightshift log: service-mode

## Iteration 1 — started 2026-07-19

Ready-set: T0a, T0b, T7, T8 (all blocked-by `-`, file-disjoint, dispatched in parallel).

Dispatched:
- T0a — effective-policy seam over the forwarding path (behaviour frozen)
- T0b — integration harness over the real pipeline
- T7 — production configuration layer
- T8 — infrastructure definition (Bicep)

### Results

- **T8 → done.** `az bicep build` exit 0. Decision: inbound keys via post-deploy
  `az webapp config appsettings set`, not a Bicep-flattened loop (rotation shouldn't be a
  redeploy). Verified `NVIDIA_API_KEY` is the literal required env var name. Branch
  `worktree-agent-a4e721d37ce7eab39` @ `fc45e5f`.
- **T7 → done.** 25/25 tests green. Decisions: `SystemPromptFile: ""` correctly short-circuits the
  fail-fast file check; Kestrel loopback pin explicitly overridden to `0.0.0.0:8080` rather than
  relying on platform env-var precedence (researched and confirmed `Kestrel:Endpoints` config wins
  over `ASPNETCORE_URLS` once any endpoint is configured). Branch
  `worktree-agent-a9a0752fd2b874fdf` @ `98dd7b2`.
- **T0b → done.** 22/22 tests green (20 pre-existing unmodified + 2 new). Built
  `IntegrationHost`/`IntegrationSmokeTests` over a real `WebApplicationFactory<Program>`, full
  `IHttpClientFactory` replacement for the fake upstream, minimal flat log capture for T1's future
  no-key-leak test. Branch `worktree-agent-a8d48788e59cca914` @ `4891424`.
- **T9 dispatched** (blocked-by T8, now terminal) into a worktree branched from T8's branch
  (`nightshift-t9` / `nightshift/t9`), so it starts with the Bicep files already present.
- **T9 → done.** Blocked-by T8 (terminal), dispatched into a worktree branched from T8's branch.
  `deploy.yml` (test-gate → self-contained linux-x64 publish → OIDC login → zip-deploy),
  `keepwarm.yml` (10-min cron on `/health`), `setup-oidc.sh` (one-time human setup, scoped to
  `Website Contributor` on the resource group rather than a wider role). YAML valid, publish
  command verified to actually succeed.
- **T0a → done.** 27/27 tests green (20 pre-existing unmodified + 7 new). Built `AliasPolicy.cs`
  (`EffectivePolicy` + pure static `Resolve`), extended `ProxyOptions` (inert `InboundKeys`,
  `PromptMode` enum, new optional alias fields), moved prompt logic behind
  `PromptComposer.Apply`. Reused `ModelAlias.UpstreamModel` for the future pin rather than forking
  a field, with a dedicated tripwire test forcing T4 to update it when it wires pin-first.
  `Anchor` mode deliberately throws (unreachable until T3 implements it). `Program.cs` untouched.

## Iteration 2 — opened

T0a's completion unlocks three tickets at once. Ready-set: T0c (blocked-by T0a+T0b, both now
terminal), T3 (blocked-by T0a), T4 (blocked-by T0a).

Worktree setup: T3 and T4 each branched directly from T0a's branch (disjoint files: T3 owns
`PromptComposer.cs`, T4 owns `AliasPolicy.cs`). T0c branched from T0a then merged T0b's branch in
— clean merge, no conflicts (T0a and T0b touched disjoint files: `ProxyOptions.cs`/`ProxyService
.cs`/`PromptComposer.cs` vs. `Program.cs`/`LlmProxy.Tests/Integration*.cs`).

Dispatched: T0c, T3, T4 — all in parallel, each given T0a's decisions log verbatim (including the
T4 tripwire-test instruction) so they build on the real prior art rather than guessing.

- **T3 → done.** 36/36 green (27 + 9 new). `Anchor` implemented in `PromptComposer.Apply`;
  `Passthrough` was already correct from T0a's stub. `IdentityAnchorTests.cs` confirmed zero diff
  via `git diff --stat`.
- **T0c → done.** 34/34 green. `InboundAuth.TryResolve` happy path, `/v1/*`-scoped middleware,
  resolved caller on `HttpContext.Items`, no rate limiter registered at all (deliberately, to not
  prejudge T2's shape). Left explicit contracts for T1 (own all rejection-shape distinctions) and
  T2 (`RateLimitPartition.KeyFor` by-App rule).

## Iteration 3 — opened

T0c's completion unlocks T1, T2, T6 simultaneously (all blocked-by T0c alone). Each owns a
distinct new file (`InboundAuth.cs` real logic / `RateLimitPartition.cs` real logic /
`StartupValidation.cs` real logic) — parallel-safe. All three worktrees branched directly from
`nightshift/t0c`.

Dispatched: T1, T2, T6 — each given T0c's decisions log verbatim (the `CallerItemKey` convention,
the "auth must run before rate limiting" ordering dependency, the call-site-already-exists note
for T6) so they build on the real contract rather than guessing.

- **T4 → done.** 33/33 (AliasRoutingTests filter: 4/4). Pin-first wired into `BuildCandidatesAsync`,
  promoted only if present in the live catalog (never-dead-end preserved), outranks `_lastGood`.
  `ModelPrefer` reuses `ApplyPreferOverride` unchanged, gated on `alias is not null`. Updated T0a's
  self-flagged tripwire test as instructed.

## Iteration 4 — opened

T4's completion unlocks T5 (blocked-by T0a, T4 — both terminal). T5 shares `AliasPolicy.cs` with
T4, so its worktree branches directly from `nightshift/t4` (sequenced, not parallel — by design,
this is the one deliberate same-file dependency on the board).

Dispatched: T5.

- **T6 → done.** 41/41 (34 + 7 new). Collect-all-violations, not fail-fast. No `Program.cs` change
  needed — T0c's call site was already correctly placed. Dedicated test proves the raw key string
  never leaks into an exception message.
- **T1 → done.** 58/58 (34 + 24 new). `TryResolve` returns a richer enum but all rejection reasons
  collapse to one generic 401 body (can't-probe-key-shapes). Alias-grant enforcement placed in
  `ProxyService.ForwardJsonAsync`, not the header-only middleware — deliberate, documented. **⚠
  Touched `ProxyService.cs`, independently also touched by T4 from a different branch off T0a —
  flagged as a real merge-night conflict, not mechanical.**
- **T2 → done.** 39/39 (34 + 5 new). Hand-rolled fixed-window counter over `AddRateLimiter`
  (simpler for nullable per-partition budgets). Ordering enforced by gating on the auth-populated
  `HttpContext.Items` key being present. **⚠ Touched `ProxyOptions.cs`** (new
  `RateLimitWindowSeconds`, collides with T4's independent `ProxyOptions.cs` edit off T0a — likely
  mechanical, additive fields) **and `Program.cs`** (new block after T1's edit region — likely but
  not guaranteed mechanical).

With T1, T2, T3, T4, T6 all terminal, only **T5** stands between the board and T10. T6+T7+T9 all
terminal unlocked **T11** — worktree built by merging all three branches (T6 as base, then T7's
branch, then T9's branch); all three merges were clean, zero conflicts.

Dispatched: T5 (retry after infra interruption, see below), T11.

### Interruption — session limit hit mid-run

Both T5's and T11's first dispatch were terminated early by an API session-limit error (reset
6:30am Europe/Stockholm), before either had written or committed anything — confirmed via `git
status`/`git log` on both worktrees showing clean trees at their branch points. Re-dispatched both
fresh into the same worktrees/branches with the same briefs; no state was lost, nothing to
reconcile.

- **T11 → done.** Script + runbook, `bash -n` clean. Indirect check for "no personal prompt file"
  (absence of the local-mode announce banner in a completion response) since there's no real
  file-listing HTTP surface to check directly — reasoning documented in the script.
- **T5 → done.** 37/37 (33 + 4 new). Found and fixed a genuine pre-existing bug: a `Math.Max(5,
  ...)` timeout floor (predates this feature) that would have silently overridden any alias
  timeout under 5s — lowered to `Math.Max(1, ...)`.

## Iteration 5 — the big merge (T10)

All six of T1–T6 terminal unlocks T10, which needs all of them merged to document real behavior.
Built T10's worktree by hand (not delegated — reconciling `ProxyService.cs`/`ProxyOptions.cs`/
`Program.cs` correctly needed understanding what each ticket built, not mechanical merging):

`nightshift/t5` (T0a+T4+T5) → merge `nightshift/t3` (clean) → merge `nightshift/t1` (auto-merged
`ProxyService.cs`, verified both T1's alias-grant check and T5's timeout fix survived — 77/77
green) → merge `nightshift/t2` (auto-merged `Program.cs` + `ProxyOptions.cs`, verified middleware
ordering: auth before rate-limiter, gated on `HttpContext.Items` — **but surfaced a real
collision**: 4 of T2's `RateLimitTests` failed with 400 instead of 200, because they hardcoded
`model: "auto"` against a key granting only alias `"fast"` — stale fixtures written before T1's
alias-grant enforcement existed. Not a logic conflict: `InboundAuth.CheckAliasGrant` only checks
membership in the key's own `Aliases` list, so this was a one-line test-fixture fix (`"auto"` →
`"fast"`), applied and committed directly (`3073624`) — no assertion weakened. → 82/82 green) →
merge `nightshift/t6` (clean) → **89/89 green, all six tickets reconciled on one branch.**

This confirms the two other flagged risks (`ProxyOptions.cs` T2×T4, `Program.cs` T1×T2) really
were mechanical — git's three-way merge resolved both without markers, and no test broke because
of them; only the T1×T2 *test-fixture* interaction needed a human-judgment fix, and it was a
one-liner once diagnosed.

Dispatched: T10, into the fully-merged worktree, instructed to write docs against actual code +
`board.md`'s decisions (ground truth), not the original ticket text.

- **T10 → done.** 89/89 (docs-only, no test change expected). `## Service mode` section written
  against actual merged code + board.md decisions, not the original ticket text. Correctly avoided
  referencing `infra/`/workflows/`scripts/smoke.sh` since T10's own branch lineage (T1–T6 off
  T0a/T0b/T0c) never merged T7/T8/T9/T11 — those files genuinely aren't on that branch. Flagged
  (correctly, without fixing — docs-only scope) an apparent board.md status inconsistency, whose
  actual cause was this orchestrator's live edits never being committed mid-run — fixed by this
  commit landing them.

## Board fully drained — all 14 tickets terminal

| Ticket | Result |
|---|---|
| T0a | done — effective-policy seam, 27/27 |
| T0b | done — integration harness, 22/22 |
| T0c | done — pipeline wiring, 34/34 |
| T1 | done — inbound authority edges, 58/58 |
| T2 | done — rate limiting, 39/39 |
| T3 | done — prompt modes, 36/36 |
| T4 | done — pin-first + prefer, 33/33 |
| T5 | done — per-alias timeout, 37/37 (+ 1 pre-existing bug fixed) |
| T6 | done — startup validation, 41/41 |
| T7 | done — production config layer, 25/25 |
| T8 | done — infra Bicep |
| T9 | done — deploy + keep-warm workflows |
| T10 | done — README, 89/89 (merged branch) |
| T11 | done (hitl) — smoke script + runbook |

Zero tickets flagged. One genuine pre-existing bug found and fixed (T5, timeout floor). One
real merge-time collision found and fixed (the T10 merge, T2's stale test fixtures vs. T1's
enforcement — one-line fix, no assertion weakened). Two other flagged collision risks
(`ProxyOptions.cs` T2×T4, `Program.cs` T1×T2) turned out to be genuinely mechanical.

**Branch inventory for /merge-night**: the fully-reconciled `nightshift/t10` branch already
contains T0a, T0b, T0c, T1, T2, T3, T4, T5, T6 merged together (89/89 green) at worktree
`.claude/worktrees/nightshift-t10`. Still needs T7 (`worktree-agent-a9a0752fd2b874fdf`), T8
(`worktree-agent-a4e721d37ce7eab39`), T9 (`nightshift/t9`, includes T8), and T11
(`nightshift/t11`, already merges T6+T7+T9) folded in — T11's branch is actually the
superset for the infra/deploy side (T6+T7+T9 already merged into it), so the remaining merge
for /merge-night is essentially `nightshift/t10` + `nightshift/t11`, which together cover all
fourteen tickets.

Run **/merge-night** to assemble the final `qa/service-mode` branch.

---

## Merge report — /merge-night

**Base:** `main` (commit `124bb9f`, includes the board.md/log.md persistence commit).
**Output:** branch `qa/service-mode`, worktree `.claude/worktrees/qa-service-mode`.

### Merge sequence

Rather than merge all fourteen ticket branches individually, two already-assembled superset
branches from the nightshift run were merged instead — each already covers a cluster of tickets
and was already proven green on its own:

1. **`nightshift/t10`** (T0a, T0b, T0c, T1, T2, T3, T4, T5, T6 — assembled and proven 89/89 green
   during nightshift's iteration 5) → **clean merge**, no conflicts.
2. **`nightshift/t11`** (T6, T7, T8, T9, T11 — T6 shared with t10's lineage, so only T7/T8/T9/T11's
   diffs applied) → **auto-merged with one trivial conflict**, in `README.md`: T10 and T11 each
   added a distinct top-level section, but every cross-reference each made to the other's content
   assumed it didn't exist yet (since neither ticket's worktree ever saw the other's work). Not a
   textual conflict git flagged — both sections merged mechanically — but the *result* read wrong:
   T10's "Deployment" pointer said "once those exist in this repository" (they now did), and T11's
   runbook intro said "once every behavioural ticket is done" (it now was). **Resolved** by
   relocating "Service mode: first deploy" to sit directly after the "Service mode" section it
   belongs next to, and rewriting both stale forward-references — no content removed, only
   cross-references and heading placement. Commit `cd020b8`.

No branch was held back. Every ticket's work is present on `qa/service-mode`.

### Integration check (beyond each ticket's own green)

- `dotnet test LlmProxy.Tests` → **94/94 green** (up from T10's 89 — the +5 is T7's
  `ProductionConfigTests`, which only entered the tree via the T11 merge).
- `az bicep build --file infra/main.bicep` → clean compile.
- Both GitHub Actions workflow YAML files parse.
- `bash -n scripts/smoke.sh` → clean.
- `dotnet publish -c Release -r linux-x64 --self-contained true` → succeeds (the actual command
  `deploy.yml` runs).

### Consolidated diff vs. `main`

30 files changed, 3180 insertions(+), 28 deletions(-) — `git diff main...qa/service-mode --stat`.
New: `AliasPolicy.cs`, `InboundAuth.cs`, `RateLimitPartition.cs`, `StartupValidation.cs`, 9 new
test files, `appsettings.Production.json`, `infra/main.bicep` + `main.bicepparam` +
`setup-oidc.sh`, `.github/workflows/deploy.yml` + `keepwarm.yml`, `scripts/smoke.sh`. Modified:
`ProxyOptions.cs`, `ProxyService.cs`, `PromptComposer.cs`, `Program.cs`, `TestHost.cs`,
`FakeUpstream.cs`, `README.md`.

### DAG-smell note for /kanban next time

The three collisions flagged during nightshift (`ProxyService.cs` T1×T4/T5, `ProxyOptions.cs`
T2×T4, `Program.cs` T1×T2) all resolved as git auto-merges with zero conflict markers — the T0c
split correctly kept them file-disjoint enough for three-way merge to work. The one real
human-judgment fix needed at merge time was **T2's test fixtures predating T1's alias-grant
enforcement** (a runtime behavior interaction between two tickets' tests, not a textual merge
conflict — nothing in /kanban's file-collision analysis would have caught this ahead of time,
since it's a semantic dependency between T1 and T2's *behavior*, not their files). Worth noting
for future boards: when two tickets both gate the same request pipeline (auth + rate limiting),
consider a fixture-sharing convention or sequencing one behind the other's test data, even when
their *code* files are disjoint.

Run **/qa-plan** to review `qa/service-mode`.

### Known merge-night risk (flagging now, not fixing tonight)

Three spine files were independently touched by tickets that branched from a common ancestor
without seeing each other's changes: `ProxyService.cs` (T1 vs. T4), `ProxyOptions.cs` (T2 vs. T4),
`Program.cs` (T1 vs. T2, same T0c ancestor). This is the cost of the parallel fan-out after T0c —
each ticket's brief said "stay inside your file," but T1 and T2 both needed small, well-justified
crossovers into shared territory. None of this blocks the night (each ticket is green on its own
branch); it's /merge-night's job to reconcile, and it's flagged here so that step isn't a surprise.
