#!/usr/bin/env bash
set -euo pipefail

backup_dir=${1:-}
[[ -d "$backup_dir" ]] || { echo "Uso: $0 DIRETORIO_DO_BACKUP_EXTERNO" >&2; exit 64; }

archive=$(find "$backup_dir" -maxdepth 1 -type f -name 'lldap-*.tar.gz' -print | sort | tail -1)
[[ -n "$archive" ]] || { echo 'Cobertura inválida: nenhum backup LLDAP encontrado.' >&2; exit 30; }
[[ -f "$archive.sha256" ]] || { echo 'Cobertura inválida: checksum ausente.' >&2; exit 31; }
(cd "$backup_dir" && sha256sum -c "$(basename "$archive").sha256") >/dev/null

members=$(tar -tzf "$archive")
grep -qx 'lldap/data/users.db' <<<"$members" || { echo 'Cobertura inválida: users.db ausente.' >&2; exit 32; }
grep -qx 'lldap/secrets/lldap_key_seed' <<<"$members" || { echo 'Cobertura inválida: LLDAP_KEY_SEED ausente.' >&2; exit 33; }
grep -qx 'lldap/secrets/lldap_jwt_secret' <<<"$members" || { echo 'Cobertura inválida: LLDAP_JWT_SECRET ausente.' >&2; exit 34; }

echo "Cobertura LLDAP válida: $(basename "$archive")"
