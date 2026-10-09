#Requires -Version 7.4
<#
.SYNOPSIS
    Gera os segredos do Authelia de desenvolvimento e configura o client secret da API no user-secrets.

.DESCRIPTION
    Cria, em dev/authelia/secrets e dev/authelia/data (ambos ignorados pelo git):
      - CA e certificado TLS para https://auth.saldo360.localhost:9091
      - segredos de sessão, armazenamento e HMAC do OIDC
      - chave RSA de assinatura (JWKS) e o digest do client secret
      - um usuário de teste com senha aleatória (salva em secrets/dev_user_password.txt)
    O client secret em texto puro vai apenas para o user-secrets da API; nada é impresso no console.

.PARAMETER Username
    Login do usuário de teste.

.PARAMETER Force
    Regera tudo, invalidando segredos e sessões anteriores.
#>
param(
    [string] $Username = 'familia',
    [switch] $Force
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$root = Split-Path $PSScriptRoot -Parent
$autheliaDir = Join-Path $root 'dev/authelia'
$secretsDir = Join-Path $autheliaDir 'secrets'
$dataDir = Join-Path $autheliaDir 'data'
$apiProject = Join-Path $root 'src/Saldo360.Api'
$image = 'authelia/authelia:4.39'

if ((Test-Path (Join-Path $secretsDir 'oidc_client_secret.digest')) -and -not $Force) {
    Write-Host 'Segredos do Authelia já existem. Use -Force para regerar.'
    exit 0
}

if ($Force) {
    Remove-Item -Recurse -Force -Confirm:$false -ErrorAction SilentlyContinue $secretsDir, $dataDir
}
New-Item -ItemType Directory -Force (Join-Path $secretsDir 'tls'), $dataDir | Out-Null

function Invoke-Authelia([string[]] $Arguments) {
    docker run --rm -v "${secretsDir}:/out" $image authelia @Arguments
}

function Write-Secret([string] $Name, [string] $Value) {
    [System.IO.File]::WriteAllText((Join-Path $secretsDir $Name), $Value)
}

function New-RandomSecret([int] $Length = 64) {
    $chars = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789'.ToCharArray()
    [System.Security.Cryptography.RandomNumberGenerator]::GetString($chars, $Length)
}

# Gera uma senha aleatória e seu digest; devolve os dois sem exibi-los.
function New-HashedRandomSecret([string] $Algorithm, [int] $Length) {
    $output = Invoke-Authelia @('crypto', 'hash', 'generate', $Algorithm, '--random', '--random.length', "$Length")
    $plain = ($output | Select-String '^Random Password: (.+)$').Matches[0].Groups[1].Value
    $digest = ($output | Select-String '^Digest: (.+)$').Matches[0].Groups[1].Value
    if (-not $plain -or -not $digest) { throw "Falha ao gerar o hash $Algorithm." }
    [pscustomobject]@{ Plain = $plain; Digest = $digest }
}

Write-Host '> Certificados TLS (CA local + auth.saldo360.localhost)'
Invoke-Authelia @('crypto', 'certificate', 'rsa', 'generate', '--ca', '--common-name', 'Saldo360 Dev CA',
    '--duration', '5y', '--directory', '/out/tls',
    '--file.certificate', 'ca.public.crt', '--file.private-key', 'ca.private.pem') | Out-Null
Invoke-Authelia @('crypto', 'certificate', 'rsa', 'generate', '--common-name', 'auth.saldo360.localhost',
    '--sans', 'auth.saldo360.localhost', '--extended-usage', 'server_auth', '--duration', '1y',
    '--path.ca', '/out/tls', '--directory', '/out/tls') | Out-Null

Write-Host '> Segredos de sessão, armazenamento e OIDC'
Write-Secret 'session_secret' (New-RandomSecret)
Write-Secret 'storage_encryption_key' (New-RandomSecret)
Write-Secret 'oidc_hmac_secret' (New-RandomSecret)
Invoke-Authelia @('crypto', 'pair', 'rsa', 'generate', '--bits', '2048', '--directory', '/out',
    '--file.private-key', 'oidc_jwks.pem', '--file.public-key', 'oidc_jwks.public.pem') | Out-Null

$client = New-HashedRandomSecret 'pbkdf2' 72
Write-Secret 'oidc_client_secret.digest' $client.Digest

Write-Host "> Usuário de teste '$Username'"
$user = New-HashedRandomSecret 'argon2' 24
Write-Secret 'dev_user_password.txt' $user.Plain
@"
users:
  ${Username}:
    disabled: false
    displayname: 'Família Teste'
    password: '$($user.Digest)'
    email: '${Username}@saldo360.localhost'
    groups: []
"@ | Set-Content -NoNewline (Join-Path $dataDir 'users_database.yml')

Write-Host '> Client secret da API no user-secrets'
dotnet user-secrets set 'Authentication:Oidc:ClientSecret' $client.Plain --project $apiProject | Out-Null

Write-Host ''
Write-Host 'Pronto. Suba o provedor com: docker compose up -d --wait'
Write-Host "Senha do usuário '$Username': dev/authelia/secrets/dev_user_password.txt"
