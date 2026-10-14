#!/usr/bin/env bash
set -euo pipefail

script_dir=$(cd "$(dirname "$0")" && pwd)
source "$script_dir/_docker.sh"

data_dir=''
secrets_dir=''
output_dir=''
container=''
execute=false

usage() {
  echo "Uso: $0 --data-dir DIR --secrets-dir DIR --output-dir DIR [--container NOME --execute]" >&2
}

while (($#)); do
  case "$1" in
    --data-dir) data_dir=${2:-}; shift 2 ;;
    --secrets-dir) secrets_dir=${2:-}; shift 2 ;;
    --output-dir) output_dir=${2:-}; shift 2 ;;
    --container) container=${2:-}; shift 2 ;;
    --execute) execute=true; shift ;;
    *) usage; exit 64 ;;
  esac
done

[[ -d "$data_dir" && -d "$secrets_dir" && -n "$output_dir" ]] || { usage; exit 64; }
[[ "$data_dir" != '/' && "$secrets_dir" != '/' && "$output_dir" != '/' ]] || { echo 'Recusado: caminho raiz.' >&2; exit 64; }
[[ -f "$data_dir/users.db" ]] || { echo "users.db ausente em $data_dir." >&2; exit 66; }
[[ -s "$secrets_dir/lldap_key_seed" ]] || { echo 'LLDAP_KEY_SEED ausente.' >&2; exit 66; }
[[ -s "$secrets_dir/lldap_jwt_secret" ]] || { echo 'LLDAP_JWT_SECRET ausente.' >&2; exit 66; }
if [[ -n "$container" && "$execute" != true ]]; then
  echo 'Recusado: parar container exige --execute.' >&2
  exit 77
fi

umask 077
mkdir -p "$output_dir"
chmod 700 "$output_dir"
timestamp=$(date -u +%Y%m%dT%H%M%SZ)
archive="$output_dir/lldap-$timestamp.tar.gz"
stage=$(mktemp -d)
stopped=false

cleanup() {
  rm -rf -- "$stage"
  if [[ "$stopped" == true ]]; then
    docker_cli start "$container" >/dev/null
  fi
}
trap cleanup EXIT

if [[ -n "$container" ]]; then
  bash "$(dirname "$0")/lldap-healthcheck.sh" --container "$container"
  docker_cli stop --time 30 "$container" >/dev/null
  stopped=true
fi

mkdir -p "$stage/lldap/data" "$stage/lldap/secrets"
cp -a "$data_dir/." "$stage/lldap/data/"
cp -a "$secrets_dir/." "$stage/lldap/secrets/"
printf 'created_utc=%s\nformat=t09.3.8-v1\n' "$timestamp" >"$stage/lldap/manifest.txt"
tar -C "$stage" -czf "$archive.partial" lldap
mv "$archive.partial" "$archive"
(cd "$(dirname "$archive")" && sha256sum "$(basename "$archive")" >"$(basename "$archive").sha256")

if [[ "$stopped" == true ]]; then
  docker_cli start "$container" >/dev/null
  stopped=false
  for _ in {1..30}; do
    if bash "$(dirname "$0")/lldap-healthcheck.sh" --container "$container" >/dev/null 2>&1; then break; fi
    sleep 1
  done
  bash "$(dirname "$0")/lldap-healthcheck.sh" --container "$container" >/dev/null
fi

echo "Backup LLDAP criado: $archive"
echo 'O artefato contém segredos: envie-o somente ao backup externo criptografado.'
