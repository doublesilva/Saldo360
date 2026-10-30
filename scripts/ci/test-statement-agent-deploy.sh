#!/usr/bin/env bash
set -euo pipefail

# Synthetic Compose config only; no production files, containers or provider calls.
export POSTGRES_DB=saldo360_ci POSTGRES_USER=saldo360 POSTGRES_PASSWORD=ci-only-not-a-secret
export SALDO360_IMAGE_TAG=1111111111111111111111111111111111111111 SALDO360_PUBLIC_HOST=saldo360.example.invalid
unset AI_AGENT_ENABLED AI_AGENT_MODEL
compose=(docker compose --env-file /dev/null --profile migration -f docker-compose.production.yml)

"${compose[@]}" config --format json | python3 -c '
import json,sys
c=json.load(sys.stdin); api=c["services"]["api"]; e=api["environment"]
assert e["AiAgent__Enabled"] == "false"
assert e["AiAgent__Model"] == "gpt-4o-mini"
assert e["AiAgent__Provider"] == "OpenAI"
assert e["AiAgent__ApiKeyFile"] == "/run/secrets/openai-api-key"
assert not any(k.startswith("CreditCardStatementAgent__") for k in e)
assert not any(k.lower().endswith("__apikey") or k == "OPENAI_API_KEY" for k in e)
mounts=[v for v in api["volumes"] if v["target"] == "/run/secrets/openai-api-key"]
assert len(mounts)==1 and mounts[0]["source"] == "/etc/jarvis/secrets/openai-api-key" and mounts[0]["read_only"]
assert "1654" in api["group_add"]
assert not any(v.get("target") == "/run/secrets/openai-api-key" for name,s in c["services"].items() if name != "api" for v in s.get("volumes",[]))
print("PASS: disabled defaults, exact individual read-only mount, no inline credential")
'
export AI_AGENT_ENABLED=true AI_AGENT_MODEL=synthetic-model
"${compose[@]}" config --format json | python3 -c '
import json,sys
e=json.load(sys.stdin)["services"]["api"]["environment"]
assert e["AiAgent__Enabled"] == "true"
assert e["AiAgent__Model"] == "synthetic-model"
assert not any(k.startswith(("FinancialDocumentClassification__","ServiceBill","CreditCardStatementAgent__")) for k in e)
print("PASS: explicit activation of the AI agent; no document/service-bill flags remain")
'

tmp="$(mktemp -d)"
trap 'rm -rf -- "$tmp"' EXIT
# Exercise the exact functions, replacing ONLY the hardcoded host path in a disposable copy.
sed -n '/^# BEGIN OpenAI secret preflight/,/^# END OpenAI secret preflight/p' deploy/server/deploy-saldo360 \
  | sed "s|/etc/jarvis/secrets/openai-api-key|$tmp/synthetic-secret|g" > "$tmp/probe"
source "$tmp/probe"
compose() { printf '%s\n' "$@" > "$tmp/args"; return "${reader_exit:-0}"; }
expect_failure() { if "$@" > "$tmp/log" 2>&1; then echo 'Expected secret preflight failure' >&2; exit 1; fi; }
expect_failure validate_openai_secret_file
test ! -e "$tmp/synthetic-secret" # No Docker bind creation or provisioning on missing source.
touch "$tmp/synthetic-secret"
expect_failure validate_openai_secret_file
printf '%s' 'SYNTHETIC-SECRET-MUST-NOT-APPEAR-IN-LOGS' > "$tmp/synthetic-secret"
validate_openai_secret_file
reader_exit=1
expect_failure validate_openai_secret_reader
! grep -q 'SYNTHETIC-SECRET-MUST-NOT-APPEAR-IN-LOGS' "$tmp/log"
reader_exit=0
validate_openai_secret_reader > "$tmp/log" 2>&1
! grep -q 'SYNTHETIC-SECRET-MUST-NOT-APPEAR-IN-LOGS' "$tmp/log"
grep -Fxq 'test -r /run/secrets/openai-api-key' <(sed 's/^ *//' "$tmp/args")
grep -Fxq 'test "$(id -u)" = 1654' <(sed 's/^ *//' "$tmp/args")
grep -Fxq 'test "$(id -g)" = 1654' <(sed 's/^ *//' "$tmp/args")
grep -Fxq '/bin/sh' "$tmp/args"
! grep -Eq 'chmod|chown|cat |cp |printenv|env$' "$tmp/probe"
python3 -c '
from pathlib import Path
s=Path("deploy/server/deploy-saldo360").read_text()
assert s.index("\nvalidate_openai_secret_file\n") < s.index("\ncompose build --pull api web\n")
assert s.index("\nvalidate_openai_secret_reader\n") < s.index("\npostgres_id=")
assert "compose up -d --wait api web" in s and "wait_healthy saldo360-api-candidate" in s
assert "pg_dump" in s and "deployed-sha" in s
'
echo 'PASS: missing/empty/unreadable secrets fail closed, safe diagnostics, release/health flow preserved'
