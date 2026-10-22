#!/usr/bin/env bash
set -euo pipefail

backend=false
frontend=false
infra=false

while IFS= read -r path; do
  [ -n "$path" ] || continue
  case "$path" in
    .github/workflows/ci.yml|scripts/ci/*) backend=true; frontend=true; infra=true ;;
    deploy/nginx.conf) frontend=true; infra=true ;;
    .github/workflows/deploy.yml|docker-compose*.yml|.env.production.example|deploy/*|scripts/*) infra=true ;;
    src/*|tests/*|Dockerfile.api|Saldo360.sln|Saldo360.slnx|Directory.Build.*|Directory.Packages.*|global.json|NuGet.config|nuget.config|dotnet-tools.json) backend=true ;;
    client/*|Dockerfile.web) frontend=true ;;
    .dockerignore) backend=true; frontend=true ;;
    docs/*|README.md|*.md) ;;
    */*) ;;
    *) backend=true; frontend=true; infra=true ;;
  esac
done

printf 'backend=%s\nfrontend=%s\ninfra=%s\n' "$backend" "$frontend" "$infra"
