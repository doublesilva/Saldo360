#!/usr/bin/env bash
set -euo pipefail

readonly SOURCE_WRAPPER="${1:-deploy/server/deploy-saldo360-wrapper}"
readonly VALID_SHA='0123456789abcdef0123456789abcdef01234567'
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

staging="$tmp/srv/saldo360-deploy"
source_release="$tmp/release-source"
archive="$staging/saldo360-release.tar.gz"
manifest="$staging/saldo360-release-manifest.txt"
signature="$staging/saldo360-release-manifest.sig"
public_key="$tmp/etc/saldo360/deploy-release-public.pem"
private_key="$tmp/signing-key.pem"
wrapper="$tmp/deploy-saldo360"
invoked="$tmp/invoked"
mkdir -p "$staging" "$source_release/deploy/server" "$(dirname "$public_key")" "$tmp/run"

# O wrapper não possui overrides em runtime. O teste altera uma cópia para usar
# uma árvore descartável, mantendo o mesmo fluxo e as mesmas validações.
sed \
  -e "s|^readonly STAGING_ROOT=.*|readonly STAGING_ROOT='$staging'|" \
  -e "s|^readonly PUBLIC_KEY=.*|readonly PUBLIC_KEY='$public_key'|" \
  -e "s|^readonly TRUSTED_RUNTIME_ROOT=.*|readonly TRUSTED_RUNTIME_ROOT='$tmp/run'|" \
  -e "s|require_real_directory '/srv'|require_real_directory '$tmp/srv'|" \
  "$SOURCE_WRAPPER" > "$wrapper"
chmod 0755 "$wrapper"

openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:2048 -out "$private_key" >/dev/null 2>&1
openssl pkey -in "$private_key" -pubout -out "$public_key" >/dev/null 2>&1
chmod 0644 "$public_key"

write_release_script() {
  mkdir -p "$source_release/deploy/server"
  cat > "$source_release/deploy/server/deploy-saldo360" <<SCRIPT
#!/usr/bin/env bash
set -euo pipefail
test "\${SALDO360_RELEASE_DIR}" = "\$(dirname \$(dirname \$(dirname \$0)))"
printf '%s' "\$1" > '$invoked'
SCRIPT
  chmod 0755 "$source_release/deploy/server/deploy-saldo360"
}

sign_release() {
  local sha="${1:-$VALID_SHA}"
  local release_hash script_hash
  tar -czf "$archive" -C "$source_release" .
  release_hash="$(sha256sum "$archive" | awk '{print $1}')"
  script_hash="$(sha256sum "$source_release/deploy/server/deploy-saldo360" | awk '{print $1}')"
  printf 'protocol=1\ncommit=%s\nrelease_sha256=%s\nscript_sha256=%s\n' \
    "$sha" "$release_hash" "$script_hash" > "$manifest"
  openssl dgst -sha256 -sign "$private_key" -out "$signature" "$manifest"
}

reset_release() {
  rm -rf "$source_release" "$archive" "$manifest" "$signature" "$invoked"
  mkdir -p "$source_release/deploy/server"
  write_release_script
  sign_release
}

expect_failure() {
  if "$@" >/dev/null 2>&1; then
    printf 'esperava falha: %q ' "$@" >&2
    printf '\n' >&2
    exit 1
  fi
}

[ "$($wrapper --protocol-version)" = '1' ]

reset_release
UNTRUSTED_VALUE='ignored' "$wrapper" "$VALID_SHA"
[ "$(cat "$invoked")" = "$VALID_SHA" ]

reset_release
expect_failure "$wrapper" 'abc'
[ ! -e "$invoked" ]
expect_failure "$wrapper" '0123456789ABCDEF0123456789ABCDEF01234567'
expect_failure "$wrapper" "$VALID_SHA" 'extra'

reset_release
rm "$source_release/deploy/server/deploy-saldo360"
tar -czf "$archive" -C "$source_release" .
release_hash="$(sha256sum "$archive" | awk '{print $1}')"
printf 'protocol=1\ncommit=%s\nrelease_sha256=%s\nscript_sha256=%064d\n' \
  "$VALID_SHA" "$release_hash" 0 > "$manifest"
openssl dgst -sha256 -sign "$private_key" -out "$signature" "$manifest"
expect_failure "$wrapper" "$VALID_SHA"
[ ! -e "$invoked" ]

reset_release
mv "$source_release/deploy/server/deploy-saldo360" "$tmp/real-script"
ln -s "$tmp/real-script" "$source_release/deploy/server/deploy-saldo360"
sign_release
expect_failure "$wrapper" "$VALID_SHA"
[ ! -e "$invoked" ]

reset_release
printf '\n# adulterado\n' >> "$archive"
expect_failure "$wrapper" "$VALID_SHA"
[ ! -e "$invoked" ]

reset_release
printf 'protocol=2\ncommit=%s\nrelease_sha256=%s\nscript_sha256=%s\n' \
  "$VALID_SHA" "$(sha256sum "$archive" | awk '{print $1}')" \
  "$(sha256sum "$source_release/deploy/server/deploy-saldo360" | awk '{print $1}')" > "$manifest"
openssl dgst -sha256 -sign "$private_key" -out "$signature" "$manifest"
expect_failure "$wrapper" "$VALID_SHA"
[ ! -e "$invoked" ]

reset_release
sign_release 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'
expect_failure "$wrapper" "$VALID_SHA"
[ ! -e "$invoked" ]

reset_release
STAGING_ROOT="$tmp/attacker" PUBLIC_KEY="$tmp/attacker.pem" "$wrapper" "$VALID_SHA"
[ "$(cat "$invoked")" = "$VALID_SHA" ]

reset_release
mv "$staging" "$tmp/real-staging-directory"
ln -s "$tmp/real-staging-directory" "$staging"
expect_failure "$wrapper" "$VALID_SHA"
[ ! -e "$invoked" ]

printf 'deploy-saldo360-wrapper: todos os testes passaram\n'
