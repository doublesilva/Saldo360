# CLI-equivalent wrapper; no connection fallback and no automatic migration/bootstrap.
# Arguments use the CLI's --workspace-id, --source, --dry-run/--execute and --output syntax.
$ErrorActionPreference = 'Stop'
$bootstrapProjectPath = Join-Path $PSScriptRoot '../src/Saldo360.BudgetBootstrap/Saldo360.BudgetBootstrap.csproj'
& dotnet run --project $bootstrapProjectPath -- @args
exit $LASTEXITCODE
