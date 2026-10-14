#!/usr/bin/env bash
set -euo pipefail

script_dir=$(cd "$(dirname "$0")" && pwd)
source "$script_dir/_docker.sh"

archive=''
data_dir=''
secrets_dir=''
container=''
execute=false

usage() {
  echo "Uso: $0 --archive ARQUIVO --data-dir DIR --secrets-dir DIR [--container NOME] --execute" >&2
}

while (($#)); do
  case "$1" in
    --archive) archive=${2:-}; shift 2 ;;
    --data-dir) data_dir=${2:-}; shift 2 ;;
    --secrets-dir) secrets_dir=${2:-}; shift 2 ;;
    --container) container=${2:-}; shift 2 ;;
    --execute) execute=true; shift ;;
    *) usage; exit 64 ;;
  esac
done

[[ "$execute" == true ]] || { echo 'Recusado: restauração exige --execute.' >&2; exit 77; }
[[ -f "$archive" && -n "$data_dir" && -n "$secrets_dir" ]] || { usage; exit 64; }
[[ "$data_dir" != '/' && "$secrets_dir" != '/' ]] || { echo 'Recusado: caminho raiz.' >&2; exit 64; }
[[ -f "$archive.sha256" ]] || { echo 'Checksum ausente.' >&2; exit 66; }
(cd "$(dirname "$archive")" && sha256sum -c "$(basename "$archive").sha256") >/dev/null

umask 077
stage=$(mktemp -d)
tar -C "$stage" -xzf "$archive"
[[ -f "$stage/lldap/data/users.db" ]] || { echo 'Backup sem users.db.' >&2; exit 65; }
[[ -s "$stage/lldap/secrets/lldap_key_seed" ]] || { echo 'Backup sem LLDAP_KEY_SEED.' >&2; exit 65; }
[[ -s "$stage/lldap/secrets/lldap_jwt_secret" ]] || { echo 'Backup sem LLDAP_JWT_SECRET.' >&2; exit 65; }

timestamp=$(date -u +%Y%m%dT%H%M%SZ)
stopped=false
cleanup() {
  rm -rf -- "$stage"
  if [[ "$stopped" == true ]]; then docker_cli start "$container" >/dev/null; fi
}
trap cleanup EXIT

if [[ -n "$container" ]]; then
  docker_cli stop --time 30 "$container" >/dev/null
  stopped=true
fi

if [[ -e "$data_dir" ]]; then mv -- "$data_dir" "$data_dir.pre-restore-$timestamp"; fi
if [[ -e "$secrets_dir" ]]; then mv -- "$secrets_dir" "$secrets_dir.pre-restore-$timestamp"; fi
mkdir -p "$data_dir" "$secrets_dir"
cp -a "$stage/lldap/data/." "$data_dir/"
cp -a "$stage/lldap/secrets/." "$secrets_dir/"
chmod 700 "$secrets_dir"

if [[ "$stopped" == true ]]; then
  docker_cli start "$container" >/dev/null
  stopped=false
  for _ in {1..30}; do
    if bash "$(dirname "$0")/lldap-healthcheck.sh" --container "$container" >/dev/null 2>&1; then break; fi
    sleep 1
  done
  bash "$(dirname "$0")/lldap-healthcheck.sh" --container "$container" >/dev/null
fi

echo "LLDAP restaurado. Cópias anteriores preservadas com sufixo .pre-restore-$timestamp."
