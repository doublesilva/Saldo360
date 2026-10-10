#Requires -Version 7.4
<#
.SYNOPSIS
    Gera um segredo EXCLUSIVO para o client "saldo360" no Authelia do servidor.

.DESCRIPTION
    - Gera um segredo aleatório (72 caracteres alfanuméricos) e seu digest com a CLI do Authelia (Docker).
    - Grava o texto puro no user-secrets da API (Authentication:Oidc:ClientSecret) e,
      opcionalmente, em um arquivo (-OutFile) para provisionar outro ambiente.
    - Imprime SOMENTE o digest PBKDF2-SHA512, que vai para o cadastro do client no Authelia.
    O texto puro nunca é impresso nem passado como argumento de linha de comando: é lido da
    saída do container e entregue ao dotnet user-secrets pela entrada padrão.

.PARAMETER OutFile
    Opcional: também grava o segredo em texto puro neste arquivo (ex.: para montar em /run/secrets).

.EXAMPLE
    ./scripts/new-oidc-client-secret.ps1
#>
param(
    [string] $OutFile
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$root = Split-Path $PSScriptRoot -Parent
$apiProject = Join-Path $root 'src/Saldo360.Api'
$image = 'authelia/authelia:4.39'

# O Authelia gera o segredo e o digest; a saída é capturada aqui e o texto puro não é exibido.
$output = docker run --rm $image authelia crypto hash generate pbkdf2 --variant sha512 --random --random.length 72
$secret = ($output | Select-String '^Random Password: (.+)$').Matches[0].Groups[1].Value
$digest = ($output | Select-String '^Digest: (.+)$').Matches[0].Groups[1].Value
if (-not $secret -or -not $digest) { throw 'Falha ao gerar o segredo e o digest PBKDF2.' }

# "dotnet user-secrets set" sem argumentos lê pares em JSON da entrada padrão.
@{ 'Authentication:Oidc:ClientSecret' = $secret } | ConvertTo-Json -Compress |
    dotnet user-secrets set --project $apiProject | Out-Null

if ($OutFile) {
    [System.IO.File]::WriteAllText($OutFile, $secret)
    Write-Host "Segredo em texto puro gravado em: $OutFile (proteja este arquivo)"
}

Write-Host 'Segredo gravado no user-secrets da API (Authentication:Oidc:ClientSecret).'
Write-Host ''
Write-Host 'Digest para o cadastro do client saldo360 no Authelia (client_secret):'
Write-Host $digest
