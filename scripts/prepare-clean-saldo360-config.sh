#!/usr/bin/env bash
# Administrative preparation only. DOES NOT reset, stop, deploy or access a database.
set -euo pipefail
export PATH=/usr/sbin:/usr/bin:/sbin:/bin
umask 077
die() { printf 'clean-config: %s\n' "$*" >&2; exit 1; }
[ "$(id -u)" -eq 0 ] || die 'execute com sudo em sessão administrativa'
[ "$#" -ge 1 ] && [ "$#" -le 2 ] && [ "$1" = PREPARAR_SALDO360_LIMPO ] || die 'uso: PREPARAR_SALDO360_LIMPO [workspace-real-retornado-pela-API]'
workspace="${2:-}"
if [ -n "$workspace" ]; then
  [[ "$workspace" =~ ^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$ ]] \
    && [ "${workspace//-/}" != 00000000000000000000000000000000 ] || die 'WorkspaceId inválido'
fi
readonly root=/srv/saldo360-deploy
readonly target="$root/.env"
[ "$(readlink -f -- "$root")" = "$root" ] && [ -d "$root" ] || die 'diretório operacional inesperado'
[ -f "$target" ] && [ ! -L "$target" ] || die '.env ausente ou link'
[ "$(readlink -f -- "$target")" = "$target" ] || die 'alvo inesperado'
[ "$(stat -c %U "$target")" = deploy ] || die 'owner operacional inesperado'
[ "$(stat -c %a "$target")" = 600 ] || die 'permissão operacional inesperada'
backup="$(mktemp "$root/.env.before-clean.XXXXXX")"
install -o root -g root -m 0600 "$target" "$backup"
prepared="$(mktemp "$root/.env.clean.XXXXXX")"
trap 'rm -f -- "$prepared"' EXIT
awk -v workspace="$workspace" '
BEGIN {
 v["FINANCIAL_WORKSPACE_ID"]=workspace;
 v["FINANCIAL_DOCUMENT_CLASSIFICATION_ENABLED"]="false";
 v["FINANCIAL_DOCUMENT_CLASSIFICATION_AGENT_ENABLED"]="false";
 v["SERVICE_BILL_EXTRACTION_ENABLED"]="false";
 v["SERVICE_BILL_EXTRACTION_AGENT_ENABLED"]="false";
 v["SERVICE_BILL_RECONCILIATION_ENABLED"]="false";
 v["SERVICE_BILL_CONFIRMATION_ENABLED"]="false";
 v["SERVICE_BILL_BINDING_ENABLED"]="false";
}
{
 k=$0; sub(/=.*/, "", k);
 if (k == "FINANCIAL_WORKSPACE_CUTOVER_PLAN") next;
 if (k in v) { if (!(k in seen)) print k "=" v[k]; seen[k]=1; }
 else print;
}
END { for (k in v) if (!(k in seen)) print k "=" v[k]; }
' "$target" > "$prepared"
chown deploy:deploy "$prepared"
chmod 0600 "$prepared"
mv -T -- "$prepared" "$target"
printf 'Configuração preparada: destino=%s, plano temporário removido e sete flags false.\n' "${workspace:-não configurado}"
printf 'Backup APENAS de configuração (root-only): %s\n' "$backup"
printf 'Nenhum container, banco ou documento alterado. Reload/reset ainda NÃO executados.\n'
