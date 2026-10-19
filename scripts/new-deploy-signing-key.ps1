[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $PublicKeyPath,
    [string] $Repository = 'doublesilva/Saldo360',
    [string] $EnvironmentName = 'saldo360-production'
)

$ErrorActionPreference = 'Stop'
if (Test-Path -LiteralPath $PublicKeyPath) {
    throw 'O arquivo público já existe. Não será substituído nem haverá rotação automática.'
}

$secretNames = & gh secret list --repo $Repository --env $EnvironmentName --json name --jq '.[].name'
if ($LASTEXITCODE -ne 0) { throw 'Não foi possível consultar os secrets do environment.' }
if ($secretNames -contains 'DEPLOY_RELEASE_SIGNING_KEY') {
    throw 'DEPLOY_RELEASE_SIGNING_KEY já existe. Não será substituído.'
}

$rsa = [Security.Cryptography.RSA]::Create(3072)
$process = [Diagnostics.Process]::new()
$privatePem = $null
try {
    # A chave privada existe somente em memória e no stdin do gh. Não utiliza
    # arquivo temporário, argumento de processo, variável de ambiente ou log.
    $privatePem = $rsa.ExportPkcs8PrivateKeyPem()
    $publicPem = $rsa.ExportSubjectPublicKeyInfoPem()
    $process.StartInfo.FileName = (Get-Command gh -ErrorAction Stop).Source
    $process.StartInfo.UseShellExecute = $false
    $process.StartInfo.RedirectStandardInput = $true
    $process.StartInfo.RedirectStandardOutput = $true
    $process.StartInfo.RedirectStandardError = $true
    foreach ($argument in @('secret', 'set', 'DEPLOY_RELEASE_SIGNING_KEY', '--repo', $Repository, '--env', $EnvironmentName)) {
        $process.StartInfo.ArgumentList.Add($argument)
    }
    if (-not $process.Start()) { throw 'Não foi possível iniciar gh secret set.' }
    $process.StandardInput.Write($privatePem)
    $process.StandardInput.Close()
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    $process.WaitForExit()
    $null = $stdoutTask.GetAwaiter().GetResult()
    $null = $stderrTask.GetAwaiter().GetResult()
    if ($process.ExitCode -ne 0) { throw 'Falha ao cadastrar o secret de assinatura no GitHub.' }

    $parent = Split-Path -Parent $PublicKeyPath
    if ($parent) { $null = New-Item -ItemType Directory -Force -Path $parent }
    Set-Content -LiteralPath $PublicKeyPath -Value ($publicPem + "`n") -Encoding utf8 -NoNewline
    $fingerprint = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($rsa.ExportSubjectPublicKeyInfo())).ToLowerInvariant()
    Write-Output "Secret de assinatura cadastrado em $Repository / $EnvironmentName."
    Write-Output "Chave pública: $PublicKeyPath"
    Write-Output "SHA-256 SPKI: $fingerprint"
}
finally {
    $privatePem = $null
    $rsa.Dispose()
    $process.Dispose()
}
