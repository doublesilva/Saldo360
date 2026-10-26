#!/usr/bin/env bash
set -euo pipefail

backend=false
frontend=false
infra=false
backend_specific=false
ocr=false

while IFS= read -r path; do
  [ -n "$path" ] || continue
  # Expensive, component-specific checks. Unknown/shared build inputs fail closed.
  # Runtime financial constraints, auth and concurrency remain in the normal suite.
  case "$path" in
    .github/workflows/ci.yml|scripts/ci/*|tests/*|Directory.*|global.json|*NuGet.config|*nuget.config|*.sln|*.slnx|*.csproj|.dockerignore)
      backend_specific=true; ocr=true ;;
    src/Saldo360.Domain/*|src/*/Bootstrap/*|src/*/Persistence/*|src/*/Workspaces/*|src/Saldo360.BudgetBootstrap/*|src/Saldo360.DocumentCutover/*|src/Saldo360.StatementDiagnostic/*|src/Saldo360.StatementExtractionLab/*)
      backend_specific=true ;;
    src/*/CreditCards/*)
      backend_specific=true; ocr=true ;;
    src/*/DependencyInjection.cs|src/Saldo360.Api/Program.cs)
      backend_specific=true; ocr=true ;;
    client/*|Dockerfile.web|deploy/nginx.conf|docs/*|README.md|*.md) ;;
    Dockerfile.ocr-tests) backend=true; ocr=true ;;
    Dockerfile.api|docker-compose*.yml|.env.production.example|deploy/*|.github/workflows/deploy.yml)
      backend=true; backend_specific=true ;;
    */*) ;;
    *) backend_specific=true; ocr=true ;;
  esac
  case "$path" in
    .github/workflows/ci.yml|scripts/ci/*) backend=true; frontend=true; infra=true ;;
    deploy/nginx.conf) frontend=true; infra=true ;;
    .github/workflows/deploy.yml|docker-compose*.yml|.env.production.example|deploy/*|scripts/*) infra=true ;;
    src/*|tests/*|Dockerfile.api|Dockerfile.ocr-tests|Saldo360.sln|Saldo360.slnx|Directory.Build.*|Directory.Packages.*|global.json|NuGet.config|nuget.config|dotnet-tools.json) backend=true ;;
    client/*|Dockerfile.web) frontend=true ;;
    .dockerignore) backend=true; frontend=true ;;
    docs/*|README.md|*.md) ;;
    */*) ;;
    *) backend=true; frontend=true; infra=true ;;
  esac
done

printf 'backend=%s\nfrontend=%s\ninfra=%s\nbackend_specific=%s\nocr=%s\n' "$backend" "$frontend" "$infra" "$backend_specific" "$ocr"
