#!/usr/bin/env bash
# Smoke test de login REAL contra o Authelia de desenvolvimento, simulando o navegador com curl:
#   frontend (Vite :5173) -> API -> Authelia -> callback -> sessão -> /api/me -> logout.
#
# Pré-requisitos: docker compose up -d --wait; API (dotnet run) e frontend (npm run dev) rodando;
# segredos gerados por scripts/setup-dev-auth.ps1. Uso: bash scripts/smoke-login.sh
#
# Usa a API interna do portal do Authelia (/api/firstfactor), que é o que a página de login chama.
set -euo pipefail

ROOT=$(cd "$(dirname "$0")/.." && pwd)
CA=$ROOT/dev/authelia/secrets/tls/ca.public.crt
AUTHELIA=https://auth.saldo360.localhost:9091
FRONT=${FRONT:-http://localhost:5173}
USER_NAME=${USER_NAME:-familia}
USER_PASS=$(cat "$ROOT/dev/authelia/secrets/dev_user_password.txt")

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

BODY=$(printf '{"username":"%s","password":"%s","keepMeLoggedIn":false,"flowID":"%s","flow":"openid_connect"}' \
  "$USER_NAME" "$USER_PASS" "$FLOW_ID")
FIRST=$($C -H 'Content-Type: application/json' -H "Origin: $AUTHELIA" -d "$BODY" "$AUTHELIA/api/firstfactor")
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
