#!/usr/bin/env bash
set -euo pipefail
backend=false
frontend=false
infra=false
full=false
ocr=false
declare -A areas=()

affect() {
  backend=true
  local area
  for area in "$@"; do areas["$area"]=1; done
}
# Reviewed reverse dependencies, transitively expanded, not test-name matching.
financial() {
  case "$1" in
    Accounts) affect Accounts Transactions Transfers Planning Scenarios Cards Debts Goals Budgets Dashboard ;;
    Categories) affect Categories Transactions Planning Scenarios Budgets Cards Debts Goals Recurrences Dashboard ;;
    Transactions) affect Transactions Accounts Planning Scenarios Budgets Goals Debts Transfers Cards Recurrences Dashboard ;;
    Transfers) affect Transfers Accounts Planning Scenarios Dashboard ;;
    Planning) affect Planning Scenarios Accounts Budgets Cards Debts Goals Recurrences Dashboard ;;
    Scenarios) affect Scenarios Dashboard ;;
    Cards) affect Cards Documents Services Planning Scenarios Accounts Budgets Recurrences Dashboard ;;
    # Reader/schema/provider changes affect documentary consumers, not account CRUD.
    # Known import, official consolidation and Inbox integrations carry Documents too.
    Documents) affect Documents Services ;;
    Services) affect Services Documents Planning Scenarios Dashboard ;;
    Budgets) affect Budgets Planning Scenarios Dashboard ;;
    Goals) affect Goals Scenarios Dashboard ;;
    Debts) affect Debts Planning Scenarios Budgets Accounts Dashboard ;;
    Recurrences) affect Recurrences Planning Scenarios Cards Budgets Dashboard ;;
    Families) affect Families Security Workspaces ;;
    Security) affect Security Families Workspaces Documents Services ;;
    Platform) affect Platform ;;
    Admin) affect Admin Cards Planning Scenarios Budgets Goals Debts Infrastructure ;;
    *) backend=true; full=true ;;
  esac
}

while IFS= read -r path || [ -n "$path" ]; do
  [ -n "$path" ] || continue
  case "$path" in
    docs/*|README.md|*.md) ;;
    client/*|Dockerfile.web) frontend=true ;;
    deploy/nginx.conf) frontend=true; infra=true ;;
    .github/workflows/ci.yml|scripts/ci/*|.dockerignore)
      backend=true; frontend=true; infra=true; full=true ;;
    tests/*|Directory.*|global.json|*NuGet.config|*nuget.config|*.sln|*.slnx|*.csproj|dotnet-tools.json)
      backend=true; full=true ;;
    Dockerfile.api|Dockerfile.ocr-tests|docker-compose*.yml|.env.production.example|deploy/*|scripts/*|.github/workflows/deploy.yml)
      backend=true; infra=true; full=true ;;
    src/*/DependencyInjection.cs|src/Saldo360.Api/Program.cs|src/*/Common/*|src/*/Users/*|src/*/Workspaces/*|src/*/appsettings*)
      backend=true; full=true ;;
    src/*/Persistence/Configurations/FinancialCreditCard*|src/*/Persistence/Configurations/FinancialPlannedInvoice*) financial Cards ;;
    src/*/Persistence/Configurations/FinancialAccount*) financial Accounts ;;
    src/*/Persistence/Configurations/FinancialCategory*) financial Categories ;;
    src/*/Persistence/Configurations/FinancialTransaction*) financial Transactions ;;
    src/*/Persistence/Configurations/FinancialTransfer*) financial Transfers ;;
    src/*/Persistence/Configurations/FinancialPlannedTransaction*) financial Planning ;;
    src/*/Persistence/Configurations/FinancialBudget*) financial Budgets ;;
    src/*/Persistence/Configurations/FinancialGoal*) financial Goals ;;
    src/*/Persistence/Configurations/FinancialDebt*) financial Debts ;;
    src/*/Persistence/Configurations/FinancialRecurrence*) financial Recurrences ;;
    src/*/Persistence/Configurations/ServiceAccount*) financial Services ;;
    src/*/Persistence/Configurations/FinancialDocument*|src/*/Persistence/Configurations/FinancialEmail*) financial Documents ;;
    src/*/Persistence/*) backend=true; full=true ;;
    src/*/Accounts/*) financial Accounts ;;
    src/*/Categories/*) financial Categories ;;
    src/*/Transactions/*) financial Transactions ;;
    src/*/Transfers/*) financial Transfers ;;
    src/*/Planning/*Scenario*) financial Scenarios ;;
    src/*/Planning/*|src/*/Calendar/*) financial Planning ;;
    src/*/Budgets/*) financial Budgets ;;
    src/*/Goals/*) financial Goals ;;
    src/*/Debts/*) financial Debts ;;
    src/*/Recurrences/*) financial Recurrences ;;
    src/*/Families/*) financial Families ;;
    src/*/Authentication/*) financial Security ;;
    src/*/ServiceAccounts/*) financial Services ;;
    src/*/Health/*|src/*/Errors/*|src/*/Observability/*) financial Platform ;;
    src/*/Dashboard/*) affect Dashboard ;;
    src/*/CreditCards/*Statement*|src/*/CreditCards/*Pdf*|src/*/CreditCards/*Csv*|src/*/CreditCards/*Document*|src/*/CreditCards/*Email*|src/*/CreditCards/*Gmail*|src/*/CreditCards/*PageReader*) financial Documents ;;
    src/*/CreditCards/*ServiceBill*|src/*/CreditCards/*ServiceAccount*) financial Services ;;
    src/*/CreditCards/*) financial Cards ;;
    src/*/Bootstrap/*|src/Saldo360.BudgetBootstrap/*) financial Admin ;;
    src/Saldo360.DocumentCutover/*|src/Saldo360.StatementDiagnostic/*|src/Saldo360.StatementExtractionLab/*)
      backend=true; full=true ;;
    # New nested modules and unknown shared inputs also fail closed.
    *) backend=true; frontend=true; infra=true; full=true ;;
  esac
done

affected_filter=''
if [ "$full" = true ]; then
  ocr=true
elif [ "${#areas[@]}" -gt 0 ]; then
  joined=''
  while IFS= read -r area; do joined+="${joined:+|}Area=$area"; done < <(printf '%s\n' "${!areas[@]}" | LC_ALL=C sort)
  affected_filter="($joined)&Profile!=FAST"
  if [[ -v areas[Cards] || -v areas[Documents] || -v areas[Services] ]]; then ocr=true; fi
fi
printf 'backend=%s\nfrontend=%s\ninfra=%s\nfull=%s\nocr=%s\n' "$backend" "$frontend" "$infra" "$full" "$ocr"
# Fixed area names only; untrusted input paths are never emitted as shell code.
printf 'affected_filter=%q\n' "$affected_filter"
