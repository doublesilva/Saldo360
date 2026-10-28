# Laboratório Pluggy (T09.1) — configuração e uso

POC **somente leitura** para avaliar dados do Open Finance via Pluggy. Não lança transações, não cria contas, não altera saldos, previsões, cartões, faturas ou a projeção, e não implementa conciliação. Os resultados da avaliação ficam em [poc-pluggy-resultados.md](poc-pluggy-resultados.md).

## Configuração local

O laboratório vem **desligado** (`Pluggy:Enabled=false`) e, desligado, as rotas respondem 404. Credenciais nunca vão em `appsettings*.json` nem para o React.

```powershell
dotnet user-secrets set "Pluggy:Enabled" "true" --project src/Saldo360.Api
dotnet user-secrets set "Pluggy:ClientId" "<client-id do Dashboard da Pluggy>" --project src/Saldo360.Api
dotnet user-secrets set "Pluggy:ClientSecret" "<client-secret>" --project src/Saldo360.Api
```

Atalho: preencha `PLUGGY_CLIENT_ID` e `PLUGGY_CLIENT_SECRET` no `.env` (ignorado pelo git) e rode `pwsh scripts/pluggy-poc-load-env.ps1`, que grava as três chaves em user-secrets sem exibir os valores. A API não lê o `.env` diretamente.

Equivalente por variáveis de ambiente: `Pluggy__Enabled`, `Pluggy__ClientId`, `Pluggy__ClientSecret`. Opcionais: `Pluggy__BaseUrl` (padrão `https://api.pluggy.ai`), `Pluggy__TimeoutSeconds` (30) e `Pluggy__MaxTransactionPages` (20).

Aplique a migration `AddPluggyItemLinks` (tabela `pluggy_item_links`, só com `itemId`, orçamento, usuário e rótulo). Não habilite em produção.

## Cadastrar o itemId

1. No Dashboard da Pluggy, conecte a instituição pelo fluxo **Meu Pluggy** (a autorização é manual; o widget de onboarding não faz parte da POC).
2. Copie o **Item ID** (UUID) da conexão.
3. No Saldo360, abra o menu **Laboratório → Laboratório Pluggy** (aparece só com o laboratório habilitado), informe o itemId e um rótulo opcional e escolha *Cadastrar Item*. A API confirma que o Item existe para as credenciais configuradas antes de gravar o vínculo.

Ou via API (com sessão e token CSRF): `POST /api/financial-workspaces/{workspaceId}/pluggy-poc/connections` com `{"itemId":"…","label":"Itaú PF"}`.

## Endpoints

Todos exigem sessão e dono do orçamento; respostas usam `Cache-Control: no-store`. Seguem o padrão de rotas do projeto (prefixo `/api/financial-workspaces/{workspaceId}`).

| Método | Rota (após `/pluggy-poc`) | Descrição |
|---|---|---|
| GET | `/status` | 200 se o laboratório está habilitado para o usuário; 404 caso contrário |
| GET | `/connections` | Conexões: instituição, estado, última atualização, expiração, erro |
| POST | `/connections` | Cadastra um `itemId` (CSRF) |
| GET | `/connections/{id}/accounts` | Contas e saldos (números mascarados) |
| GET | `/connections/{id}/diagnostics` | Diagnóstico de cobertura do Item |
| GET | `/accounts/{accountId}/transactions?from=&to=&after=` | Transações (`GET /v2/transactions`), cursor em `nextCursor` |
| GET | `/accounts/{accountId}/bills?page=&pageSize=` | Faturas (`GET /bills`) |

Falhas de integração viram erros controlados: `PLUGGY_NOT_CONFIGURED` (503), `PLUGGY_RATE_LIMITED` (429), `PLUGGY_TIMEOUT` (504), `PLUGGY_UNAVAILABLE` (503), `PLUGGY_AUTHENTICATION_FAILED`/`PLUGGY_ACCESS_DENIED`/`PLUGGY_REQUEST_REJECTED`/`PLUGGY_INVALID_RESPONSE` (502). Não há rota de pagamento, transferência nem criação de token de conexão.

## Segurança

- A API Key da Pluggy fica só em memória do backend, com renovação sob `SemaphoreSlim` (uma autenticação por vez) e nova tentativa única em 401.
- O cliente HTTP não registra URLs; erros não carregam corpo, URL, token nem segredo.
- Números de conta, agência e cartão saem mascarados (últimos 4); CPF/CNPJ, pagador e recebedor não são expostos (apenas se existem). Não há JSON bruto na interface.
- Um `itemId` só pode ter um vínculo; outro usuário ou orçamento recebe 404/409.
