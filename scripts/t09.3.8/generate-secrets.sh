#!/usr/bin/env bash
set -euo pipefail

usage() {
  echo "Uso: $0 --output-dir DIRETORIO [--force] [--include-lab-authelia]" >&2
}

output_dir=''
force=false
include_lab_authelia=false
while (($#)); do
  case "$1" in
    --output-dir) output_dir=${2:-}; shift 2 ;;
    --force) force=true; shift ;;
    --include-lab-authelia) include_lab_authelia=true; shift ;;
    *) usage; exit 64 ;;
  esac
done

[[ -n "$output_dir" ]] || { usage; exit 64; }
[[ "$output_dir" != '/' ]] || { echo 'Recusado: diretório raiz.' >&2; exit 64; }
command -v openssl >/dev/null || { echo 'openssl é obrigatório.' >&2; exit 69; }

umask 077
mkdir -p "$output_dir"
chmod 700 "$output_dir"

files=(
  lldap_key_seed
  lldap_jwt_secret
  lldap_admin_password
  lldap_bind_password
  diego_initial_password
)

# Somente o laboratório Authelia isolado precisa de chaves novas. Em uma
# migração real, as chaves existentes do Authelia devem ser preservadas.
if [[ "$include_lab_authelia" == true ]]; then
  files+=(authelia_session_secret authelia_storage_encryption_key)
fi

for name in "${files[@]}"; do
  target="$output_dir/$name"
  if [[ -e "$target" && "$force" != true ]]; then
    echo "Recusado: $target já existe; use --force somente em laboratório." >&2
    exit 73
  fi
done

for name in "${files[@]}"; do
  target="$output_dir/$name"
  temporary="$target.tmp.$$"
  # Secret files must not contain a trailing newline. Besides being safer for
  # consumers that read bytes verbatim, this keeps LLDAP's key seed identical
  # for the server and the password bootstrap helper.
  openssl rand -hex 32 | tr -d '\r\n' >"$temporary"
  chmod 600 "$temporary"
  mv -f "$temporary" "$target"
done

echo "Segredos gravados com permissão restrita em $output_dir; nenhum valor foi exibido."
