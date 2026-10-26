#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
classifier="$root/scripts/ci/classify-changes.sh"

assert_case() {
  local paths=$1 expected="$2 backend_specific=${3:-false} ocr=${4:-false}" actual
  actual="$(printf '%s\n' "$paths" | bash "$classifier" | tr '\n' ' ' | sed 's/ $//')"
  if [ "$actual" != "$expected" ]; then
    echo "classification mismatch: paths=[$paths] expected=[$expected] actual=[$actual]" >&2
    exit 1
  fi
}

assert_case 'src/Saldo360.Domain/Test.cs' 'backend=true frontend=false infra=false' true
assert_case 'src/New.Project/New.Project.csproj' 'backend=true frontend=false infra=false' true true
assert_case 'tests/New.Project.Tests/Test.cs' 'backend=true frontend=false infra=false' true true
assert_case 'client/src/App.tsx' 'backend=false frontend=true infra=false'
assert_case 'Dockerfile.api' 'backend=true frontend=false infra=false' true
assert_case 'Dockerfile.web' 'backend=false frontend=true infra=false'
assert_case 'docker-compose.production.yml' 'backend=true frontend=false infra=true' true
assert_case '.github/workflows/deploy.yml' 'backend=true frontend=false infra=true' true
assert_case '.github/workflows/ci.yml' 'backend=true frontend=true infra=true' true true
assert_case '.dockerignore' 'backend=true frontend=true infra=false' true true
assert_case 'deploy/nginx.conf' 'backend=false frontend=true infra=true'
assert_case 'docs/README.md' 'backend=false frontend=false infra=false'
assert_case 'README.md' 'backend=false frontend=false infra=false'
assert_case '.editorconfig' 'backend=true frontend=true infra=true' true true
assert_case 'src/Saldo360.Api/Accounts/FinancialAccountEndpoints.cs' 'backend=true frontend=false infra=false'
assert_case 'src/Saldo360.Application/Goals/FinancialGoalService.cs' 'backend=true frontend=false infra=false'
assert_case 'src/Saldo360.Infrastructure/Persistence/Migrations/New.cs' 'backend=true frontend=false infra=false' true
assert_case 'src/Saldo360.Infrastructure/CreditCards/LocalStatementPageReader.cs' 'backend=true frontend=false infra=false' true true
assert_case 'src/Saldo360.BudgetBootstrap/Program.cs' 'backend=true frontend=false infra=false' true
assert_case 'src/Saldo360.Infrastructure/Workspaces/FinancialDocumentWorkspaceCutover.cs' 'backend=true frontend=false infra=false' true
assert_case 'Dockerfile.ocr-tests' 'backend=true frontend=false infra=false' false true
assert_case $'docs/report.md\nsrc/Saldo360.Api/Accounts/FinancialAccountEndpoints.cs' 'backend=true frontend=false infra=false'
assert_case $'src/Saldo360.Api/Accounts/FinancialAccountEndpoints.cs\nDockerfile.ocr-tests' 'backend=true frontend=false infra=false' false true

echo 'change classification contract: ok'
