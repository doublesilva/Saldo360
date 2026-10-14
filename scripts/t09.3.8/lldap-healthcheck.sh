#!/usr/bin/env bash
set -euo pipefail

script_dir=$(cd "$(dirname "$0")" && pwd)
source "$script_dir/_docker.sh"

# O argumento /app/... pertence ao container e não deve ser convertido pelo Git Bash.
if [[ "$(uname -s)" == MINGW* ]]; then export MSYS_NO_PATHCONV=1; fi

container='lldap'
while (($#)); do
  case "$1" in
    --container) container=${2:-}; shift 2 ;;
    *) echo "Uso: $0 [--container NOME]" >&2; exit 64 ;;
  esac
done

status=$(docker_cli inspect --format '{{if .State.Health}}{{.State.Health.Status}}{{else}}missing{{end}}' "$container" 2>/dev/null || true)
if [[ "$status" != 'healthy' ]]; then
  echo "LLDAP indisponível: container '$container' com health '$status'." >&2
  exit 20
fi

if ! docker_cli exec "$container" /app/lldap healthcheck --config-file /data/lldap_config.toml >/dev/null 2>&1; then
  echo "LLDAP indisponível: healthcheck interno falhou em '$container'." >&2
  exit 21
fi

echo "LLDAP saudável ($container)."
