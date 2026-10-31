<#
.SYNOPSIS
  Sonda SOMENTE LEITURA da API Pluggy para verificar o que ela realmente devolve (type, operationType, status, moeda, data bruta, paginação e cobertura).
.DESCRIPTION
  Só faz GET (a única chamada POST é a autenticação /auth, que apenas troca clientId/clientSecret por uma apiKey temporária; nada é criado, atualizado,
  sincronizado, apagado nem registrado em webhook). As credenciais vêm de PLUGGY_CLIENT_ID/PLUGGY_CLIENT_SECRET (ambiente) ou dos user-secrets da API
  (Pluggy:ClientId/Pluggy:ClientSecret, gravados por scripts/pluggy-poc-load-env.ps1). NUNCA lê o .env, nunca imprime credenciais, tokens nem ids completos.
  Saída mascarada em arquivo JSON (padrão: pasta temporária do usuário): ids truncados (8), descrição truncada (14), sem número de conta.
  -SelfTest processa uma resposta sintética (sem rede) para validar a lógica de resumo e de mascaramento.
.PARAMETER ItemId
  Ids dos Items Pluggy (Itaú PF e PJ). O Saldo360 os guarda em pluggy_item_links; não são segredos, mas são mascarados na saída.
.PARAMETER Amount
  Valor opcional (positivo) para listar TODAS as ocorrências candidatas (por exemplo 55.99, 320, 17000). Busca por valor é localizador, não identificador.
.PARAMETER MaxPages
  Limite de páginas por conta (padrão 60), para detectar se a paginação trunca o resultado.
#>
param(
    [string[]]$ItemId = @(),
    [decimal]$Amount = 0,
    [int]$MaxPages = 60,
    [string]$OutFile = (Join-Path ([IO.Path]::GetTempPath()) 'pluggy-readonly-probe.json'),
    [switch]$SelfTest
)
$ErrorActionPreference = 'Stop'
$base = 'https://api.pluggy.ai'

function Mask([string]$value, [int]$keep) { if ([string]::IsNullOrEmpty($value)) { return $null }; if ($value.Length -le $keep) { return $value }; return $value.Substring(0, $keep) + '…' }

function Summarize($rows, [decimal]$wanted) {
    $withTime = @($rows | Where-Object { $_.date -and $_.date -notmatch 'T00:00:00(\.000)?Z?$' }).Count
    $dates = @($rows | Where-Object { $_.date } | ForEach-Object { $_.date } | Sort-Object)
    [ordered]@{
        rows              = @($rows).Count
        firstRawDate      = if ($dates.Count) { $dates[0] } else { $null }
        lastRawDate       = if ($dates.Count) { $dates[-1] } else { $null }
        rowsWithTimeOfDay = $withTime
        byType            = @($rows | Group-Object type | ForEach-Object { [ordered]@{ value = $_.Name; count = $_.Count } })
        byOperationType   = @($rows | Group-Object operationType | ForEach-Object { [ordered]@{ value = $_.Name; count = $_.Count } })
        byStatus          = @($rows | Group-Object status | ForEach-Object { [ordered]@{ value = $_.Name; count = $_.Count } })
        byCurrency        = @($rows | Group-Object currencyCode | ForEach-Object { [ordered]@{ value = $_.Name; count = $_.Count } })
        candidates        = if ($wanted -gt 0) {
            @($rows | Where-Object { [math]::Abs([decimal]$_.amount) -eq $wanted } | ForEach-Object {
                [ordered]@{ id = Mask $_.id 8; rawDate = $_.date; amount = $_.amount; type = $_.type; operationType = $_.operationType; status = $_.status
                            category = $_.category; description = Mask $_.description 14 } })
        } else { @() }
    }
}

if ($SelfTest) {
    $sample = @(
        [pscustomobject]@{ id = 'aaaaaaaa-1111'; date = '2026-10-23T00:00:00.000Z'; amount = 55.99; type = 'CREDIT'; operationType = 'PIX'; status = 'POSTED'; currencyCode = 'BRL'; category = 'Shopping'; description = 'Pix devolvido Pix Marketplace' },
        [pscustomobject]@{ id = 'bbbbbbbb-2222'; date = '2026-10-13T00:00:00.000Z'; amount = -55.99; type = 'DEBIT'; operationType = 'PIX'; status = 'POSTED'; currencyCode = 'BRL'; category = 'Shopping'; description = 'Pagamento de Pix QR Code Pix Marketplace' },
        [pscustomobject]@{ id = 'cccccccc-3333'; date = '2026-10-09T15:30:00.000Z'; amount = 17000; type = 'CREDIT'; operationType = 'TED'; status = 'POSTED'; currencyCode = 'BRL'; category = $null; description = 'TED 237.2276.WK R H E' })
    $result = Summarize $sample 55.99
    $result | ConvertTo-Json -Depth 6
    if ($result.rows -ne 3 -or $result.rowsWithTimeOfDay -ne 1 -or $result.candidates.Count -ne 2) { throw 'SelfTest falhou.' }
    if (($result | ConvertTo-Json -Depth 6) -match 'Marketplace') { throw 'SelfTest falhou: descrição não mascarada.' }
    Write-Host 'SelfTest OK (sem rede, dados sintéticos).'
    return
}

if (-not $ItemId.Count) { throw 'Informe -ItemId (um ou mais Items Pluggy).' }
$clientId = $env:PLUGGY_CLIENT_ID; $clientSecret = $env:PLUGGY_CLIENT_SECRET
if (-not $clientId -or -not $clientSecret) {
    $secrets = Join-Path $env:APPDATA 'Microsoft\UserSecrets\saldo360-api-dev\secrets.json'
    if (-not (Test-Path $secrets)) { throw 'Credenciais ausentes: defina PLUGGY_CLIENT_ID/PLUGGY_CLIENT_SECRET ou execute scripts/pluggy-poc-load-env.ps1 (user-secrets). Não leia o .env.' }
    $json = Get-Content $secrets -Raw | ConvertFrom-Json
    $clientId = $json.'Pluggy:ClientId'; $clientSecret = $json.'Pluggy:ClientSecret'
    if (-not $clientId -or -not $clientSecret) { throw 'Pluggy:ClientId/Pluggy:ClientSecret ausentes nos user-secrets.' }
}
$auth = Invoke-RestMethod -Method Post -Uri "$base/auth" -ContentType 'application/json' -Body (@{ clientId = $clientId; clientSecret = $clientSecret } | ConvertTo-Json)
$headers = @{ 'X-API-KEY' = $auth.apiKey; Accept = 'application/json' }
$clientId = $null; $clientSecret = $null

$report = [ordered]@{ generatedAtUtc = (Get-Date).ToUniversalTime().ToString('o'); readOnly = $true; items = @() }
foreach ($item in $ItemId) {
    $accounts = (Invoke-RestMethod -Method Get -Uri "$base/accounts?itemId=$([uri]::EscapeDataString($item))" -Headers $headers).results
    $entry = [ordered]@{ item = Mask $item 8; accounts = @() }
    foreach ($account in $accounts) {
        $rows = New-Object System.Collections.Generic.List[object]; $pages = 0; $truncated = $false; $after = $null; $seenCursors = @{}
        do {
            $pages++
            $uri = "$base/v2/transactions?accountId=$([uri]::EscapeDataString($account.id))" + $(if ($after) { "&after=$([uri]::EscapeDataString($after))" } else { '' })
            $page = Invoke-RestMethod -Method Get -Uri $uri -Headers $headers
            foreach ($row in $page.results) { $rows.Add($row) }
            $after = $null
            if ($page.next) { $m = [regex]::Match([string]$page.next, 'after=([^&]+)'); if ($m.Success) { $after = [uri]::UnescapeDataString($m.Groups[1].Value) } }
            if ($after -and $seenCursors.ContainsKey($after)) { $truncated = $true; break }
            if ($after) { $seenCursors[$after] = $true }
            if ($pages -ge $MaxPages -and $after) { $truncated = $true; break }
        } while ($after)
        $summary = Summarize $rows $Amount
        $entry.accounts += [ordered]@{ account = Mask $account.id 8; type = $account.type; subtype = $account.subtype; pagesRead = $pages; stoppedBeforeEnd = $truncated; summary = $summary }
    }
    $report.items += $entry
}
$report | ConvertTo-Json -Depth 8 | Set-Content -Path $OutFile -Encoding UTF8
Write-Host "Relatório mascarado gravado em: $OutFile"
