#!/usr/bin/env bash
set -euo pipefail

# Synthetic configuration only. No containers, real .env, secrets, DB or storage.
export POSTGRES_DB=saldo360_ci POSTGRES_USER=saldo360 POSTGRES_PASSWORD=ci-only-not-a-secret
export SALDO360_IMAGE_TAG=1111111111111111111111111111111111111111
export SALDO360_PUBLIC_HOST=saldo360.example.invalid
export FINANCIAL_WORKSPACE_ID='' FINANCIAL_WORKSPACE_CUTOVER_PLAN=''
export FINANCIAL_DOCUMENT_CLASSIFICATION_ENABLED=false FINANCIAL_DOCUMENT_CLASSIFICATION_AGENT_ENABLED=false
export SERVICE_BILL_EXTRACTION_ENABLED=false SERVICE_BILL_EXTRACTION_AGENT_ENABLED=false
export SERVICE_BILL_RECONCILIATION_ENABLED=false SERVICE_BILL_CONFIRMATION_ENABLED=false SERVICE_BILL_BINDING_ENABLED=false
compose=(docker compose --env-file .env.production.example --profile migration -f docker-compose.production.yml)

"${compose[@]}" config --format json | python3 -c '
import json, sys
c = json.load(sys.stdin); api = c["services"]["api"]["environment"]; m = c["services"]["migrate"]
assert not any(k.startswith("FinancialEmailImport__Sources__") for k in api)
assert api["FinancialEmailImport__Enabled"] == "false"
assert not m["environment"].get("FINANCIAL_WORKSPACE_CUTOVER_PLAN")
assert "workspace_cutover_plan" not in m["environment"]["ConnectionStrings__Saldo360"]
print("Clean install: no workspace destination or cutover plan required")
'
if "${compose[@]}" -f docker-compose.collectors.yml config --quiet 2>/dev/null; then
  echo 'Collector overlay accepted missing destination' >&2; exit 1
fi
export FINANCIAL_WORKSPACE_ID=11111111-1111-4111-8111-111111111111
"${compose[@]}" -f docker-compose.collectors.yml config --format json | python3 -c '
import json, sys
e = json.load(sys.stdin)["services"]["api"]["environment"]
assert e["FinancialEmailImport__Enabled"] == "false"
for i in range(6):
 p = "FinancialEmailImport__Sources__%d__" % i
 assert e[p+"WorkspaceId"] == "11111111-1111-4111-8111-111111111111"
 assert e[p+"Enabled"] == "false"
print("Configured destination: all six manual profiles and scheduler remain disabled")
'

# Exercise the EXACT resolved shell entrypoint with a stub bundle, not a database.
tmp="$(mktemp -d)"
trap 'rm -rf -- "$tmp"' EXIT
export FINANCIAL_WORKSPACE_CUTOVER_PLAN='[{"family_id":"22222222-2222-4222-8222-222222222222","workspace_id":"11111111-1111-4111-8111-111111111111","owner_user_id":"33333333-3333-4333-8333-333333333333"}]'
"${compose[@]}" config --format json | python3 -c '
import json, sys, pathlib
c = json.load(sys.stdin)["services"]["migrate"]
assert c["environment"]["FINANCIAL_WORKSPACE_CUTOVER_PLAN"]
# Compose config re-escapes dollars for a reusable Compose representation.
# Container creation removes that escaping before handing the string to sh.
s = c["entrypoint"][3].replace("$$", "$")
assert s.count("/app/efbundle") == 1
pathlib.Path(sys.argv[1]).write_bytes(s.replace("/app/efbundle", "printf '\''%s\\n'\''").encode())
' "$tmp/entrypoint"
unset FINANCIAL_WORKSPACE_CUTOVER_PLAN
export ConnectionStrings__Saldo360='Host=synthetic;Database=clean'
sh -eu "$tmp/entrypoint" previous > "$tmp/actual"
printf '%s\n' --connection "$ConnectionStrings__Saldo360" previous > "$tmp/expected"
cmp "$tmp/actual" "$tmp/expected"
export FINANCIAL_WORKSPACE_CUTOVER_PLAN='[{"synthetic":"plan with spaces"}]'
sh -eu "$tmp/entrypoint" previous > "$tmp/actual"
printf '%s\n' --connection "$ConnectionStrings__Saldo360;Options=-c saldo360.workspace_cutover_plan=$FINANCIAL_WORKSPACE_CUTOVER_PLAN" previous > "$tmp/expected"
cmp "$tmp/actual" "$tmp/expected"
unset ConnectionStrings__Saldo360
if sh -eu "$tmp/entrypoint" > /dev/null 2>&1; then echo 'Missing connection accepted' >&2; exit 1; fi
echo 'Migration entrypoint: optional plan, exact argument forwarding and fail-closed connection verified'
