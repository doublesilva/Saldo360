#!/usr/bin/env bash
# Smoke test de login REAL contra um Authelia, simulando o navegador com curl:
#   frontend (Vite :5173) -> API -> Authelia -> callback -> sessão -> /api/me -> logout.
#
# Uso:
#   bash scripts/smoke-login.sh                    # Authelia do servidor (padrão)
#   bash scripts/smoke-login.sh --authelia-local   # Authelia local (profile authelia-local)
#
# Configuração por variáveis de ambiente (todas opcionais):
#   AUTHORITY      emissor esperado          (padrão: https://auth.home.doublesilva.com)
#   CA_FILE        CA (PEM) do provedor      (padrão: dev/certs/caddy-local-root.crt)
#   SMOKE_USER     usuário no Authelia       (padrão servidor: pergunta; local: familia)
#   PASSWORD_FILE  arquivo com a senha       (padrão servidor: pergunta sem eco; local: dev/authelia/secrets/…)
#   FRONT          origem do frontend        (padrão: http://localhost:5173)
#
# A senha nunca vai para argumentos de comando: é lida de arquivo ou do prompt sem eco e enviada
# ao curl pela entrada padrão. Só funciona com política one_factor e consentimento implícito/pré-aprovado;
# com segundo fator ou tela de consentimento, valide pelo navegador.
#
# Usa a API interna do portal do Authelia (/api/firstfactor), que é o que a página de login chama.
set -euo pipefail

ROOT=$(cd "$(dirname "$0")/.." && pwd)
if [ "${1:-}" = "--authelia-local" ]; then
  AUTHORITY=${AUTHORITY:-https://auth.saldo360.localhost:9091}
  CA_FILE=${CA_FILE:-$ROOT/dev/authelia/secrets/tls/ca.public.crt}
  SMOKE_USER=${SMOKE_USER:-familia}
  PASSWORD_FILE=${PASSWORD_FILE:-$ROOT/dev/authelia/secrets/dev_user_password.txt}
fi
AUTHELIA=${AUTHORITY:-https://auth.home.doublesilva.com}
CA=${CA_FILE:-$ROOT/dev/certs/caddy-local-root.crt}
FRONT=${FRONT:-http://localhost:5173}

[ -f "$CA" ] || { echo "CA não encontrada: $CA (defina CA_FILE)"; exit 1; }
if [ -z "${SMOKE_USER:-}" ]; then read -rp "Usuário no Authelia ($AUTHELIA): " SMOKE_USER; fi
USER_NAME=$SMOKE_USER
if [ -n "${PASSWORD_FILE:-}" ]; then
  USER_PASS=$(cat "$PASSWORD_FILE")
else
  read -rsp "Senha de $USER_NAME (não será exibida): " USER_PASS; echo
fi

JAR=$(mktemp)
trap 'rm -f "$JAR" "$JAR.before-logout"' EXIT
# --ssl-no-revoke: a CA local não publica lista de revogação (necessário no curl/Schannel do Windows).
C="curl -sS --ssl-no-revoke --cacert $CA -b $JAR -c $JAR"

step() { printf '\n== %s\n' "$*"; }
expect() { # expect <esperado> <obtido> <descrição>
  if [ "$1" = "$2" ]; then echo "ok   $3 ($2)"; else echo "FALHA $3: esperado $1, obtido $2"; exit 1; fi
}

step "Sem sessão"
expect 401 "$($C -o /dev/null -w '%{http_code}' "$FRONT/api/me")" "/api/me"

step "Login no provedor"
AUTHZ=$($C -o /dev/null -w '%{redirect_url}' "$FRONT/auth/login?returnUrl=%2F")
expect "$AUTHELIA/api/oidc/authorization" "${AUTHZ%%\?*}" "/auth/login redireciona ao provedor"

PORTAL=$($C -o /dev/null -w '%{redirect_url}' "$AUTHZ")
FLOW_ID=$(printf '%s' "$PORTAL" | grep -o 'flow_id=[^&]*' | cut -d= -f2)

# printf é builtin: o corpo com a senha vai ao curl pela entrada padrão, não pela linha de comando.
FIRST=$(printf '{"username":"%s","password":"%s","keepMeLoggedIn":false,"flowID":"%s","flow":"openid_connect"}' \
    "$USER_NAME" "$USER_PASS" "$FLOW_ID" |
  $C -H 'Content-Type: application/json' -H "Origin: $AUTHELIA" --data-binary @- "$AUTHELIA/api/firstfactor")
unset USER_PASS
expect '"status":"OK"' "$(printf '%s' "$FIRST" | grep -o '"status":"[A-Z]*"')" "usuário e senha aceitos"
NEXT=$(printf '%s' "$FIRST" | grep -o '"redirect":"[^"]*"' | cut -d'"' -f4 | sed 's/\\u0026/\&/g')

CALLBACK=$($C -o /dev/null -w '%{redirect_url}' "$NEXT")
expect "$FRONT/signin-oidc" "${CALLBACK%%\?*}" "provedor devolve o code ao callback"

RETURN=$($C -o /dev/null -w '%{http_code} %{redirect_url}' "$CALLBACK")
expect "302 $FRONT/" "$RETURN" "callback cria a sessão e volta ao frontend"

step "Com sessão"
ME=$($C -w '\n%{http_code}' "$FRONT/api/me")
expect 200 "$(printf '%s' "$ME" | tail -1)" "/api/me"
echo "     $(printf '%s' "$ME" | head -1)"
ME_JSON=$(printf '%s' "$ME" | head -1)
expect "\"issuer\":\"$AUTHELIA\"" "$(printf '%s' "$ME_JSON" | grep -o '"issuer":"[^"]*"')" "issuer vem do ID token do Authelia"
SUBJECT=$(printf '%s' "$ME_JSON" | grep -o '"subject":"[^"]*"' | cut -d'"' -f4)
expect "\"id\":\"$SUBJECT\"" "$(printf '%s' "$ME_JSON" | grep -o '"id":"[^"]*"')" "id = subject (compatibilidade)"
# Equivalente a recarregar a página: nova requisição só com o cookie.
expect "$ME_JSON" "$($C "$FRONT/api/me")" "sessão mantida em nova requisição (refresh)"
cp "$JAR" "$JAR.before-logout"

step "Logout"
expect 400 "$($C -o /dev/null -w '%{http_code}' -X POST "$FRONT/auth/logout")" "logout sem token CSRF é recusado"
TOKEN=$($C "$FRONT/auth/csrf" | cut -d'"' -f4)
expect 204 "$($C -o /dev/null -w '%{http_code}' -X POST -H "X-CSRF-TOKEN: $TOKEN" "$FRONT/auth/logout")" "logout com token CSRF"
expect 401 "$($C -o /dev/null -w '%{http_code}' "$FRONT/api/me")" "/api/me depois do logout"
expect 401 "$(curl -sS -b "$JAR.before-logout" -o /dev/null -w '%{http_code}' "$FRONT/api/me")" "cookie antigo reenviado (replay)"

step "Health"
expect 200 "$(curl -sS -o /dev/null -w '%{http_code}' "$FRONT/health")" "/health público"

printf '\nTudo certo.\n'
