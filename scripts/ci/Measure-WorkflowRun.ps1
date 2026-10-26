[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[0-9]+$')][string]$RunId,
    [ValidatePattern('^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')][string]$Repository = 'doublesilva/Saldo360',
    [ValidateRange(1, 86400)][int]$BaselineSeconds = 815,
    [ValidateRange(1, 86400)][int]$TargetSeconds = 244
)

$ErrorActionPreference = 'Stop'
function Get-UtcInstant($Value) {
    # ConvertFrom-Json can return either ISO strings or DateTime depending on pwsh version.
    if ($Value -is [DateTimeOffset]) { return $Value.ToUniversalTime() }
    if ($Value -is [DateTime]) { return ([DateTimeOffset]$Value).ToUniversalTime() }
    return [DateTimeOffset]::Parse([string]$Value, [Globalization.CultureInfo]::InvariantCulture,
        [Globalization.DateTimeStyles]::AssumeUniversal).ToUniversalTime()
}
# GET only: never dispatch, rerun, publish, push or deploy. Never fetch application logs.
$runJson = & gh api "repos/$Repository/actions/runs/$RunId"
if ($LASTEXITCODE -ne 0) { throw 'Cannot read workflow run metadata.' }
$run = ($runJson -join "`n") | ConvertFrom-Json
if ($run.path -notlike '.github/workflows/ci.yml*') { throw 'Expected the CI validation workflow, not Deploy.' }
if ($run.status -ne 'completed' -or $run.conclusion -ne 'success') {
    throw 'Timing acceptance requires a completed successful workflow run.'
}
$jobsJson = & gh api "repos/$Repository/actions/runs/$RunId/jobs?per_page=100" --paginate --slurp
if ($LASTEXITCODE -ne 0) { throw 'Cannot read workflow job metadata.' }
$pages = ($jobsJson -join "`n") | ConvertFrom-Json
$jobs = @($pages | ForEach-Object { $_.jobs } | Where-Object { $_.status -eq 'completed' -and $_.completed_at -and $_.started_at })
if ($jobs.Count -eq 0) { throw 'No completed jobs available.' }
$finish = ($jobs | ForEach-Object { Get-UtcInstant $_.completed_at } | Sort-Object | Select-Object -Last 1)
# Include the workflow's own final status update, not only its last job/step.
$runFinish = Get-UtcInstant $run.updated_at
if ($runFinish -gt $finish) { $finish = $runFinish }
$start = Get-UtcInstant $run.run_started_at
$seconds = ($finish - (Get-UtcInstant $run.created_at)).TotalSeconds
$reduction = (1 - $seconds / $BaselineSeconds) * 100
$testSteps = @($jobs | ForEach-Object { $_.steps } | Where-Object { $_.conclusion -eq 'success' })
[pscustomobject]@{
    runId = $RunId
    url = $run.html_url
    sha = $run.head_sha
    event = $run.event
    attempt = $run.run_attempt
    workflowSeconds = $seconds
    executionSeconds = ($finish - $start).TotalSeconds
    initialQueueSeconds = ($start - (Get-UtcInstant $run.created_at)).TotalSeconds
    baselineSeconds = $BaselineSeconds
    reductionPercent = [Math]::Round($reduction, 2)
    targetSeconds = $TargetSeconds
    measuredTargetMet = ($seconds -le $TargetSeconds -and $reduction -ge 70 -and $run.run_attempt -eq 1 -and
        @($testSteps | Where-Object name -like 'Test FAST*').Count -gt 0)
    fastExecuted = @($testSteps | Where-Object name -like 'Test FAST*').Count -gt 0
    affectedExecuted = @($testSteps | Where-Object name -like 'Test AFFECTED*').Count -gt 0
    fullExecuted = @($testSteps | Where-Object name -like 'Test FULL*').Count -gt 0
    jobs = @($jobs | ForEach-Object {
        [pscustomobject]@{
            name = $_.name
            conclusion = $_.conclusion
            seconds = ((Get-UtcInstant $_.completed_at) - (Get-UtcInstant $_.started_at)).TotalSeconds
        }
    })
    remoteMutationExecuted = $false
} | ConvertTo-Json -Depth 5
