#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
scratch="$(mktemp -d)"
cleanup() {
  rm -f -- "$scratch/detect.sh" "$scratch/output" "$scratch/summary" "$scratch/changed-files.txt"
  rmdir -- "$scratch"
}
trap cleanup EXIT
# Exercise the workflow's actual selection, not a duplicate branch implementation.
awk '
  /- name: Detect changed components/ { detect=1 }
  detect && /run: \|/ { capture=1; next }
  capture && /^  backend:/ { exit }
  capture { sub(/^          /, ""); print }
' "$root/.github/workflows/ci.yml" > "$scratch/detect.sh"
# Match the literal workflow assignment, not this test's environment.
# shellcheck disable=SC2016
grep -Fq 'release_full=$full' "$scratch/detect.sh"
export RUNNER_TEMP="$scratch" GITHUB_OUTPUT="$scratch/output" GITHUB_STEP_SUMMARY="$scratch/summary"
export PR_BASE_SHA='' PUSH_BEFORE_SHA='' HEAD_SHA=1111111111111111111111111111111111111111
cd "$root"
assert_selection() {
  local event=$1 area=$2 expect_full=$3 expect_release=$4 expect_ocr=$5
  : > "$GITHUB_OUTPUT"
  : > "$GITHUB_STEP_SUMMARY"
  EVENT_NAME="$event" BENCHMARK_AREA="$area" bash "$scratch/detect.sh" >/dev/null
  grep -Fxq "backend=true" "$GITHUB_OUTPUT"
  grep -Fxq "frontend=true" "$GITHUB_OUTPUT"
  grep -Fxq "infra=true" "$GITHUB_OUTPUT"
  grep -Fxq "full=$expect_full" "$GITHUB_OUTPUT"
  grep -Fxq "release_full=$expect_release" "$GITHUB_OUTPUT"
  grep -Fxq "ocr=$expect_ocr" "$GITHUB_OUTPUT"
}
assert_selection workflow_dispatch none true true true
assert_selection schedule none true true true
assert_selection workflow_dispatch goals false false false
grep -Fxq 'affected_filter=(Area=Dashboard|Area=Goals|Area=Scenarios)&Profile!=FAST' "$GITHUB_OUTPUT"
assert_selection workflow_dispatch documents false false true
grep -Fxq 'affected_filter=(Area=Documents|Area=Services)&Profile!=FAST' "$GITHUB_OUTPUT"
assert_selection workflow_dispatch cards false false true
assert_selection workflow_dispatch security false false true
assert_selection push none true true true # Missing base always falls back to FULL.
if EVENT_NAME=workflow_dispatch BENCHMARK_AREA=untrusted bash "$scratch/detect.sh" >/dev/null 2>&1; then
  echo 'Untrusted benchmark choice was accepted' >&2
  exit 1
fi
# A benchmark must never create the successful check required by the deploy gate.
awk '/^  ci-full-success:/ { capture=1 } capture { print }' "$root/.github/workflows/ci.yml" |
  grep -Fq "needs.changes.outputs.release_full == 'true'"
echo 'Workflow selection: 8 scenarios passed; default/schedule FULL and non-release benchmarks verified'
