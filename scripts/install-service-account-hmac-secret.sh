#!/usr/bin/env bash
# Run interactively with sudo. Creates only the dedicated secret, never prints its value.
set -euo pipefail
export PATH=/usr/sbin:/usr/bin:/sbin:/bin
umask 077
die() { printf 'service-account-secret: %s\n' "$*" >&2; exit 1; }
[ "$(id -u)" -eq 0 ] || die 'execute em sessão administrativa com sudo'
[ "$#" -eq 0 ] || die 'não recebe argumentos ou segredos'
readonly DIR=/etc/saldo360/secrets
readonly TARGET="$DIR/service-account-identifier-hmac-key"
[ ! -L /etc/saldo360 ] && [ ! -L "$DIR" ] && [ ! -L "$TARGET" ] || die 'symlink não permitido'
[ -d "$DIR" ] || die 'diretório de secrets existente obrigatório'
[ "$(stat -c %u "$DIR")" = 0 ] && [ "$(stat -c %a "$DIR")" = 700 ] || die 'diretório deve ser root-owned 0700'
command -v openssl >/dev/null
command -v python3 >/dev/null
if [ ! -e "$TARGET" ]; then
  # noclobber prevents replacing a key created concurrently. No key material reaches stdout.
  (set -o noclobber; openssl rand -base64 32 > "$TARGET") || die 'criação não concluída; revisar sem sobrescrever'
fi
[ -f "$TARGET" ] && [ "$(stat -c %h "$TARGET")" = 1 ] || die 'arquivo regular sem hardlinks obrigatório'
python3 - "$TARGET" <<'PY'
import base64, pathlib, sys
try:
    key = base64.b64decode(pathlib.Path(sys.argv[1]).read_bytes().strip(), validate=True)
    if len(key) != 32: raise ValueError()
except Exception:
    sys.exit('service-account-secret: chave existente inválida; não foi substituída')
PY
chown root:1654 "$TARGET"
chmod 0640 "$TARGET"
[ "$(stat -c '%u:%g:%a' "$TARGET")" = '0:1654:640' ] || die 'permissões incorretas'
printf 'Secret dedicado validado; root:1654 0640; diretório root:root 0700. Valor não exibido.\n'
printf 'Nenhum container reiniciado, flag alterada ou dado financeiro escrito.\n'
