#!/usr/bin/env bash
# Janela operacional T15.6.10: mesma imagem, apenas classificação/extração/matching.
set -euo pipefail
export PATH=/usr/sbin:/usr/bin:/sbin:/bin
umask 077

die() { printf 'readonly-config: %s\n' "$*" >&2; exit 1; }
[ "$(id -u)" -eq 0 ] || die 'execute com sudo em sessão administrativa'
[ "$#" -eq 1 ] || die 'uso: enable-service-bill-readonly.sh <sha-atualmente-implantado>'
readonly SHA="$1"
[[ "$SHA" =~ ^[0-9a-f]{40}$ ]] || die 'SHA inválido'
readonly ROOT=/srv/saldo360-deploy
readonly ENV_FILE="$ROOT/.env"
readonly PUBLIC_KEY=/etc/saldo360/deploy-release-public.pem
for file in "$ENV_FILE" "$PUBLIC_KEY" "$ROOT/saldo360-release.tar.gz" "$ROOT/saldo360-release-manifest.txt" "$ROOT/saldo360-release-manifest.sig"; do
  [ -f "$file" ] && [ ! -L "$file" ] || die 'arquivo operacional ausente ou link simbólico'
done
[ "$(stat -c %u "$PUBLIC_KEY")" = 0 ] || die 'chave pública não pertence ao root'
key_mode="$(stat -c %a "$PUBLIC_KEY")"
(( (8#$key_mode & 0022) == 0 )) || die 'permissões da chave pública inválidas'
command -v python3 >/dev/null
[ "$(docker inspect --format '{{.Config.Image}}' saldo360-api-1)" = "saldo360-api:$SHA" ] || die 'API executa outro SHA'
[ "$(docker inspect --format '{{.Config.Image}}' saldo360-web-1)" = "saldo360-web:$SHA" ] || die 'web executa outro SHA'

work="$(mktemp -d /run/saldo360-readonly-config.XXXXXX)"
trap 'rm -rf -- "$work"' EXIT
install -m 0400 "$ROOT/saldo360-release.tar.gz" "$work/release.tar.gz"
install -m 0400 "$ROOT/saldo360-release-manifest.txt" "$work/manifest"
install -m 0400 "$ROOT/saldo360-release-manifest.sig" "$work/signature"
openssl dgst -sha256 -verify "$PUBLIC_KEY" -signature "$work/signature" "$work/manifest" >/dev/null 2>&1 || die 'assinatura inválida'
[ "$(wc -l < "$work/manifest")" -eq 4 ] || die 'manifesto inválido'
expected="$(printf 'protocol=1\ncommit=%s\nrelease_sha256=%s' "$SHA" "$(sha256sum "$work/release.tar.gz" | awk '{print $1}')")"
[ "$(head -n 3 "$work/manifest")" = "$expected" ] || die 'pacote não corresponde ao SHA implantado'
mkdir -m 0700 "$work/release"
tar -xzf "$work/release.tar.gz" -C "$work/release" --no-same-owner --no-same-permissions
if find "$work/release" -type l -print -quit | grep -q .; then die 'release contém symlink'; fi
export SALDO360_IMAGE_TAG="$SHA"

# Não imprime nem altera outras variáveis ou segredos do arquivo de ambiente.
awk '
BEGIN {
  value["FINANCIAL_DOCUMENT_CLASSIFICATION_ENABLED"]="true";
  value["SERVICE_BILL_EXTRACTION_ENABLED"]="true";
  value["SERVICE_BILL_RECONCILIATION_ENABLED"]="true";
  value["FINANCIAL_DOCUMENT_CLASSIFICATION_AGENT_ENABLED"]="false";
  value["SERVICE_BILL_EXTRACTION_AGENT_ENABLED"]="false";
  value["SERVICE_BILL_CONFIRMATION_ENABLED"]="false";
  value["SERVICE_BILL_BINDING_ENABLED"]="false";
}
{
  key=$0; sub(/=.*/, "", key);
  if (key in value) { if (!(key in seen)) print key "=" value[key]; seen[key]=1; }
  else print;
}
END { for (key in value) if (!(key in seen)) print key "=" value[key]; }
' "$ENV_FILE" > "$work/new.env"

compose() {
  docker compose --project-name saldo360 --env-file "$1" --project-directory "$work/release" -f "$work/release/docker-compose.production.yml" "${@:2}"
}
compose "$work/new.env" config --format json | python3 -c '
import json, sys
env = json.load(sys.stdin)["services"]["api"]["environment"]
expected = {
 "FinancialDocumentClassification__Enabled": "true",
 "ServiceBillExtraction__Enabled": "true", "ServiceBillReconciliation__Enabled": "true",
 "FinancialDocumentClassification__AgentEnabled": "false", "ServiceBillExtraction__AgentEnabled": "false",
 "ServiceBillReconciliation__ConfirmationEnabled": "false", "ServiceBillReconciliation__BindingEnabled": "false",
 "FinancialEmailImport__Enabled": "false"}
if any(str(env.get(k)).lower() != v for k, v in expected.items()):
 sys.exit("Configuração efetiva não respeita as oito flags obrigatórias")
print("Configuração validada: três flags de leitura true; cinco flags de agentes/mutação/scheduler false")
'

install -d -o root -g root -m 0700 /etc/saldo360/config-backups
backup="$(mktemp /etc/saldo360/config-backups/pre-service-bill-readonly.XXXXXX)"
install -o root -g root -m 0600 "$ENV_FILE" "$backup"
install -o deploy -g deploy -m 0600 "$work/new.env" "$ENV_FILE"
if ! compose "$ENV_FILE" up -d --no-build --pull never --no-deps --wait api; then
  install -o deploy -g deploy -m 0600 "$backup" "$ENV_FILE"
  compose "$ENV_FILE" up -d --no-build --pull never --no-deps --wait api
  die 'reload falhou; configuração anterior restaurada'
fi
docker exec saldo360-api-1 curl --fail --silent --show-error --max-time 10 http://127.0.0.1:8080/health/ready >/dev/null
curl --fail --silent --show-error --max-time 10 http://127.0.0.1:5600/web-health >/dev/null
docker inspect --format '{{range .Config.Env}}{{println .}}{{end}}' saldo360-api-1 | grep -E '^(FinancialDocumentClassification__(Enabled|AgentEnabled)|ServiceBillExtraction__(Enabled|AgentEnabled)|ServiceBillReconciliation__(Enabled|ConfirmationEnabled|BindingEnabled)|FinancialEmailImport__Enabled)='
printf 'Reload da mesma imagem concluído; healthchecks OK; backup de configuração root-only: %s\n' "$backup"
printf 'Nenhuma coleta, confirmação, binding, migration ou operação financeira executada pelo procedimento.\n'
