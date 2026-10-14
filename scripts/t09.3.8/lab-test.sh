#!/usr/bin/env bash
set -euo pipefail

root=$(cd "$(dirname "$0")/../.." && pwd)
cd "$root"
lab='dev/t09.3.8'
compose="$lab/docker-compose.yml"
runtime="$lab/runtime"
scripts='scripts/t09.3.8'
source "$scripts/_docker.sh"

if [[ -e "$runtime" ]]; then
  echo "Laboratório já existe em $runtime. Remova-o conscientemente antes de repetir o ensaio." >&2
  exit 73
fi

cleanup() {
  docker_cli compose -f "$compose" --profile file --profile ldap down >/dev/null 2>&1 || true
}
trap cleanup EXIT

mkdir -p "$runtime/lldap-data" "$runtime/authelia-data" "$runtime/backups"
bash "$scripts/generate-secrets.sh" --output-dir "$runtime/secrets" --include-lab-authelia

echo '== LLDAP isolado e provisionamento fictício =='
docker_cli compose -f "$compose" up -d --wait lldap
MSYS_NO_PATHCONV=1 docker_cli compose -f "$compose" exec -T \
  -e USER_CONFIGS_DIR=/bootstrap/user-configs \
  -e GROUP_CONFIGS_DIR=/bootstrap/group-configs \
  -e DO_CLEANUP=false \
  lldap sh -c 'LLDAP_ADMIN_PASSWORD=$(cat /secrets/lldap_admin_password); export LLDAP_ADMIN_PASSWORD; exec /app/bootstrap.sh' >/dev/null

login_status() {
  local password=$1
  printf '{"username":"diego","password":"%s"}' "$password" |
    curl -sS -o /dev/null -w '%{http_code}' -H 'Content-Type: application/json' --data-binary @- \
      http://127.0.0.1:17171/auth/simple/login
}

old_password=$(<"$runtime/secrets/diego_initial_password")
[[ "$(login_status "$old_password")" == 200 ]] || { echo 'Login LLDAP inicial falhou.' >&2; exit 1; }

admin_password=$(<"$runtime/secrets/lldap_admin_password")
login=$(printf '{"username":"admin","password":"%s"}' "$admin_password" |
  curl -sS -H 'Content-Type: application/json' --data-binary @- http://127.0.0.1:17171/auth/simple/login)
unset admin_password
token=$(printf '%s' "$login" | sed -n 's/.*"token":"\([^"]*\)".*/\1/p')
[[ -n "$token" ]] || { echo 'Token administrativo LLDAP ausente.' >&2; exit 1; }
profile=$(printf '%s' '{"query":"query { user(userId: \"diego\") { id email displayName groups { displayName } } }"}' |
  curl -sS -H 'Content-Type: application/json' -H "Authorization: Bearer $token" --data-binary @- \
    http://127.0.0.1:17171/api/graphql)
unset token login
[[ "$profile" == *'"id":"diego"'* && "$profile" == *'"displayName":"home-admin"'* ]] || {
  echo 'Usuário ou grupo esperado não encontrado no LLDAP.' >&2; exit 1;
}

echo '== Backend file e captura do identificador opaco =='
docker_cli compose -f "$compose" --profile file up -d --wait authelia-file
opaque='42c7424a-3d61-4d8c-8cee-91ea5a8d3709'
MSYS_NO_PATHCONV=1 docker_cli compose -f "$compose" exec -T authelia-file authelia storage user identifiers add diego \
  --identifier "$opaque" --config /config/configuration.yml >/dev/null
MSYS_NO_PATHCONV=1 docker_cli compose -f "$compose" exec -T authelia-file authelia storage user identifiers export \
  --file /data/opaque-before.yml --config /config/configuration.yml >/dev/null
MSYS_NO_PATHCONV=1 docker_cli compose -f "$compose" exec -T authelia-file grep -q "$opaque" /data/opaque-before.yml
docker_cli compose -f "$compose" stop authelia-file >/dev/null

echo '== Cutover de laboratório protegido pelo pré-flight =='
ALLOW_T09_3_8_CUTOVER=yes bash "$scripts/recreate-authelia.sh" \
  --compose-file "$compose" --project-directory "$lab" \
  --lldap-container saldo360-t0938-lldap --authelia-service authelia-ldap --execute >/dev/null
MSYS_NO_PATHCONV=1 docker_cli compose -f "$compose" exec -T authelia-ldap authelia storage user identifiers export \
  --file /data/opaque-after.yml --config /config/configuration.yml >/dev/null
MSYS_NO_PATHCONV=1 docker_cli compose -f "$compose" exec -T authelia-ldap \
  cmp /data/opaque-before.yml /data/opaque-after.yml

echo '== Backup, alteração de senha e recuperação =='
bash "$scripts/backup-lldap.sh" --data-dir "$runtime/lldap-data" --secrets-dir "$runtime/secrets" \
  --output-dir "$runtime/backups" --container saldo360-t0938-lldap --execute >/dev/null
archive=$(find "$runtime/backups" -name 'lldap-*.tar.gz' -print | sort | tail -1)
bash "$scripts/verify-backup-coverage.sh" "$runtime/backups" >/dev/null
openssl rand -hex 32 | tr -d '\r\n' >"$runtime/secrets/diego_initial_password.tmp"
chmod 600 "$runtime/secrets/diego_initial_password.tmp"
mv "$runtime/secrets/diego_initial_password.tmp" "$runtime/secrets/diego_initial_password"
MSYS_NO_PATHCONV=1 docker_cli compose -f "$compose" exec -T \
  -e USER_CONFIGS_DIR=/bootstrap/user-configs \
  -e GROUP_CONFIGS_DIR=/bootstrap/group-configs \
  -e DO_CLEANUP=false \
  lldap sh -c 'LLDAP_ADMIN_PASSWORD=$(cat /secrets/lldap_admin_password); export LLDAP_ADMIN_PASSWORD; exec /app/bootstrap.sh' >/dev/null
new_password=$(<"$runtime/secrets/diego_initial_password")
[[ "$(login_status "$new_password")" == 200 ]] || { echo 'Nova senha não autenticou.' >&2; exit 1; }
[[ "$(login_status "$old_password")" != 200 ]] || { echo 'Senha anterior continuou válida.' >&2; exit 1; }
unset new_password

bash "$scripts/restore-lldap.sh" --archive "$archive" --data-dir "$runtime/lldap-data" \
  --secrets-dir "$runtime/secrets" --container saldo360-t0938-lldap --execute >/dev/null
[[ "$(login_status "$old_password")" == 200 ]] || { echo 'Senha do backup não foi recuperada.' >&2; exit 1; }
unset old_password

echo '== Rollback de laboratório para file =='
docker_cli compose -f "$compose" stop authelia-ldap >/dev/null
docker_cli compose -f "$compose" --profile file up -d --wait authelia-file
MSYS_NO_PATHCONV=1 docker_cli compose -f "$compose" exec -T authelia-file authelia storage user identifiers export \
  --file /data/opaque-rollback.yml --config /config/configuration.yml >/dev/null
MSYS_NO_PATHCONV=1 docker_cli compose -f "$compose" exec -T authelia-file \
  cmp /data/opaque-before.yml /data/opaque-rollback.yml

echo 'Laboratório aprovado: login, grupo, cutover, sub opaco, senha, backup/restauração e rollback.'
