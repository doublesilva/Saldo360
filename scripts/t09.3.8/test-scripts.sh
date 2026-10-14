#!/usr/bin/env bash
set -euo pipefail

root=$(cd "$(dirname "$0")/../.." && pwd)
scripts="$root/scripts/t09.3.8"
work=$(mktemp -d)
trap 'rm -rf -- "$work"' EXIT

fail() { echo "FALHA: $*" >&2; exit 1; }
pass() { echo "ok   $*"; }

echo '== geração de segredos sem exposição =='
output=$(bash "$scripts/generate-secrets.sh" --output-dir "$work/secrets" --include-lab-authelia)
for name in lldap_key_seed lldap_jwt_secret lldap_admin_password lldap_bind_password diego_initial_password authelia_session_secret authelia_storage_encryption_key; do
  [[ -s "$work/secrets/$name" ]] || fail "$name não foi criado"
  [[ "$(wc -c <"$work/secrets/$name")" -eq 64 ]] || fail "$name contém tamanho ou quebra de linha inesperada"
  secret=$(<"$work/secrets/$name")
  [[ "$output" != *"$secret"* ]] || fail "$name apareceu na saída"
done
pass 'segredos criados sem conteúdo no stdout'

bash "$scripts/generate-secrets.sh" --output-dir "$work/production-secrets" >/dev/null
[[ ! -e "$work/production-secrets/authelia_session_secret" ]] || fail 'gerador padrão tentou substituir segredo do Authelia'
[[ ! -e "$work/production-secrets/authelia_storage_encryption_key" ]] || fail 'gerador padrão tentou substituir chave do storage Authelia'
pass 'modo padrão preserva os segredos existentes do Authelia'

echo '== backup, cobertura e restauração =='
mkdir -p "$work/data" "$work/backups"
printf 'sqlite-lab-v1' >"$work/data/users.db"
printf 'config-lab-v1' >"$work/data/lldap_config.toml"
bash "$scripts/backup-lldap.sh" --data-dir "$work/data" --secrets-dir "$work/secrets" --output-dir "$work/backups" >/dev/null
archive=$(find "$work/backups" -name 'lldap-*.tar.gz' -print | head -1)
[[ -n "$archive" ]] || fail 'arquivo de backup ausente'
bash "$scripts/verify-backup-coverage.sh" "$work/backups" >/dev/null
printf 'sqlite-alterado' >"$work/data/users.db"
printf 'seed-alterada' >"$work/secrets/lldap_key_seed"
bash "$scripts/restore-lldap.sh" --archive "$archive" --data-dir "$work/data" --secrets-dir "$work/secrets" --execute >/dev/null
[[ "$(<"$work/data/users.db")" == 'sqlite-lab-v1' ]] || fail 'users.db não foi restaurado'
[[ "$(<"$work/secrets/lldap_key_seed")" != 'seed-alterada' ]] || fail 'key seed não foi restaurada'
pass 'backup íntegro e restauração byte a byte'

echo '== pré-flight obrigatório =='
mkdir -p "$work/bin" "$work/project"
touch "$work/compose.yml"
cat >"$work/bin/docker" <<'MOCK'
#!/usr/bin/env bash
set -euo pipefail
echo "$*" >>"$MOCK_DOCKER_LOG"
if [[ "${1:-}" == inspect ]]; then echo "${MOCK_HEALTH:-unhealthy}"; exit 0; fi
if [[ "${1:-}" == exec ]]; then [[ "${MOCK_HEALTH:-unhealthy}" == healthy ]]; exit; fi
exit 0
MOCK
chmod +x "$work/bin/docker"
export PATH="$work/bin:$PATH" MOCK_DOCKER_LOG="$work/docker.log"

export MOCK_HEALTH=unhealthy
if bash "$scripts/recreate-authelia.sh" --compose-file "$work/compose.yml" --project-directory "$work/project" --execute 2>/dev/null; then
  fail 'pré-flight aceitou LLDAP unhealthy'
fi
grep -q '^compose ' "$work/docker.log" && fail 'Authelia foi recriado com LLDAP unhealthy'
pass 'LLDAP unhealthy bloqueia antes do compose'

: >"$work/docker.log"
export MOCK_HEALTH=healthy ALLOW_T09_3_8_CUTOVER=yes
bash "$scripts/recreate-authelia.sh" --compose-file "$work/compose.yml" --project-directory "$work/project" --execute >/dev/null
grep -q '^compose .*up -d --no-deps --force-recreate authelia$' "$work/docker.log" || fail 'recriação autorizada não ocorreu após health válido'
pass 'LLDAP healthy libera somente a recriação explicitamente autorizada'

echo 'Todos os testes de scripts passaram.'
