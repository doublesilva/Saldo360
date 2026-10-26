#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
classifier="$root/scripts/ci/classify-changes.sh"
assert_case() {
  local paths=$1 expected=$2 required=${3:-} forbidden=${4:-} actual area
  local backend frontend infra full ocr affected_filter
  eval "$(printf '%s\n' "$paths" | bash "$classifier")"
  actual="backend=$backend frontend=$frontend infra=$infra full=$full ocr=$ocr"
  [ "$actual" = "$expected" ] || { echo "selection mismatch: [$paths] expected=[$expected] actual=[$actual]" >&2; exit 1; }
  for area in $required; do
    [[ "$affected_filter" == *"Area=$area|"* || "$affected_filter" == *"Area=$area)"* ]] || { echo "missing reverse dependency: $area for [$paths]" >&2; exit 1; }
  done
  for area in $forbidden; do
    [[ "$affected_filter" != *"Area=$area|"* && "$affected_filter" != *"Area=$area)"* ]] || { echo "unexpected area: $area for [$paths]" >&2; exit 1; }
  done
  if [ "$full" = true ]; then [ -z "$affected_filter" ];
  elif [ "$backend" = true ]; then [[ "$affected_filter" == *'&Profile!=FAST' ]]; fi
}
assert_case 'src/Saldo360.Application/Goals/FinancialGoalService.cs' 'backend=true frontend=false infra=false full=false ocr=false' 'Goals Scenarios Dashboard' 'Planning Documents Infrastructure'
assert_case 'src/Saldo360.Application/Planning/FinancialPlanningScenarioProjection.cs' 'backend=true frontend=false infra=false full=false ocr=false' 'Scenarios Dashboard' 'Planning Cards'
assert_case 'src/Saldo360.Infrastructure/Budgets/FinancialBudgetExecutionRepository.cs' 'backend=true frontend=false infra=false full=false ocr=false' 'Budgets Planning Dashboard'
assert_case 'src/Saldo360.Domain/Transfers/FinancialTransfer.cs' 'backend=true frontend=false infra=false full=false ocr=false' 'Transfers Accounts Planning Dashboard'
assert_case 'src/Saldo360.Infrastructure/Transactions/FinancialTransactionRepository.cs' 'backend=true frontend=false infra=false full=false ocr=true' 'Transactions Accounts Cards Budgets Debts Goals Recurrences Planning'
assert_case 'src/Saldo360.Api/Authentication/AuthenticationSetup.cs' 'backend=true frontend=false infra=false full=false ocr=true' 'Security Workspaces Families Documents Services'
assert_case 'src/Saldo360.Infrastructure/CreditCards/LocalStatementPageReader.cs' 'backend=true frontend=false infra=false full=false ocr=true' 'Documents Services' 'Accounts Planning Budgets'
assert_case 'src/Saldo360.Infrastructure/CreditCards/OpenAiCreditCardStatementAgentProvider.cs' 'backend=true frontend=false infra=false full=false ocr=true' 'Documents Services' 'Accounts Planning Budgets'
assert_case 'src/Saldo360.Application/CreditCards/FinancialCreditCardPurchaseService.cs' 'backend=true frontend=false infra=false full=false ocr=true' 'Cards Documents Services Planning Accounts Budgets'
assert_case 'src/Saldo360.Infrastructure/Persistence/Configurations/FinancialCreditCardInvoicePaymentConfiguration.cs' 'backend=true frontend=false infra=false full=false ocr=true' 'Cards Documents Budgets'
assert_case 'src/Saldo360.Infrastructure/Persistence/Configurations/FinancialGoalConfiguration.cs' 'backend=true frontend=false infra=false full=false ocr=false' 'Goals Scenarios'
for path in src/Saldo360.Infrastructure/Persistence/Migrations/New.cs src/Saldo360.Infrastructure/Persistence/Saldo360DbContext.cs src/Saldo360.Domain/Common/Money.cs src/Saldo360.Infrastructure/Workspaces/FinancialWriteCoordination.cs src/Saldo360.Infrastructure/DependencyInjection.cs src/Saldo360.Api/Program.cs src/New.Project/New.Project.csproj tests/Saldo360.Tests/Api/AuthEndpointsTests.cs; do
  assert_case "$path" 'backend=true frontend=false infra=false full=true ocr=true'
done
for path in .github/workflows/ci.yml scripts/ci/new.sh .dockerignore .editorconfig src/Saldo360.Application/NewFeature/New.cs .github/workflows/new.yml "src/unknown/\$(exit 99).cs"; do
  assert_case "$path" 'backend=true frontend=true infra=true full=true ocr=true'
done
for path in Dockerfile.api Dockerfile.ocr-tests docker-compose.production.yml .github/workflows/deploy.yml; do
  assert_case "$path" 'backend=true frontend=false infra=true full=true ocr=true'
done
for path in client/src/App.tsx Dockerfile.web; do
  assert_case "$path" 'backend=false frontend=true infra=false full=false ocr=false'
done
assert_case 'deploy/nginx.conf' 'backend=false frontend=true infra=true full=false ocr=false'
assert_case 'docs/report.md' 'backend=false frontend=false infra=false full=false ocr=false'
assert_case '' 'backend=false frontend=false infra=false full=false ocr=false'
assert_case $'docs/report.md\nsrc/Saldo360.Api/Goals/FinancialGoalEndpoints.cs' 'backend=true frontend=false infra=false full=false ocr=false' 'Goals Scenarios Dashboard'
assert_case $'src/Saldo360.Api/Goals/Goal.cs\nsrc/Saldo360.Api/Authentication/Auth.cs' 'backend=true frontend=false infra=false full=false ocr=true' 'Goals Scenarios Dashboard Security Documents'
assert_case $'src/Saldo360.Api/Goals/Goal.cs\nsrc/Unknown/Unclassified.cs' 'backend=true frontend=true infra=true full=true ocr=true'
echo 'FAST/AFFECTED/FULL selection contract: ok'
