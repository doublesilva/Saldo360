#!/usr/bin/env bash
set -euo pipefail
export MSYS_NO_PATHCONV=1
: "${IMAGE:?Local test image required}"
: "${CONTAINER:?Disposable API container name required}"
: "${POSTGRES_CONTAINER:?Disposable database container name required}"
: "${NETWORK:?Disposable network name required}"
CONNECTION='Host=postgres;Port=5432;Database=saldo360_ci;Username=saldo360;Password=ci-only-not-a-secret'
# Refuse collisions before installing the cleanup trap; never remove preexisting resources.
for name in "$CONTAINER" "$POSTGRES_CONTAINER" "$NETWORK"; do
  [[ "$name" == saldo360-*ci* || "$name" == saldo360-tci2-* ]] || { echo 'Expected disposable CI names' >&2; exit 1; }
done
if docker container inspect "$CONTAINER" >/dev/null 2>&1 ||
   docker container inspect "$POSTGRES_CONTAINER" >/dev/null 2>&1 ||
   docker network inspect "$NETWORK" >/dev/null 2>&1; then
  echo 'Disposable resource name already exists; nothing removed' >&2; exit 1
fi
created_api=false
created_postgres=false
created_network=false
cleanup() {
  local status=$?
  if [ "$status" -ne 0 ]; then
    if [ "$created_api" = true ]; then docker logs "$CONTAINER" 2>/dev/null || true; fi
    if [ "$created_postgres" = true ]; then docker logs "$POSTGRES_CONTAINER" 2>/dev/null || true; fi
  fi
  if [ "$created_api" = true ]; then docker rm -f "$CONTAINER" >/dev/null 2>&1 || true; fi
  if [ "$created_postgres" = true ]; then docker rm -f "$POSTGRES_CONTAINER" >/dev/null 2>&1 || true; fi
  if [ "$created_network" = true ]; then docker network rm "$NETWORK" >/dev/null 2>&1 || true; fi
}
trap cleanup EXIT
trap 'exit 143' TERM
trap 'exit 130' INT
docker network create "$NETWORK" >/dev/null
created_network=true
docker create --name "$POSTGRES_CONTAINER" --network "$NETWORK" --network-alias postgres \
  -e POSTGRES_DB=saldo360_ci -e POSTGRES_USER=saldo360 -e POSTGRES_PASSWORD=ci-only-not-a-secret \
  --health-cmd='pg_isready -U saldo360 -d saldo360_ci' --health-interval=2s --health-timeout=2s --health-retries=30 \
  postgres:17-alpine >/dev/null
created_postgres=true
docker start "$POSTGRES_CONTAINER" >/dev/null
for _ in $(seq 1 60); do
  [ "$(docker inspect --format '{{.State.Health.Status}}' "$POSTGRES_CONTAINER")" = healthy ] && break
  sleep 1
done
[ "$(docker inspect --format '{{.State.Health.Status}}' "$POSTGRES_CONTAINER")" = healthy ]
docker run --rm --network "$NETWORK" -e ASPNETCORE_ENVIRONMENT=Production \
  -e ServiceAccounts__IdentifierHmacKey=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA= \
  -e "ConnectionStrings__Saldo360=$CONNECTION" -e Authentication__Oidc__Authority=https://example.invalid \
  -e Authentication__Oidc__ClientId=saldo360 -e Authentication__Oidc__ClientSecret=ci-only \
  --entrypoint /app/efbundle "$IMAGE" --connection "$CONNECTION"
docker create --name "$CONTAINER" --network "$NETWORK" -p 127.0.0.1::8080 \
  -e ServiceAccounts__IdentifierHmacKey=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA= \
  -e ASPNETCORE_ENVIRONMENT=Production -e "ConnectionStrings__Saldo360=$CONNECTION" \
  -e Authentication__Oidc__Authority=https://example.invalid -e Authentication__Oidc__ClientId=saldo360 \
  -e Authentication__Oidc__ClientSecret=ci-only "$IMAGE" >/dev/null
created_api=true
docker start "$CONTAINER" >/dev/null
for _ in $(seq 1 90); do
  health="$(docker inspect --format '{{if .State.Health}}{{.State.Health.Status}}{{else}}none{{end}}' "$CONTAINER")"
  [ "$health" = healthy ] && break
  [ "$(docker inspect --format '{{.State.Status}}' "$CONTAINER")" = running ] || exit 1
  sleep 2
done
[ "$(docker inspect --format '{{.State.Health.Status}}' "$CONTAINER")" = healthy ]
endpoint="$(docker port "$CONTAINER" 8080/tcp)"
[[ "$endpoint" =~ ^127\.0\.0\.1:[0-9]+$ ]]
curl --fail --silent --show-error --max-time 10 "http://$endpoint/health/ready" >/dev/null
[ "$(docker exec "$CONTAINER" id -u)" != 0 ]
echo 'API smoke: migrations, readiness, published HTTP port and nonroot passed'
