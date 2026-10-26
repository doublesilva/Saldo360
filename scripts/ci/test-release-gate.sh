#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
gate="$(mktemp)"
summary="$(mktemp)"
trap 'rm -f -- "$gate" "$summary"' EXIT
# Execute the actual inline deploy gate, not a duplicate implementation.
awk '
  /response="\$\(gh api/ { capture=1 }
  /- name: Checkout deploy SHA/ { exit }
  capture { sub(/^          /, ""); print }
' "$root/.github/workflows/deploy.yml" > "$gate"
grep -Fq 'ci-full-success' "$gate"
export DEPLOY_SHA=1111111111111111111111111111111111111111
export GITHUB_REPOSITORY=synthetic/ci-test GITHUB_STEP_SUMMARY="$summary"
gh() {
  [[ "$*" == *"repos/$GITHUB_REPOSITORY/commits/$DEPLOY_SHA/check-runs?per_page=100"* ]] || return 99
  printf '%s\n' "$CHECKS"
}
export -f gh
check() {
  jq -nc --argjson id "$1" --arg name "$2" --arg status "$3" --arg conclusion "$4" --arg app "${5:-github-actions}" \
    '{id:$id,name:$name,status:$status,conclusion:(if $conclusion=="null" then null else $conclusion end),app:{slug:$app},details_url:"https://example.invalid/check"}'
}
assert_gate() {
  local expected=$1 name=$2
  shift 2
  CHECKS="$(printf '%s\n' "$@" | jq -sc '{check_runs:.}')"
  export CHECKS
  if bash -euo pipefail "$gate" >/dev/null 2>&1; then
    [ "$expected" = pass ] || { echo "release gate unexpectedly accepted: $name" >&2; exit 1; }
  else
    [ "$expected" = fail ] || { echo "release gate unexpectedly rejected: $name" >&2; exit 1; }
  fi
}
ci="$(check 10 ci-success completed success)"
full="$(check 20 ci-full-success completed success)"
assert_gate fail fast-only "$ci"
assert_gate pass full-same-sha "$ci" "$full"
assert_gate pass ordinary-skipped-marker-does-not-mask-full "$ci" "$full" "$(check 30 ci-full-success completed skipped)"
assert_gate fail newer-full-failed "$ci" "$full" "$(check 30 ci-full-success completed failure)"
assert_gate fail full-still-running "$ci" "$full" "$(check 30 ci-full-success in_progress null)"
assert_gate fail foreign-provider "$ci" "$(check 20 ci-full-success completed success foreign)"
assert_gate fail failed-ci "$(check 10 ci-success completed failure)" "$full"
assert_gate fail foreign-ci "$(check 10 ci-success completed success foreign)" "$full"
assert_gate fail newer-ci-running "$ci" "$(check 40 ci-success in_progress null)" "$full"
echo 'FULL release gate contract: 9/9 passed; no remote request or deployment'
