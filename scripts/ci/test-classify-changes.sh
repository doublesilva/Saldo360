#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
classifier="$root/scripts/ci/classify-changes.sh"
assert_case() {
  local paths=$1 expected=$2 required=${3:-} forbidden=${4:-} actual area
  local backend frontend infra full affected_filter
  eval "$(printf '%s\n' "$paths" | bash "$classifier")"
  actual="backend=$backend frontend=$frontend infra=$infra full=$full"
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
assert_case 'src/Saldo360.Application/Goals/FinancialGoalService.cs' 'backend=true frontend=false infra=false full=false' 'Goals Scenarios Dashboard' 'Planning Documents Infrastructure'
assert_case 'src/Saldo360.Application/Planning/FinancialPlanningScenarioProjection.cs' 'backend=true frontend=false infra=false full=false' 'Scenarios Dashboard' 'Planning Cards'
assert_case 'src/Saldo360.Infrastructure/Budgets/FinancialBudgetExecutionRepository.cs' 'backend=true frontend=false infra=false full=false' 'Budgets Planning Dashboard'
assert_case 'src/Saldo360.Domain/Transfers/FinancialTransfer.cs' 'backend=true frontend=false infra=false full=false' 'Transfers Accounts Planning Dashboard'
assert_case 'src/Saldo360.Infrastructure/Transactions/FinancialTransactionRepository.cs' 'backend=true frontend=false infra=false full=false' 'Transactions Accounts Cards Budgets Debts Goals Recurrences Planning'
assert_case 'src/Saldo360.Api/Authentication/AuthenticationSetup.cs' 'backend=true frontend=false infra=false full=false' 'Security Workspaces Families Documents Services'
assert_case 'src/Saldo360.Application/CreditCards/FinancialCreditCardPurchaseService.cs' 'backend=true frontend=false infra=false full=false' 'Cards Documents Services Planning Accounts Budgets'
assert_case 'src/Saldo360.Infrastructure/Persistence/Configurations/FinancialCreditCardInvoicePaymentConfiguration.cs' 'backend=true frontend=false infra=false full=false' 'Cards Documents Budgets'
assert_case 'src/Saldo360.Infrastructure/Persistence/Configurations/FinancialGoalConfiguration.cs' 'backend=true frontend=false infra=false full=false' 'Goals Scenarios'
for path in src/Saldo360.Infrastructure/Persistence/Migrations/New.cs src/Saldo360.Infrastructure/Persistence/Saldo360DbContext.cs src/Saldo360.Domain/Common/Money.cs src/Saldo360.Infrastructure/Workspaces/FinancialWriteCoordination.cs src/Saldo360.Infrastructure/DependencyInjection.cs src/Saldo360.Api/Program.cs src/New.Project/New.Project.csproj tests/Saldo360.Tests/Api/AuthEndpointsTests.cs; do
  assert_case "$path" 'backend=true frontend=false infra=false full=true'
done
for path in src/Saldo360.Infrastructure/Ai/OpenAiCompletionProvider.cs src/Saldo360.Application/Pluggy/PluggyAutoSync.cs .github/workflows/ci.yml scripts/ci/new.sh .dockerignore .editorconfig src/Saldo360.Application/NewFeature/New.cs .github/workflows/new.yml "src/unknown/\$(exit 99).cs"; do
  assert_case "$path" 'backend=true frontend=true infra=true full=true'
done
for path in Dockerfile.api docker-compose.production.yml .github/workflows/deploy.yml; do
  assert_case "$path" 'backend=true frontend=false infra=true full=true'
done
for path in client/src/App.tsx Dockerfile.web; do
  assert_case "$path" 'backend=false frontend=true infra=false full=false'
done
assert_case 'deploy/nginx.conf' 'backend=false frontend=true infra=true full=false'
assert_case 'docs/report.md' 'backend=false frontend=false infra=false full=false'
assert_case '' 'backend=false frontend=false infra=false full=false'
assert_case $'docs/report.md\nsrc/Saldo360.Api/Goals/FinancialGoalEndpoints.cs' 'backend=true frontend=false infra=false full=false' 'Goals Scenarios Dashboard'
assert_case $'src/Saldo360.Api/Goals/Goal.cs\nsrc/Saldo360.Api/Authentication/Auth.cs' 'backend=true frontend=false infra=false full=false' 'Goals Scenarios Dashboard Security Documents'
assert_case $'src/Saldo360.Api/Goals/Goal.cs\nsrc/Unknown/Unclassified.cs' 'backend=true frontend=true infra=true full=true'
echo 'FAST/AFFECTED/FULL selection contract: ok'
