# PRD: service-mode

Reference doc for the feature settled in `brief.md`. Authoritative on *what*, *why*, module
boundaries, and definition of done. Tickets in `board.md` cite sections here.

## Problem statement

Three of the human's applications need hosted LLM inference and are pivoting to NVIDIA NIM's free
tier, but NIM's catalog is unstable: models are withdrawn, renamed, or return errors under a
200 envelope, and per-model pools saturate. Each app would otherwise reimplement the same
failover, cooldown, and catalog-drift handling — and each would need its own copy of the NVIDIA
credential.

This repo already solves that problem, but only for one caller on one machine. It has zero inbound
authentication (by design — the LM Studio plugin sends no credentials), and it owns the system
prompt at the *provider* level with a personal developer-identity prompt plus an identity anchor,
stripping whatever the client sent. Pointed at a second consumer, it would silently replace that
app's prompt with the human's personal one. Its model aliases are too thin to express per-app
policy, and one of them is inert: on a provider using dynamic model discovery, the alias's
configured upstream model is ignored entirely. There is no deployment path, and the committed
configuration binds to localhost.

## Solution

Add a **service mode** to the same binary: an authenticated, multi-consumer front end over the
existing routing engine, deployable to Azure App Service on the free tier.

Consumers authenticate with a per-application bearer key. Each key is bound to the set of
**aliases** it may use, so a typo cannot silently fall through to another app's routing identity.
Aliases are promoted from a thin provider+model mapping into full **routing profiles** carrying
their own prompt policy, model preference, pinned model, and attempt timeout. Per-key rate
limiting protects the shared upstream quota. The existing failover machinery — dynamic candidate
discovery, cooldown benching, tool-capability learning, declarative routing rules, and the
never-dead-end guarantees — is reused unchanged beneath all of it.

Local behaviour is frozen: with no keys configured, the proxy behaves exactly as it does today, so
the LM Studio setup keeps working untouched and stays on the local instance.

## User stories

1. As the **operator**, I want each application to hold its own credential, so that a leak is
   contained to one app and revoking it doesn't disrupt the others.
2. As the **operator**, I want a key to be usable only with the aliases I granted it, so that a
   misconfigured app gets a clear error instead of silently borrowing another app's identity and
   model policy.
3. As the **operator**, I want to run two live keys for one application at once, so that rotating a
   credential is a deploy rather than an outage.
4. As the **operator**, I want key material to never appear in logs or error responses, so that
   diagnostics and log aggregation are not a credential-disclosure path.
5. As the **operator**, I want per-application request budgets, so that one app cannot exhaust the
   shared upstream quota or run away unattended if its key leaks.
6. As the **operator**, I want the service to refuse to start in production without inbound keys,
   so that a misconfigured deploy cannot come up wide open on a public URL.
7. As the **operator**, I want configuration mistakes (a key granting an alias that doesn't exist,
   an alias naming an unknown provider) to fail at startup, so that they surface at deploy time
   rather than as a runtime rejection at 05:00.
8. As the **operator**, I want the NVIDIA credential to exist only on the server, so that no
   consuming application or browser ever holds it.
9. As the **operator**, I want to deploy from a push to the default branch with no long-lived cloud
   credential stored in the forge, so that the deployment path isn't itself a standing secret.
10. As the **operator**, I want infrastructure expressed as code, so that the environment is
    reproducible and its free-tier constraints are explicit.
11. As the **news digest batch**, I want to change endpoint, model name, and key and nothing else,
    so that migrating costs no code — its client library binds a static credential once at
    startup.
12. As the **news digest batch**, I want my own system prompt delivered to the model verbatim, so
    that summaries are not polluted by the proxy's identity text.
13. As the **news digest batch**, I want throttling signalled with a standard status and retry
    hint, so that my existing rate-limit-aware retry handles it without new code.
14. As the **portfolio assistant**, I want to keep my own assistant persona while gaining a
    guarantee that the answering model won't claim to be a specific commercial product, so that a
    model swap mid-conversation doesn't produce an identity contradiction to a visitor.
15. As the **portfolio assistant**, I want the proxy to answer without a model-name banner
    prepended to replies, so that visitors see the assistant's voice and not proxy diagnostics.
16. As the **blockchain agent**, I want a per-alias attempt timeout far longer than the default, so
    that a slow reasoning model's healthy response isn't abandoned and silently failed over to a
    fast non-reasoning model — which would quietly change a risk decision.
17. As the **blockchain agent**, I want my preferred reasoning model attempted first with failover
    still available behind it, so that I normally get the model I chose but never dead-end when
    it's withdrawn.
18. As the **blockchain agent**, I want my request body — structured-output directives, temperature,
    and all other fields — passed through untouched, so that my strict-JSON contract holds.
19. As **any consumer**, I want a stable OpenAI-compatible surface, so that standard client
    libraries work unmodified.
20. As the **LM Studio user**, I want the local proxy's behaviour to be bit-for-bit unchanged, so
    that a feature aimed at other apps doesn't regress my daily driver.
21. As a **night agent**, I want the routing engine declared off limits and the new seams named, so
    that I extend the system without destabilising finished work.

## Acceptance criteria (definition of done)

Carried from the brief; verification sharpened where the existing harness cannot reach (see
*Testing strategy*). "Unit" = a directly-constructed module, no web host. "Integration" = a request
driven through the real application pipeline.

**Backward compatibility**
- [ ] With no inbound keys configured, behaviour is unchanged from today — verified by: the entire
      pre-existing test suite passing **without modification to any existing test**.
- [ ] With no inbound keys configured, an unauthenticated chat request succeeds — verified by:
      integration test.
- [ ] With no rate limits configured, no throttling occurs — verified by: integration test issuing
      a burst.

**Authentication and authorization**
- [ ] A request to any versioned API path with a missing, malformed, or unknown bearer key is
      rejected with 401 and an error body in the proxy's existing error envelope — verified by:
      integration test.
- [ ] The health and root endpoints answer without a credential — verified by: integration test
      (this is what the keep-warm schedule depends on).
- [ ] A valid key requesting an alias outside its grant is rejected with 400 whose message names
      the aliases that key may use — verified by: unit test on the authorization module plus one
      integration test for the wire shape.
- [ ] A key granting exactly one alias may omit the model field and is routed to that alias; a key
      granting several that omits the model is rejected with 400 — verified by: unit tests.
- [ ] Two distinct live keys mapping to the same application are both accepted — verified by: unit
      test.
- [ ] Neither logs nor error responses contain key material on any path, including rejection —
      verified by: integration test capturing log output and response bodies, asserting the key
      string is absent.

**Rate limiting**
- [ ] Requests beyond an application's configured budget are rejected with 429 carrying a
      retry hint header — verified by: integration test with a compressed window.
- [ ] Budgets partition by **application, not by key**: two live keys for one application share one
      budget — verified by: integration test (prevents rotation from silently doubling an app's
      allowance).
- [ ] One application exhausting its budget does not affect another's — verified by: integration
      test.
- [ ] A rate-limit rejection is decided before any upstream call and never benches a model in the
      routing state — verified by: integration test asserting zero upstream requests were captured
      and the routing state is unchanged.

**Alias routing profiles**
- [ ] An alias in passthrough prompt mode relays the client's messages unmodified — no message
      removed, none inserted, order and content preserved — verified by: unit test asserting on the
      captured upstream request body.
- [ ] An alias in anchor prompt mode preserves every client message and adds exactly one system
      message carrying the identity anchor, positioned after the client's last system message, or
      first when the client sent none — verified by: unit tests for both cases.
- [ ] An alias in owning prompt mode reproduces today's behaviour exactly (client system messages
      replaced by the composed provider prompt plus anchor) — verified by: the pre-existing
      identity-anchor tests continuing to pass unmodified.
- [ ] An alias with no prompt mode configured behaves as its provider does today — verified by:
      unit test.
- [ ] An alias's pinned upstream model is attempted first on a dynamic-discovery provider, and when
      it fails the request proceeds through the normal dynamic candidates rather than dead-ending —
      verified by: unit test asserting the captured attempt order.
- [ ] An alias's model preference reorders candidates for that alias's requests only, leaving
      non-aliased requests on the provider's ordering — verified by: unit test on attempt order.
- [ ] An alias's attempt timeout overrides the global value for that request only — verified by:
      unit test on effective-policy resolution, plus a behavioural test with compressed values
      where the request succeeds only because the alias's longer timeout applied.
- [ ] All other request-body fields are forwarded unmodified in every prompt mode — verified by:
      unit test asserting a structured-output directive and sampling parameters survive round-trip.

**Startup validation**
- [ ] In a production environment with no inbound keys configured, the host refuses to start —
      verified by: unit test on the validation routine.
- [ ] A key granting an alias that does not exist fails startup with a message naming the offender
      — verified by: unit test.
- [ ] An alias naming a provider that is not configured fails startup — verified by: unit test.

**Deployment**
- [ ] The production configuration layer disables the model-announcement banner, references no
      system-prompt file, and does not bind to loopback — verified by: unit test asserting the
      bound options in that layer.
- [ ] The infrastructure definition compiles — verified by: the cloud CLI's build command exiting
      zero, run in CI.
- [ ] A push to the default branch builds, tests, and deploys without a long-lived cloud credential
      stored in the forge — verified by: a green workflow run.
- [ ] **(HITL — requires the human's cloud login)** Against the deployed instance: the health
      endpoint answers 200; a correctly keyed chat completion returns a completion; a wrong key
      returns 401; the personal system-prompt file is absent from the deployed payload.

## Deep-module map

New and modified modules, each stated as a narrow interface over hidden complexity. Naming follows
the repo's existing vocabulary (alias, candidate, bench/cooldown, prefer bias, pin).

- **Inbound authority** — *interface:* two questions — "who is this caller?" (from an authorization
  header) and "may this caller use this alias?" (from caller identity plus the requested model),
  answering with either a resolved alias or a rejection carrying its reason and the permitted
  alias list. *Hides:* bearer scheme parsing and case handling; whether authentication is enabled
  at all (no keys configured ⇒ open, which is what preserves local behaviour); the several-keys-
  to-one-application rotation fact; the single-grant model-omission rule; and construction of
  rejection messages that must never echo key material. *Tested:* yes — pure and directly
  constructible; prior art is the existing routing-rule and prompt-composition unit tests.

- **Alias policy resolution** — *interface:* given a resolved alias, its provider, and the global
  options, return one effective policy for this request (prompt mode, pinned model, prefer
  patterns, attempt timeout). *Hides:* the three-level precedence chain (alias value, else provider
  value, else global default) for every setting, so no call site re-implements the fallback and the
  "unset alias behaves exactly like today" guarantee lives in one place. *Tested:* yes — pure, and
  it is where backward compatibility is proven.

- **Prompt composition** (extend existing) — *interface:* apply a prompt mode to the client's
  message list, given the provider's base prompt and the identity anchor. *Hides:* strip-versus-
  preserve, insertion position, the multiple-client-system-messages case, and the
  nothing-to-inject case. *Tested:* yes — strong prior art; today's identity-anchor tests already
  assert on the captured upstream message array and must keep passing as the owning-mode
  regression. **Note:** owning-mode logic currently lives inline in the forwarding path; moving it
  behind this interface is a deliberate deepening, not incidental refactoring.

- **Startup validation** — *interface:* validate the bound options against the hosting environment,
  throwing with a precise, actionable message. *Hides:* every cross-field consistency rule — keys
  referencing real aliases, aliases referencing real providers, production requiring keys, and the
  existing prompt-file existence check. *Tested:* yes — cheap and high value; converts a class of
  3am runtime failures into deploy-time failures.

- **Rate-limit partitioning** — *interface:* map a request to a partition key and a window. *Hides:*
  the application-not-key partitioning rule and the unconfigured-means-unlimited case. Thin by
  design — it adapts the framework's built-in limiter rather than reimplementing one. *Tested:*
  partition selection as a unit; the wire behaviour (status, retry hint, isolation) via
  integration, since the limiter only exists in the pipeline.

- **Forwarding path** (modify, minimally) — takes the effective policy as input instead of reading
  global options, and seeds the candidate list with the pinned model. The candidate-building,
  retry, failover, peek-and-classify, and relay logic is **not** restructured.

- **Integration harness** (new test infrastructure) — *interface:* start the real application over
  the scripted fake upstream and drive HTTP requests at it. *Hides:* host construction,
  configuration injection per test, and log capture. Required because the current harness
  constructs the forwarding service directly and therefore cannot observe middleware at all. This
  is the enabling dependency for every authentication and rate-limiting criterion.

## Configuration contract (this feature's "schema")

There is no database; the configuration surface *is* the data model, and it is an operator-facing
contract that must extend without breaking existing files. All additions are optional, and every
one of them absent must reproduce today's behaviour exactly.

| Addition | Meaning | Absent ⇒ |
|---|---|---|
| Inbound key record: application name | Attribution in logs and the rate-limit partition | — |
| Inbound key record: granted aliases | The only aliases this key may request | — |
| Inbound key record: request budget | Per-minute allowance for the owning application | No limit for that application |
| Any inbound keys at all | Enables authentication | Authentication disabled (today's local behaviour) |
| Alias: prompt mode | Passthrough, anchor, or owning | Provider-level behaviour (today) |
| Alias: pinned upstream model | First candidate attempted, failover behind it | Dynamic discovery only (today) |
| Alias: prefer patterns | Candidate ordering bias for this alias's requests | Provider's bias (today) |
| Alias: attempt timeout | Per-request upstream attempt bound | Global value (today) |

Backward compatibility: existing configuration files remain valid and behaviourally identical.
The one intended behaviour *change* to existing config is that an alias's configured upstream model
stops being ignored on dynamic-discovery providers — today's inert sample alias begins doing what
it already says. This is a bug fix, and it is called out because it is the single place where an
untouched config file changes behaviour.

Secrets — the upstream provider credential and all inbound keys — are supplied by environment in
the hosted environment and by the git-ignored local settings file in development. Neither is ever
committed, and the personal system-prompt file is not part of the deployed payload.

## Testing strategy

The existing harness constructs the forwarding service directly over a scripted fake upstream and
a pinned candidate chain. It is excellent for routing behaviour and is the right tool for prompt
modes, pinning, preference, and timeout resolution — all of which are decided inside the forwarding
path. It **cannot** exercise authentication, rate limiting, endpoint reachability, or startup
validation, because none of those exist below the pipeline.

Therefore: decision logic goes into pure modules testable with no web host (matching the repo's
existing preference for pure, directly-constructed components), and a small integration harness is
added for the handful of facts that are only true in the pipeline — rejection status and body
shape, unauthenticated reachability of health, throttling status and retry hint, budget isolation,
and absence of key material from logs. This adds a test-only dependency on the framework's
integration-testing package and requires the web host to expose its entry-point type to the test
project. Because everything authentication-related depends on it, the harness is built in the
walking skeleton and proven before any ticket relies on it.

## Vertical slices (preview of tickets)

- **T0 — walking skeleton.** One configured key granting one alias; one authenticated chat request
  driven through the real pipeline to the scripted upstream and back, green. Establishes every
  seam the rest plug into: the extended configuration binding, the authentication middleware
  position, the rate-limiter registration point, alias policy resolution reached from the
  forwarding path, and the integration harness itself. All other tickets block on this.
- **Authentication edges.** Rejection paths, health reachability, model-omission rules, multi-key
  rotation, and the no-key-in-logs guarantee.
- **Rate limiting.** Application-partitioned budgets, throttle response shape, isolation between
  applications, unconfigured-means-unlimited, and the no-upstream-call/no-benching guarantee.
- **Prompt modes.** Passthrough and anchor implemented behind prompt composition, with owning mode
  preserved as the regression.
- **Alias model targeting.** Pin-first-then-failover and per-alias preference ordering.
- **Per-alias attempt timeout.** Effective-policy resolution plus the behavioural proof.
- **Startup validation.** Production-requires-keys and the config consistency rules.
- **Production configuration layer.** Announcement banner off, no prompt file, non-loopback binding.
- **Infrastructure definition.** Free-tier plan and app, application settings wired to secrets,
  compiling in CI.
- **Deploy and keep-warm workflows.** Federated-credential deploy on push to default branch, plus
  the scheduled health ping.
- **Documentation.** Service-mode section covering operating both modes, key issuance and rotation,
  and the consumer migration map.
- **HITL — deployed smoke.** Requires the human's cloud login; verifies the deployed instance and
  the absence of the personal prompt file from the payload.

## Constraints & standing rules

- The shared upstream free tier allows roughly 40 requests per minute across every consumer plus
  the local instance. Per-application budgets must sum below it. Batch scheduling and staggering
  remain the consumers' responsibility, not the proxy's.
- Free-tier hosting: no always-on, so the instance idles out and the first request after idle pays
  a cold start of several seconds — acceptable to all three consumers, softened by the scheduled
  ping. A daily CPU allowance and a modest daily egress cap apply; every consumer is
  non-streaming JSON, so both are comfortable.
- Security posture in place of a token flow (settled in the brief, not to be relitigated): high-
  entropy per-application keys, never logged or echoed, several live keys per application for
  rotation, per-application budgets, and transport security from the platform.
- The routing engine is finished. Cooldown benching, tool-capability learning, declarative routing
  rules, request classification, and catalog handling are not to be restructured — service mode
  composes with them, it does not modify them.
- The never-dead-end guarantees are invariants: filters and biases may reorder or narrow
  candidates, but a request always attempts something. Pinning must not become a dead end.
- Local no-keys behaviour is frozen. No existing test may be modified to accommodate this feature;
  if one needs to change, that is a signal of an unintended behaviour change.
- Publish as a self-contained build for the hosting platform's architecture.
- Consumers are the human's own applications only — not a public service.

## Out of scope (non-goals)

- Anything in the villa project — that AI feature is being removed in its own repository. No alias
  for it.
- Embeddings routing. Service mode is chat-only; the existing embeddings passthrough is untouched
  and unused. Embedding-aware aliases (pinned model, no chat filtering, no cross-model failover)
  are a plausible follow-on, not this feature.
- Token-based authentication in any form: no login endpoint, no proxy-issued tokens, no external
  identity provider integration. Revisit only if the proxy serves something that isn't the human's
  own applications, or if end-user identity must reach it.
- Browser-direct access and the cross-origin configuration it would require; the portfolio's widget
  calls its own backend, which holds the key.
- Public access, signup, metering, billing.
- Distributed or shared routing state across instances; a single instance with in-memory state is
  the design.
- Activating the other pre-wired providers.
- Managed secret storage, custom domains, deployment slots.
- Filtering the model catalog response per key.
- **Any code change in a consumer repository.** The migration map in the brief is reference for
  later, separate work.
- The solar platform's contract generator, which stays on its current provider.

## Risks & open questions

- **The integration harness is new ground in this repo** and everything authentication-related
  depends on it → build it in the walking skeleton, prove it green before any dependent ticket
  starts; if it proves troublesome, the fallback is to push more decisions into pure modules and
  reduce pipeline tests to a minimal smoke set.
- **Rate limiting must partition by application, which requires authentication to have run first**
  → an explicit pipeline ordering dependency; pin it with the two-keys-one-budget test so a
  reordering regression is caught rather than silently doubling allowances.
- **Pinning could dead-end a request** if implemented as a filter rather than a bias → implement as
  seeding the front of the candidate list, never as exclusion; assert failover proceeds past a
  failed pin.
- **Prompt-mode work touches the one path every existing test exercises** → owning mode's existing
  tests are the regression gate and must pass unmodified.
- **A long per-alias timeout multiplies with the per-model retry count**, so the worst-case wall
  time for the agent's alias is larger than the timeout alone; the consuming application's own
  client timeout must accommodate it. Noted for that repo's migration, not solvable here.
- **Free-tier capacity may be refused in the preferred region** → fall back to the alternate region
  named in the brief; the infrastructure definition should make the region a parameter.
- **Self-contained publish size against free-tier storage** → expected to fit comfortably, but
  confirm at first deploy rather than assuming.
- **Authenticating the catalog endpoints changes their reachability.** Locally (no keys) nothing
  changes; hosted, the catalog and its refresh both require a key — intended, since an open cache
  buster is a cheap abuse vector.

## Blast radius

- **Fair game:** the options/configuration model; provider and alias resolution; the forwarding
  path's policy inputs and candidate seeding; prompt composition; application startup and
  middleware wiring; the committed base configuration (additive only); a new production
  configuration layer; new infrastructure and workflow definitions; the README; the test project.
- **Off limits:** the routing state (cooldown and tool-capability memory), the declarative routing
  rule evaluator, the request classifier, and the model catalog's internals — the routing engine is
  finished. The personal system-prompt file's contents. The documented LM Studio plugin workaround,
  which must keep working as written.
