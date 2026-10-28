<#
.SYNOPSIS
  Copia PLUGGY_ENABLED / PLUGGY_IMPORT_ENABLED (T09.2, opcional) / PLUGGY_CLIENT_ID / PLUGGY_CLIENT_SECRET do .env para os user-secrets da API (T09.1, só local).
.DESCRIPTION
  A API não lê o .env (só o docker compose o lê). Este script lê as três chaves, valida que estão preenchidas e as grava
  em user-secrets como Pluggy:Enabled, Pluggy:ClientId e Pluggy:ClientSecret. Nunca imprime valores.
#>
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$envFile = Join-Path $root '.env'
if (-not (Test-Path $envFile)) { throw ".env não encontrado em $root" }

$values = @{}
foreach ($line in Get-Content $envFile) {
    if ($line -match '^\s*(PLUGGY_(?:ENABLED|IMPORT_ENABLED|CLIENT_ID|CLIENT_SECRET))\s*=\s*(.*?)\s*$') { $values[$Matches[1]] = $Matches[2].Trim('"', "'") }
}
foreach ($key in 'PLUGGY_CLIENT_ID', 'PLUGGY_CLIENT_SECRET') {
    if (-not $values[$key]) { throw "$key está vazio no .env. Preencha antes de executar." }
}
$enabled = if ($values['PLUGGY_ENABLED']) { $values['PLUGGY_ENABLED'] } else { 'true' }

$project = Join-Path $root 'src/Saldo360.Api'
dotnet user-secrets set 'Pluggy:Enabled' $enabled --project $project | Out-Null
if ($values['PLUGGY_IMPORT_ENABLED']) { dotnet user-secrets set 'Pluggy:ImportEnabled' $values['PLUGGY_IMPORT_ENABLED'] --project $project | Out-Null }
dotnet user-secrets set 'Pluggy:ClientId' $values['PLUGGY_CLIENT_ID'] --project $project | Out-Null
dotnet user-secrets set 'Pluggy:ClientSecret' $values['PLUGGY_CLIENT_SECRET'] --project $project | Out-Null
Write-Host "Pluggy:Enabled=$enabled, Pluggy:ClientId e Pluggy:ClientSecret gravados em user-secrets (valores não exibidos)."
