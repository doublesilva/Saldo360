#!/usr/bin/env bash
set -euo pipefail

# Local/CI disposable Docker volume only; NEVER mount /etc/jarvis or production services.
docker_bin="${DOCKER_BIN:-docker}"
image=alpine:3.21
volume="saldo360-openai-preflight-test-$$-$RANDOM"
tmp="$(mktemp -d)"
cleanup() { "$docker_bin" volume rm "$volume" >/dev/null; rm -rf -- "$tmp"; }
trap cleanup EXIT
"$docker_bin" volume create "$volume" >/dev/null
sed -n '/^# BEGIN OpenAI secret preflight/,/^# END OpenAI secret preflight/p' deploy/server/deploy-saldo360 > "$tmp/probe"
source "$tmp/probe"
compose() {
  # Feed the EXACT release probe to a synthetic container with the same UID/GID and read-only mount.
  "$docker_bin" run --rm --network none --user 1654:1654 --group-add 1654 \
    --mount "type=volume,source=$volume,target=/run/secrets,readonly" \
    --entrypoint /bin/sh "$image" "${@:7}"
}
"$docker_bin" run --rm --network none --mount "type=volume,source=$volume,target=/run/secrets" \
  --entrypoint /bin/sh "$image" -eu -c '
    printf synthetic-not-a-credential > /run/secrets/openai-api-key
    chown 0:1654 /run/secrets/openai-api-key
    chmod 440 /run/secrets/openai-api-key
    test "$(stat -c %u:%g:%a /run/secrets/openai-api-key)" = 0:1654:440
  '
validate_openai_secret_reader
"$docker_bin" run --rm --network none --mount "type=volume,source=$volume,target=/run/secrets" \
  --entrypoint /bin/sh "$image" -eu -c '
    test "$(stat -c %u:%g:%a /run/secrets/openai-api-key)" = 0:1654:440
    chmod 400 /run/secrets/openai-api-key
  '
if validate_openai_secret_reader > "$tmp/log" 2>&1; then
  echo 'Expected API user to reject unreadable synthetic secret' >&2; exit 1
fi
! grep -q synthetic-not-a-credential "$tmp/log"
echo 'PASS: real Linux reader UID/GID 1654:1654 reads root:1654 0440; rejects 0400; no content exposed'
