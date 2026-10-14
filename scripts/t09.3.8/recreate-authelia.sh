#!/usr/bin/env bash
set -euo pipefail

script_dir=$(cd "$(dirname "$0")" && pwd)
source "$script_dir/_docker.sh"
compose_file=''
project_directory=''
lldap_container='lldap'
authelia_service='authelia'
execute=false

usage() {
  echo "Uso: $0 --compose-file ARQUIVO --project-directory DIR [--lldap-container NOME] [--authelia-service SERVICO] [--execute]" >&2
}

while (($#)); do
  case "$1" in
    --compose-file) compose_file=${2:-}; shift 2 ;;
    --project-directory) project_directory=${2:-}; shift 2 ;;
    --lldap-container) lldap_container=${2:-}; shift 2 ;;
    --authelia-service) authelia_service=${2:-}; shift 2 ;;
    --execute) execute=true; shift ;;
    *) usage; exit 64 ;;
  esac
done

[[ -f "$compose_file" && -d "$project_directory" ]] || { usage; exit 64; }

# Esta verificação é deliberadamente executada imediatamente antes do único comando autorizado a recriar o Authelia.
bash "$script_dir/lldap-healthcheck.sh" --container "$lldap_container"

if [[ "$execute" != true ]]; then
  echo "Pré-flight aprovado. Simulação: o Authelia NÃO foi recriado (adicione --execute no cutover autorizado)."
  exit 0
fi

if [[ "${ALLOW_T09_3_8_CUTOVER:-}" != 'yes' ]]; then
  echo 'Recusado: defina ALLOW_T09_3_8_CUTOVER=yes somente na janela de mudança autorizada.' >&2
  exit 77
fi

docker_cli compose --project-directory "$project_directory" -f "$compose_file" up -d --no-deps --force-recreate "$authelia_service"
