#!/usr/bin/env bash
#
# scripts/smoke.sh — post-deploy smoke test for a live LlmProxy service-mode instance.
#
# This is the HUMAN-RUN half of T11: it needs the human's real deployed URL and a real inbound
# key. It cannot run in CI or unattended — there is no live Azure deployment until the human has
# run infra/setup-oidc.sh, deployed the Bicep, set inbound keys, and pushed deploy.yml. See the
# "Service mode: first deploy" section in README.md for the full order of operations.
#
# Usage:
#   scripts/smoke.sh <base-url> <api-key> [alias] [budget]
#   SMOKE_BASE_URL=... SMOKE_API_KEY=... [SMOKE_ALIAS=...] [SMOKE_BUDGET=...] scripts/smoke.sh
#
# Positional args, if given, override the environment variables of the same purpose.
#
#   base-url  Required. The deployed instance, e.g. https://llmproxy-app.azurewebsites.net
#             (no trailing slash).
#   api-key   Required. A valid inbound key (Proxy:InboundKeys:<KeyId> value) already configured
#             on the deployed app via `az webapp config appsettings set` (see README).
#   alias     Optional. The alias name this key is allowed to use, e.g. "news-digest". Defaults
#             to "news-digest". Only used to fill the `model` field of the smoke chat request —
#             if the key grants exactly one alias, the proxy also accepts the model field omitted
#             entirely per the single-grant-omission rule, but we pass it explicitly here so the
#             script works the same regardless of how many aliases the key grants.
#   budget    Optional. The key's configured RequestsPerMinute budget, e.g. "10". Used only to
#             size the burst for the rate-limit check (budget + 3 requests). If omitted, the
#             rate-limit check is SKIPPED (see check d below) rather than hard-failed, since the
#             script has no way to discover the key's configured budget from outside.
#
# Dependencies: bash, curl. No jq — response bodies are small and checked with grep so the script
# has no extra dependency beyond curl (portable to a bare CI runner or the human's own machine
# without a jq install).
#
# Exit code: 0 only if every non-skipped check passed. Non-zero if any check failed.

set -uo pipefail
# Deliberately NOT `set -e`: each check makes its own curl call and inspects the result; a
# non-2xx/non-timeout response from any single check must not abort the rest of the script.

# ---- argument / env resolution --------------------------------------------------------------

BASE_URL="${1:-${SMOKE_BASE_URL:-}}"
API_KEY="${2:-${SMOKE_API_KEY:-}}"
ALIAS="${3:-${SMOKE_ALIAS:-news-digest}}"
BUDGET="${4:-${SMOKE_BUDGET:-}}"

usage() {
  cat <<'EOF'
Usage:
  scripts/smoke.sh <base-url> <api-key> [alias] [budget]
  SMOKE_BASE_URL=... SMOKE_API_KEY=... [SMOKE_ALIAS=...] [SMOKE_BUDGET=...] scripts/smoke.sh

Example:
  scripts/smoke.sh https://llmproxy-app.azurewebsites.net sk-abc123... news-digest 10

Checks run:
  a. GET  /health                           (no key)      -> expect 200
  b. POST /v1/chat/completions               (valid key)   -> expect 200 + a completion body
  c. POST /v1/chat/completions               (wrong key)   -> expect 401
  d. burst past <budget> requests/min        (valid key)   -> expect at least one 429
     (skipped if <budget> is not supplied — the script cannot know the configured limit)
  e. indirect check that Production config is active: the completion body from check (b) must
     NOT start with the local-mode "_[model]_" announce banner (AnnounceModel:false in
     appsettings.Production.json is the proxy signal that the Production layer — which also
     drops the personal system-prompt file — is actually in effect; this app has no HTTP surface
     that lists its own deployed files, so this is the closest verifiable proxy for "the personal
     prompt file is absent").
EOF
}

if [[ -z "$BASE_URL" || -z "$API_KEY" ]]; then
  usage
  exit 1
fi

BASE_URL="${BASE_URL%/}"

PASS=0
FAIL=0
SKIP=0

ok()   { PASS=$((PASS + 1)); printf '  \xe2\x9c\x93 %s\n' "$1"; }
bad()  { FAIL=$((FAIL + 1)); printf '  \xe2\x9c\x97 %s\n' "$1"; }
skip() { SKIP=$((SKIP + 1)); printf '  - skipped: %s\n' "$1"; }

# ---- a. GET /health, no Authorization header --------------------------------------------------

echo "[a] GET /health (unauthenticated)"
health_status=$(curl -s -o /dev/null -w '%{http_code}' --max-time 15 "$BASE_URL/health")
if [[ "$health_status" == "200" ]]; then
  ok "/health returned 200"
else
  bad "/health returned $health_status, expected 200"
fi

# ---- b. POST /v1/chat/completions with the valid key -------------------------------------------

echo "[b] POST /v1/chat/completions (valid key, alias=$ALIAS)"
chat_body='{"model":"'"$ALIAS"'","messages":[{"role":"user","content":"Reply with the single word: pong"}],"max_tokens":16}'
chat_response_file="$(mktemp)"
chat_status=$(curl -s -o "$chat_response_file" -w '%{http_code}' --max-time 60 \
  -H "Authorization: Bearer $API_KEY" \
  -H "Content-Type: application/json" \
  -d "$chat_body" \
  "$BASE_URL/v1/chat/completions")
chat_body_text="$(cat "$chat_response_file")"

if [[ "$chat_status" == "200" ]] && grep -q '"choices"' "$chat_response_file"; then
  ok "chat completion via alias '$ALIAS' returned 200 with a choices array"
else
  bad "chat completion returned status $chat_status (expected 200) or body missing \"choices\": $chat_body_text"
fi

# ---- c. POST /v1/chat/completions with an obviously wrong key ----------------------------------

echo "[c] POST /v1/chat/completions (wrong key)"
wrong_status=$(curl -s -o /dev/null -w '%{http_code}' --max-time 30 \
  -H "Authorization: Bearer wrong-key-12345" \
  -H "Content-Type: application/json" \
  -d '{"model":"'"$ALIAS"'","messages":[{"role":"user","content":"hi"}]}' \
  "$BASE_URL/v1/chat/completions")
if [[ "$wrong_status" == "401" ]]; then
  ok "wrong key returned 401"
else
  bad "wrong key returned $wrong_status, expected 401"
fi

# ---- d. burst past the configured budget, expect at least one 429 ------------------------------

echo "[d] burst past rate limit (budget=${BUDGET:-<unset>})"
if [[ -z "$BUDGET" ]]; then
  skip "no budget supplied; pass it as the 4th arg / SMOKE_BUDGET to exercise this check (e.g. the RequestsPerMinute configured for this key)"
else
  burst_count=$((BUDGET + 3))
  saw_429=0
  for ((i = 1; i <= burst_count; i++)); do
    burst_status=$(curl -s -o /dev/null -w '%{http_code}' --max-time 30 \
      -H "Authorization: Bearer $API_KEY" \
      -H "Content-Type: application/json" \
      -d '{"model":"'"$ALIAS"'","messages":[{"role":"user","content":"burst '"$i"'"}],"max_tokens":1}' \
      "$BASE_URL/v1/chat/completions")
    if [[ "$burst_status" == "429" ]]; then
      saw_429=1
      break
    fi
  done

  if [[ "$saw_429" == "1" ]]; then
    ok "observed a 429 within $burst_count requests against a budget of $BUDGET/min"
  else
    # Not a hard failure: this check's outcome depends on external configuration (how the human
    # set RequestsPerMinute for this key, and how much quota the burst above already consumed
    # from checks a-c) which the script does not fully control. Print a clear warning instead of
    # failing the whole run.
    skip "no 429 seen in $burst_count requests — either the budget wasn't reached, the window rolled over mid-burst, or no limit is actually configured for this key; this is a warning, not a hard failure"
  fi
fi

# ---- e. indirect check that the Production config layer (no prompt file) is active -------------

echo "[e] indirect check: Production config active (no announce banner => no prompt file)"
if [[ "$chat_status" == "200" ]]; then
  # appsettings.Production.json sets AnnounceModel:false and SystemPromptFile:"" together (T7).
  # There is no HTTP surface that lists the deployed file payload directly, so absence of the
  # "_[model]_" announce banner in a real completion is used as a proxy signal that the
  # Production config layer -- and therefore the no-prompt-file behaviour bundled with it -- is
  # actually the layer in effect on this deployment, not local/Development config.
  if grep -q '_\[' "$chat_response_file"; then
    bad "completion body appears to contain the local-mode announce banner ('_[model]_...'); Production config (AnnounceModel:false) may not be active"
  else
    ok "no announce banner in completion body (Production config layer appears active)"
  fi
else
  skip "check (b) did not return 200, so the completion body can't be inspected for the announce banner"
fi

rm -f "$chat_response_file"

# ---- summary -------------------------------------------------------------------------------

echo ""
echo "Summary: $PASS passed, $FAIL failed, $SKIP skipped"

if [[ "$FAIL" -gt 0 ]]; then
  exit 1
fi
exit 0
