param(
    [Parameter(Mandatory = $true)][string]$ApprovedPlanPath,
    [Parameter(Mandatory = $true)][string]$OutputSqlPath
)
# Generates an offline review artifact only. Never opens a database connection.
$ErrorActionPreference = 'Stop'
$plan = @(Get-Content -LiteralPath $ApprovedPlanPath -Raw | ConvertFrom-Json)
if ($plan.Count -eq 0) { throw 'An explicit nonempty ownership plan is required.' }
$families = @{}; $workspaces = @{}; $owners = @{}
foreach ($entry in $plan) {
    foreach ($field in 'family_id','workspace_id','owner_user_id') {
        $id = [Guid]::Empty
        if (-not [Guid]::TryParse([string]$entry.$field, [ref]$id) -or $id -eq [Guid]::Empty) { throw "Invalid $field" }
        $entry.$field = $id.ToString()
    }
    if ($entry.family_id -eq $entry.workspace_id) { throw 'FamilyId and WorkspaceId must be independent.' }
    if ($families.ContainsKey($entry.family_id) -or $workspaces.ContainsKey($entry.workspace_id) -or $owners.ContainsKey($entry.owner_user_id)) {
        throw 'Ambiguous mapping or merge: a separate ownership decision is required.'
    }
    $families[$entry.family_id] = $true; $workspaces[$entry.workspace_id] = $true; $owners[$entry.owner_user_id] = $true
}
$destination = [IO.Path]::GetFullPath($OutputSqlPath)
if (Test-Path -LiteralPath $destination) { throw 'Output already exists; refusing to overwrite.' }
$normalized = @($plan | Select-Object family_id, workspace_id, owner_user_id)
$json = ConvertTo-Json -InputObject $normalized -Depth 4 -Compress
$migration = & dotnet ef migrations script 20261020155313_AddUserServiceAccounts 20261020222341_IntroduceFinancialWorkspaces --project src/Saldo360.Infrastructure --startup-project src/Saldo360.Api --no-build
if ($LASTEXITCODE -ne 0) { throw 'Migration SQL generation failed; no artifact written.' }
# JSON contains normalized GUID fields only; no financial values, tokens or service identifiers.
$header = @"
-- OFFLINE REVIEW ARTIFACT: requires approved maintenance window and coherent database/storage backup.
-- Run as ONE session only after independent review. This script does not convert document files.
-- Stop if any owner, holder, responsible user, scope or storage destination is ambiguous.
SELECT set_config('saldo360.workspace_cutover_plan', '$json', false);
"@
[IO.File]::WriteAllText($destination, $header + [Environment]::NewLine + ($migration -join [Environment]::NewLine))
Write-Output 'Offline SQL prepared. No database connection, migration or file conversion executed.'
