# Saldo360

Aplicação de orçamento familiar: API ASP.NET Core 10 com EF Core + PostgreSQL, frontend React + TypeScript (Vite) e login via OpenID Connect com sessão mantida pela API.

## Estrutura

```
Saldo360.slnx
src/
  Saldo360.Domain/          Entidades e regras de negócio (sem dependências externas)
  Saldo360.Application/     Casos de uso; depende apenas do Domain
  Saldo360.Infrastructure/  EF Core (Npgsql), migrations, sessões no banco, Data Protection
  Saldo360.Api/             Host ASP.NET Core: /health, /auth/*, /api/*
tests/
  Saldo360.Tests/           Testes de integração da API (xUnit + WebApplicationFactory)
client/                     React + TypeScript (Vite)
dev/authelia/               Authelia local opcional (o provedor padrão é o Authelia do servidor)
scripts/                    Setup do provedor de desenvolvimento e smoke test de login
docs/                       Documentação de autenticação e dos módulos financeiros
docker-compose.yml          PostgreSQL (e Authelia local opcional, profile authelia-local)
```

Dependências: `Api → Application, Infrastructure`; `Infrastructure → Application, Domain`; `Application → Domain`.

## Pré-requisitos

- .NET SDK 10
- Node.js 20+ e npm
- Docker
- PowerShell 7.4+ (scripts de segredo do client e do Authelia local)

## Configuração de desenvolvimento

Nenhuma credencial fica no repositório. Os valores reais ficam em arquivos ignorados pelo git (`.env`, `dev/certs/`, `dev/authelia/secrets/`, `dev/authelia/data/`) ou no user-secrets do .NET.

1. **Banco de dados** — copie o exemplo e troque a senha:

   ```bash
   cp .env.example .env        # edite POSTGRES_PASSWORD
   ```

2. **Suba o PostgreSQL**:

   ```bash
   docker compose up -d --wait
   ```

3. **Connection string da API** — guarde no user-secrets (fica no seu perfil de usuário, fora do projeto). Use os mesmos valores do `.env`:

   ```bash
   dotnet user-secrets set "ConnectionStrings:Saldo360" \
     "Host=localhost;Port=5432;Database=saldo360;Username=saldo360;Password=<sua-senha>" \
     --project src/Saldo360.Api
   ```

4. **Provedor OIDC: Authelia do servidor** (`https://auth.home.doublesilva.com`, client `saldo360`):

   ```powershell
   pwsh scripts/new-oidc-client-secret.ps1   # segredo exclusivo → user-secrets; imprime só o digest
   ```

   - Cadastre o client no Authelia do servidor com o digest, usando o trecho [docs/authelia-client-saldo360.yml](docs/authelia-client-saldo360.yml) (acrescentar, sem substituir os clients existentes).
   - Coloque a CA do Caddy em `dev/certs/caddy-local-root.crt` (ignorado pelo git); os comandos estão em [docs/autenticacao.md](docs/autenticacao.md#3-confiança-na-ca-do-caddy).

   Sem acesso ao servidor? Use o Authelia local opcional (`npm run setup:authelia-local` e `npm run dev:authelia-local`), descrito em [docs/autenticacao.md](docs/autenticacao.md#provedor-local-opcional-authelia-no-compose).

5. **Migrations do banco**:

   ```bash
   dotnet tool restore
   dotnet ef database update -p src/Saldo360.Infrastructure -s src/Saldo360.Api -- --environment Development
   ```

6. **Frontend**:

   ```bash
   cd client && npm install
   ```

   O Vite encaminha `/health`, `/api`, `/auth` e `/signin-oidc` para `http://localhost:5080`, preservando o host `localhost:5173`. Para outro endereço da API, copie `client/.env.example` para `client/.env.local` e ajuste `API_PROXY_TARGET`.

Em outros ambientes, use variáveis de ambiente (`ConnectionStrings__Saldo360`, `Authentication__Oidc__ClientSecretFile`, `ReverseProxy__…`). As chaves esperadas estão em [src/Saldo360.Api/appsettings.Example.json](src/Saldo360.Api/appsettings.Example.json) (só referência; não é carregado nem publicado). Detalhes de autenticação, CA, proxy reverso e logs em [docs/autenticacao.md](docs/autenticacao.md).

## Executando

O `package.json` da raiz reúne os atalhos. Depois de criar o `.env`, a connection string e o segredo do client (passos 1, 3 e 4):

```bash
npm install          # instala o orquestrador (concurrently)
npm run setup        # dotnet tools e dependências do frontend (uma vez)
npm run dev          # sobe o PostgreSQL no Docker, aplica migrations e roda API + frontend
```

| Script | O que faz |
| --- | --- |
| `npm run dev` | `docker:up` → `db:migrate` → `start` (login pelo Authelia do servidor) |
| `npm run dev:authelia-local` | Mesmo fluxo com o Authelia local (profile `authelia-local`) |
| `npm start` | API (`:5080`) e frontend (`:5173`) juntos, sem mexer no Docker |
| `npm run docker:up` / `docker:down` | Sobe o PostgreSQL / para todos os containers (os dados ficam nos volumes) |
| `npm run docker:up:authelia-local` | Sobe PostgreSQL e o Authelia local |
| `npm run docker:logs` / `docker:ps` | Logs e status dos containers |
| `npm run db:migrate` | Aplica as migrations no banco de desenvolvimento |
| `npm run api` / `api:authelia-local` / `web` | Só a API (servidor ou Authelia local) / só o frontend |
| `npm run setup:client-secret` | Gera o segredo exclusivo do client `saldo360` (servidor) |
| `npm run setup:authelia-local` | Gera segredos e usuário do Authelia local |
| `npm run check` | Build, testes e lint |
| `npm run smoke:login` / `smoke:login:authelia-local` | Login real via curl contra o Authelia do servidor / local |

Ou, manualmente:

```bash
dotnet run --project src/Saldo360.Api   # API em http://localhost:5080
cd client && npm run dev                # Frontend em http://localhost:5173
```

Acesse **<http://localhost:5173>** (não a porta da API): a página mostra **Entrar** ou o usuário conectado com **Sair**, além do status da API.

## Endpoints

| Método | Caminho | Autenticação | Descrição |
| --- | --- | --- | --- |
| GET | `/health` | pública | Saúde da API e do banco. `200` saudável, `503` com falha. |
| GET | `/auth/login?returnUrl=/caminho` | pública | Inicia o login no provedor. Ao final volta para `returnUrl` (só URLs locais). |
| GET | `/signin-oidc` | — | Callback do provedor (tratado pelo middleware OIDC). |
| GET | `/auth/csrf` | pública | Devolve `{ "token" }` para o header `X-CSRF-TOKEN`. |
| POST | `/auth/logout` | sessão + CSRF | Remove a sessão no servidor e apaga o cookie. `204`; `400` sem token CSRF válido. |
| GET | `/api/me` | sessão | `{ id, issuer, subject, displayName, username, email }`; `401` sem sessão. A identidade é o par `issuer` + `subject` (`id` = `subject`, só por compatibilidade). |
| POST | `/api/families` | sessão + CSRF | Corpo `{ "name" }` (obrigatório, até 100 caracteres). Cria o núcleo com o usuário da sessão como único `Owner`. `201`; `400` com `errors.name`, campos extras ou sem CSRF. |
| GET | `/api/families` | sessão | Núcleos dos quais o usuário é membro: `[{ id, name, createdAt, myRole, memberCount }]`. |
| GET | `/api/families/{id}` | sessão | `{ id, name, createdAt, myRole, members: [{ displayName, role, joinedAt, isCurrentUser }] }`; `404` se não existir ou se o usuário não for membro. |
| POST | `/api/families/{id}/invitations` | Owner + CSRF | Gera convite de uso único, válido por 48 h. `201 { id, token, expiresAt }`: o token só aparece nesta resposta e o banco guarda apenas o SHA-256. `403` para Member; `404` para família inexistente ou de outros. |
| DELETE | `/api/families/{id}/invitations/{invitationId}` | Owner + CSRF | Revoga convite pendente. `204` (idempotente); `409` se já foi usado ou expirou; `403` para Member; `404` se não existir nessa família. |
| POST | `/api/family-invitations/accept` | sessão + CSRF | Corpo `{ "token" }`. Entra como `Member` e devolve `{ alreadyMember, family }`. Quem já é membro mantém o papel e não consome o convite. Token inválido, expirado, revogado ou usado: `404` com mensagem genérica. |
| POST | `/api/families/{id}/accounts` | membro + CSRF | Corpo `{ name, institution?, type, financialScope, responsibleMemberId?, openingBalance?, openingBalanceDate? }`. `type`: `Checking`, `Savings` ou `Cash`; `financialScope`: `PF` ou `PJ`. Responsável nulo significa conta conjunta. Saldo inicial opcional: valor (texto decimal `"-1234.56"`, recomendado, ou número; até 2 casas, pode ser negativo) e data `yyyy-MM-dd` juntos ou ambos ausentes — é o saldo no início dessa data, antes das movimentações do dia ([regra](docs/saldo-inicial.md)). Moeda BRL. Criador e família vêm do servidor. `201`; `400` com `errors.<campo>` (inclusive "Responsável inválido." para vínculo de outra família) ou com campos extras. |
| GET | `/api/families/{id}/accounts[?includeInactive=true]` | membro | Contas da família (só as ativas, por padrão): `[{ id, name, institution, type, financialScope, responsible: { memberId, displayName } | null, openingBalance, openingBalanceDate, isActive, currency, createdAt }]`. `openingBalance` vem sempre como texto decimal exato com duas casas (`"-1234.56"`, `"0.00"`) ou `null` quando não informado (não zero), em criação, listagem, consulta e edição.\| null, isActive, currency, createdAt }]`. |
| GET | `/api/families/{id}/accounts/{accountId}` | membro | Uma conta; `404` se não existir nessa família ou se o usuário não for membro. |
| PUT | `/api/families/{id}/accounts/{accountId}` | membro + CSRF | Mesmos campos do POST mais `isActive` (obrigatório): edita e ativa/desativa. Substitui o cadastro inteiro: saldo inicial omitido ou nulo remove a configuração. Não há exclusão física. |
| GET | `/api/families/{id}/accounts/{accountId}/balance[?asOf=yyyy-MM-dd]` | membro | Saldo realizado no fechamento de `asOf` (padrão: hoje em America/Sao_Paulo; futura ou fora do formato: `400`): saldo inicial + receitas − despesas + transferências recebidas − enviadas, não canceladas, com referência da conta ≤ data ≤ `asOf`, agregado no PostgreSQL. `{ accountId, asOf, openingBalance, openingBalanceDate, totalIncome, totalExpense, totalTransferIn, totalTransferOut, balance, unavailableReason }`, valores como texto decimal exato (`"1300.00"`). Sem saldo inicial ou antes da referência: totais e saldo `null` e `unavailableReason` `OpeningBalanceNotConfigured`/`BeforeOpeningBalanceDate` ([fórmula e retornos](docs/saldo-inicial.md#saldo-realizado)). Conta inativa também. |
| GET | `/api/families/{id}/accounts/{accountId}/projection?through=yyyy-MM-dd` | membro | Saldo projetado até `through` (obrigatório, hoje ou futuro; passado, ausente ou fora do formato: `400`): saldo realizado de hoje (America/Sao_Paulo) + previsões **pendentes** vencidas (`overdue*`, antes de hoje) e do período (`planned*`, hoje até `through`); canceladas e realizadas ficam de fora. `{ accountId, baseDate, through, realizedBalance, overdueIncome, overdueExpense, plannedIncome, plannedExpense, projectedBalance, unavailableReason }` em texto decimal exato; saldo realizado indisponível: mesmo motivo e todos os valores `null`. Leitura num único snapshot (REPEATABLE READ) ([fórmula e regras](docs/saldo-inicial.md#saldo-projetado)). Conta inativa também. |
| GET | `/api/families/{id}/financial-calendar?from=yyyy-MM-dd&through=yyyy-MM-dd` | membro | Agenda unificada no período inclusivo (passado, presente ou futuro), em ordem cronológica. `from` e `through` são obrigatórios e estritamente ISO; filtros opcionais `accountId` e `type=Income\|Expense` (omitido = ambos). Retorna `{ from, through, items, summary }`; cada item contém `{ id, source, status, type, amount, date, description, account, category, currency, plannedTransactionId }`, com `amount` em texto decimal exato. `summary` traz `realizedIncome`, `realizedExpense`, `pendingIncome`, `pendingExpense`, `overdueIncome` e `overdueExpense`, todos como strings decimais exatas e submetidos ao período e aos filtros. Os totais vencidos são subconjuntos dos pendentes, não parcelas adicionais; vencimento hoje não é vencido. `source`: `Transaction` ou `PlannedTransaction`; `status`: `Realized`, `Pending` ou `Overdue` (dia civil de America/Sao_Paulo). Não inclui cancelados nem duplica previsão realizada: ela aparece somente como lançamento, pela data e valor efetivos. Cancelar esse lançamento reabre a previsão pela data e valor previstos. Leitura num único snapshot REPEATABLE READ somente leitura. |
| GET | `/api/families/{id}/financial-calendar/balances?from=yyyy-MM-dd&through=yyyy-MM-dd[&accountId=...]` | membro | Evolução diária do saldo. Antes de hoje retorna fechamentos realizados; hoje e futuro usam a fórmula da T06.3, aplicando vencidas uma vez e previsões pendentes em suas datas. Sem conta, consolida todas as contas e neutraliza transferências internas; com conta, transferências entram/saem em campos próprios. Retorna `{ from, through, accountId, openingBalance, unavailableReason, days, risk }`; cada dia informa `kind`, fluxos, fechamento, risco negativo, vencidas e coordenada visual calculada no backend. Dinheiro é string decimal exata ou `null` com o motivo existente de indisponibilidade. Snapshot REPEATABLE READ somente leitura e agregação diária no PostgreSQL ([detalhes](docs/agenda-financeira.md#evolução-diária-do-saldo)). |
| GET | `/api/families/{id}/financial-calendar/monthly-projection?from=yyyy-MM&months=6[&accountId=...]` | membro | Planejamento de 1 a 12 competências (`6` por padrão), com realizados, pendentes, vencidos, abertura, fechamento e risco calculados no backend. Reutiliza a evolução diária, mantém vencidas anteriores apenas na projeção, neutraliza transferências internas no consolidado e retorna dinheiro como texto exato ou `null` quando indisponível ([detalhes](docs/agenda-financeira.md#planejamento-dos-próximos-seis-meses)). |
| POST/GET | `/api/families/{id}/budgets` | membro (+ CSRF na escrita) | Cria e lista orçamentos por competência `yyyy-MM`; apenas um por família/mês. A listagem aceita `competence` e `includeArchived`. Valores são strings decimais exatas ([modelo e contrato](docs/orcamentos.md)). |
| GET/PUT/DELETE | `/api/families/{id}/budgets/{budgetId}` | membro (+ CSRF nas escritas) | Consulta, edita a descrição ou arquiva logicamente um orçamento mensal. Competência é imutável. |
| GET | `/api/families/{id}/budgets/{budgetId}/execution` | membro | Execução das metas ativas: planejado, realizado, comprometido, consumo projetado, disponível, excedente e percentual. Agrega despesas por competência em snapshot somente leitura e retorna decimais exatos ([fórmulas e contrato](docs/orcamentos.md#acompanhamento-da-execução)). |
| POST/PUT/DELETE | `/api/families/{id}/budgets/{budgetId}/targets[/{targetId}]` | membro + CSRF | Cria, edita ou arquiva meta de despesa por categoria. Categorias inativas podem ser referenciadas para preservar planejamento histórico. |
| POST | `/api/families/{id}/categories` | membro + CSRF | Corpo `{ name, type }`; `type`: `Income` (receita) ou `Expense` (despesa). Nome sem espaços nas pontas, até 100 caracteres, único por família e tipo sem diferenciar maiúsculas/minúsculas (inativas incluídas). Serve contas PF e PJ. `201`; `400` com `errors.<campo>` ou campos extras; `409` se o nome já existir. |
| GET | `/api/families/{id}/categories[?includeInactive=true]` | membro | Categorias da família (só as ativas, por padrão), receitas antes de despesas: `[{ id, name, type, isActive, createdAt }]`. |
| GET | `/api/families/{id}/categories/{categoryId}` | membro | Uma categoria; `404` se não existir nessa família ou se o usuário não for membro. |
| PUT | `/api/families/{id}/categories/{categoryId}` | membro + CSRF | Corpo `{ name, isActive }` (ambos obrigatórios): renomeia e ativa/desativa. O tipo é imutável: enviá-lo dá `400`. `409` se o novo nome já existir. Não há exclusão física. |
| POST | `/api/families/{id}/transactions` | membro + CSRF | Lançamento único **já realizado**. Corpo `{ accountId, categoryId, type, description, amount, transactionDate }`. `type`: `Income` ou `Expense` (igual ao da categoria). `amount` positivo, até 2 casas (texto `"123.45"`, recomendado, ou número); despesa não usa valor negativo. **Respostas** (criação, consulta, listagem, edição e cancelamento) trazem `amount` sempre como texto decimal exato com duas casas (`"1234.56"`), nunca número JSON. `transactionDate` `yyyy-MM-dd`, não futura em America/Sao_Paulo (pode ser anterior ao saldo inicial: [regra](docs/saldo-inicial.md#lançamentos-anteriores-ao-saldo-inicial)). Conta e categoria da família e ativas. `201`; `400` com `errors.<campo>` ("Conta inválida."/"Categoria inválida." também para referências de outra família) ou campos extras. |
| GET | `/api/families/{id}/transactions` | membro | Página `{ items, page, pageSize, totalCount }`, data decrescente e Id decrescente. Filtros: `from`, `to` (`yyyy-MM-dd`, inclusivos), `accountId`, `categoryId`, `type`, `includeCancelled=true`; `page` (1+) e `pageSize` (1–100, padrão 50). Item: `{ id, type, amount ("1234.56"), transactionDate, description, account: { id, name, isActive }, category: { id, name, isActive }, isCancelled, currency, createdAt, updatedAt, cancelledAt }`. |
| GET | `/api/families/{id}/transactions/{transactionId}` | membro | Um lançamento (inclusive cancelado); `404` fora da família. |
| PUT | `/api/families/{id}/transactions/{transactionId}` | membro + CSRF | Mesmo corpo do POST; registra `updatedAt` e o autor da edição. Pode manter conta/categoria inativas já usadas; novas escolhas precisam estar ativas. Cancelado ou vinculado a uma previsão (`plannedTransactionId`): `409` — para corrigir um lançamento efetivo, cancele-o e realize a previsão de novo. |
| POST | `/api/families/{id}/transactions/{transactionId}/cancel` | membro + CSRF | Cancela sem excluir (idempotente, `200`). Cancelado some da lista padrão e não pode ser editado. Se o lançamento realiza uma previsão, ela volta a `Pending` na mesma operação (vínculo e auditoria preservados). |
| POST | `/api/families/{id}/transfers` | membro + CSRF | Transferência **já realizada** entre contas da família (PF↔PJ ou mesmo escopo): um único registro, sem categoria, que não gera receita nem despesa. Corpo `{ sourceAccountId, destinationAccountId, amount, transferDate, description }`. Contas distintas, da família e ativas; `amount` positivo, até 2 casas; `transferDate` `yyyy-MM-dd`, não futura em America/Sao_Paulo. A origem pode ficar negativa. `201`; `400` com `errors.<campo>` ("Conta de origem/destino inválida." também para outra família) ou campos extras. Sem edição: para corrigir, cancele e cadastre de novo. |
| GET | `/api/families/{id}/transfers` | membro | Página `{ items, page, pageSize, totalCount }`, data decrescente e Id decrescente. Filtros `from`, `to` (`yyyy-MM-dd`), `accountId` (origem **ou** destino), `includeCancelled=true`; `pageSize` 1–100 (padrão 50). Item: `{ id, amount ("500.00"), transferDate, description, sourceAccount, destinationAccount: { id, name, financialScope, isActive }, isCancelled, currency, createdAt, cancelledAt }`. |
| GET | `/api/families/{id}/transfers/{transferId}` | membro | Uma transferência, inclusive cancelada; `404` fora da família. |
| POST | `/api/families/{id}/transfers/{transferId}/cancel` | membro + CSRF | Cancela sem excluir (idempotente, `200`); os saldos das duas contas voltam ao valor anterior. |
| POST | `/api/families/{id}/planned-transactions` | membro + CSRF | Receita ou despesa **prevista** única (o que se espera receber/pagar). Corpo `{ accountId, categoryId, type, description, amount, dueDate }`: mesmas regras de conta, categoria (tipo compatível), descrição e valor dos lançamentos (`amount` positivo, até 2 casas, texto `"123.45"` recomendado), mas `dueDate` (`yyyy-MM-dd`) pode ser passada, hoje ou futura. Não gera lançamento nem altera o saldo realizado; vencida continua pendente. `201`; `400` com `errors.<campo>` ou campos extras. |
| GET | `/api/families/{id}/planned-transactions` | membro | Página `{ items, page, pageSize, totalCount }`, **data prevista crescente** e Id crescente. Filtros `from`, `to` (sobre `dueDate`, `yyyy-MM-dd`), `accountId`, `categoryId`, `type`, `includeRealized=true`, `includeCancelled=true` (por padrão só pendentes); `pageSize` 1–100 (padrão 50). Item: `{ id, type, amount ("1234.56"), dueDate, description, account, category: { id, name, isActive }, status (Pending/Realized/Cancelled), realization: { transactionId, amount, transactionDate } | null, isCancelled, currency, createdAt, updatedAt, cancelledAt }` — `amount`/`dueDate` são sempre os previstos. "Vencida" não é retornado nem gravado: é derivado na tela. |
| GET | `/api/families/{id}/planned-transactions/{plannedId}` | membro | Uma previsão, inclusive cancelada; `404` fora da família. |
| PUT | `/api/families/{id}/planned-transactions/{plannedId}` | membro + CSRF | Mesmo corpo do POST; registra `updatedAt` e o autor. Pode manter conta/categoria inativas já usadas; novas escolhas precisam estar ativas. Cancelada ou realizada: `409`. |
| POST | `/api/families/{id}/planned-transactions/{plannedId}/cancel` | membro + CSRF | Cancela sem excluir (idempotente, `200`). Realizada: `409` (cancele antes o lançamento efetivo). |
| POST | `/api/families/{id}/planned-transactions/{plannedId}/realize` | membro + CSRF | Corpo `{ amount, transactionDate }` (só isso). Cria **um** lançamento efetivo vinculado com conta, categoria, tipo e descrição da previsão e o valor/data efetivos (valor pode diferir; data não futura em America/Sao_Paulo); autor = quem realizou; previsto preservado. `201` com o lançamento; repetição com o mesmo valor e data: `200` com o existente; outro valor/data ou previsão cancelada: `409`; conta/categoria inativas: `400` orientando a ajustar a previsão. Serializado com edição/cancelamento por bloqueio da previsão e garantido por índice único parcial ([detalhes](docs/previsoes.md)). |

`/health` traz apenas nome, status e duração dos checks — mensagens de erro e detalhes de conexão não são expostos:

```json
{ "status": "Healthy", "totalDurationMs": 12.3, "checks": [{ "name": "postgresql", "status": "Healthy", "durationMs": 11.8 }] }
```

## Testes e build

```bash
dotnet build Saldo360.slnx
dotnet test Saldo360.slnx
cd client && npm run lint && npm run build
```

Os testes não dependem do provedor: usam um provedor OIDC simulado (`FakeOidcProvider`, que assina ID tokens e atende PAR, token e userinfo). Os de autenticação usam EF Core InMemory; os de núcleo familiar usam **PostgreSQL real** via Testcontainers (requer Docker), com um banco novo e migrado por teste. Eles cobrem:

- `/api/me` (401/200 com `issuer` e `subject`) e o callback OIDC completo (o `iss` validado sobrevive às `ClaimActions`);
- `client_secret_post` no PAR e no token endpoint e o segredo lido de arquivo;
- identidades com o mesmo `subject` em emissores diferentes e a recusa de sessões antigas ou divergentes;
- logout com e sem CSRF e a invalidação no servidor (cookie antigo reenviado → 401);
- forwarded headers: proxy confiável gera callback HTTPS com o host público, e origem não confiável é ignorada;
- logs de falha sem `code`, tokens, segredos ou texto do provedor;
- validação TLS com CA própria (intermediária, nome do servidor, `serverAuth`);
- núcleo familiar: criação com um único `Owner`, validação do nome, CSRF, campos de identidade recusados, isolamento entre famílias (404), mesmo `subject` em emissores diferentes, logins repetidos sem duplicar usuário, criação concorrente do usuário, unicidades e check de papel no banco e atomicidade da criação.
- contas financeiras: dois membros veem e editam as mesmas contas, `openingBalance` como string JSON exata ou `null` em criação, listagem, consulta e edição (`"0.00"`, `"0.10"`, `"-1234.56"` e limites ±9.999.999.999.999,99), edição só do nome preservando saldo inicial e data, remoção devolvendo `null` e deixando o saldo realizado indisponível, não participante recebe 404 (inclusive pela rota da própria família), responsável de outra família recusado pela API (400) e pela FK composta no PostgreSQL, checks de tipo, escopo, moeda e nome, desativação que preserva o registro, reativação, CSRF e campos do servidor recusados; saldo inicial positivo, zero e negativo gravado exatamente e preservado após recarregar, configuração incompleta, mais de duas casas (sem arredondar), fora do limite e data com horário recusados pela API e pelos checks do banco, e conta antiga sem saldo após aplicar a migration;
- categorias: dois membros veem e editam as mesmas, receitas e despesas separadas, não participante recebe 404 (inclusive pela rota da própria família), nome duplicado (maiúsculas/minúsculas, espaços e inativas) com 409, mesmo nome permitido em outro tipo ou família, criações simultâneas gerando uma única categoria, corrida de INSERT e de renomeação resolvida pelo índice único (409), tipo imutável no PUT, CSRF, campos do servidor recusados, checks de nome e tipo no banco, colação ICU (`Saúde` = `SAÚDE` ≠ `Saude`) e migration preservando contas e saldos iniciais;
- lançamentos: receita e despesa com centavos e data exatos, `amount` como string JSON exata ("0.10", "1234.56", "9999999999999.99") em criação, consulta, listagem, edição e cancelamento, e edição preservando o valor exato no banco, valor zero/negativo/mais de 2 casas/fora do limite recusado sem arredondar, data futura em America/Sao_Paulo recusada (e a virada de dia no fuso), data anterior ao saldo inicial aceita, categoria de outro tipo recusada, conta/categoria de outra família recusadas com mensagem genérica, inativas recusadas na criação e mantidas na edição, histórico visível após desativar, cancelamento idempotente que preserva o registro e bloqueia edição (409), campos do servidor recusados, 404 para não participante, CSRF, filtros e paginação estável sem misturar famílias; no PostgreSQL, FKs compostas (conta e categoria da mesma família, categoria do mesmo tipo), checks de valor, descrição, tipo, moeda e auditoria, ausência de cascatas e migration preservando contas, saldos e categorias;
- saldo realizado: 1.000,00 + 500,00 − 200,00 = 1.300,00 em texto exato, movimentações do dia da referência incluídas e histórico anterior excluído, cancelados excluídos, consulta passada sem movimentações posteriores, sem saldo inicial e antes da referência indisponíveis (nulos, não zero), saldo inicial zero e negativo, 0,10 + 0,20 = 0,30, somas acima do limite de um lançamento sem perda, isolamento entre contas e famílias, edição (valor, tipo, data, conta), cancelamento e mudança do saldo inicial refletidos, conta inativa, data padrão em America/Sao_Paulo, data futura ou fora do formato `yyyy-MM-dd` recusada (inclusive nos filtros de lançamentos), e no PostgreSQL uma única consulta agregada com `SUM`/`GROUP BY` sem carregar lançamentos;
- transferências: PJ 2.000,00 e PF 100,00 com transferência de 500,00 resultam em 1.500,00 e 600,00 sem alterar receitas/despesas e sem gerar lançamentos, cancelamento idempotente que restaura os dois saldos e preserva o registro, datas de referência diferentes sem duplicar o que já está no saldo inicial, consulta passada, origem negativa permitida, PF↔PJ e mesmo escopo, contas iguais ou de outra família recusadas com mensagem genérica, inativas recusadas na criação com histórico e saldos preservados após desativar, valor/data inválidos sem arredondar, campos do servidor e categoria recusados, sem edição (405), 404 para não participante, CSRF, filtros (conta como origem ou destino) e paginação estável; no PostgreSQL, FKs compostas de origem e destino na mesma família, checks (contas distintas, valor, descrição, moeda, cancelamento), ausência de cascatas, migration preservando dados e agregação em uma consulta por tabela;
- previsões: receita futura e despesa vencida com centavos e data exatos, vencida continuando pendente, criar/editar/cancelar sem alterar o saldo realizado e sem gerar lançamentos, `amount` como string exata em todas as respostas, valor inválido sem arredondar, data com horário recusada, datas passadas e futuras aceitas, categoria de outro tipo e referências de outra família recusadas (mensagem genérica), inativas recusadas na criação e mantidas na edição, histórico após desativar, cancelamento idempotente que bloqueia edição (409), campos do servidor recusados, 404 para não participante, CSRF, filtros sobre a data prevista e paginação crescente estável; no PostgreSQL, FKs compostas (mesma família e tipo da categoria), checks de valor, descrição, tipo, moeda e auditoria, ausência de cascatas e migration preservando movimentações;
- realização de previsões: saldo 1.000,00 com despesa prevista de 200,00 inalterado, realizar por 210,00 dá 790,00 preservando a previsão de 200,00, lançamento com dados da previsão e autor de quem realizou, repetição idempotente (mesmo id, auditoria intacta) e 409 com outro valor/data, 12 realizações simultâneas gerando um único lançamento, corrida realizar × cancelar previsão e realizar × editar previsão terminando consistente, cancelar o efetivo restaura 1.000,00 e reabre a previsão, nova realização preservando o histórico, cancelamento repetido do antigo sem afetar o novo, previsão realizada não editável/cancelável e lançamento vinculado não editável (409), cancelada não realizável, valor/data inválidos, data anterior à referência fora do saldo, conta/categoria inativas com orientação, isolamento, CSRF, campos extras recusados e filtros `includeRealized`/`includeCancelled`; no PostgreSQL, índice único parcial (um ativo, cancelados liberam nova realização, reativar antigo recusado), FK composta de mesma família, ausência de cascata e migration mantendo lançamentos sem vínculo;
- saldo projetado: 1.000,00 + 500,00 − 200,00 = 1.300,00, vencida pendente de 100,00 dá 1.200,00, previsão após a data-alvo fora, realizar a despesa por 210,00 dá 1.190,00 e cancelar o efetivo volta a 1.200,00, hoje no período e vencidas separadas por tipo, pendente anterior ao saldo inicial contando, cancelada fora, conta/categoria inativas sem esconder compromissos, realizado igual ao endpoint de saldo (com lançamentos e transferências), centavos e somas acima do limite individual, isolamento entre contas e famílias, referência futura e ausência de saldo inicial com todos os valores nulos, `through` obrigatório/ISO/não passado, e projeção sempre 1.200,00 ou 1.190,00 durante realizações e cancelamentos concorrentes; no PostgreSQL, somas das pendentes (vencidas, período, canceladas, realizadas e reabertas), leitura REPEATABLE READ somente leitura com agregação no banco e snapshot único mesmo com uma realização confirmada no meio da leitura;
- convites: só Owner cria ou revoga (403 para Member, 404 para não participante), CSRF, token só como hash, estados expirado, revogado e usado com a mesma resposta genérica, aceitações simultâneas gerando um único vínculo, corrida entre revogação e aceite, membro existente sem consumir o convite, falha no vínculo sem consumir o convite e família com mais de duas pessoas.
- orçamentos mensais: uma competência por família inclusive após arquivamento, metas de despesa únicas por categoria, valores zero/centavos/limite como texto decimal exato, categoria inativa preservada, categorias de receita e de outra família recusadas, isolamento e autorização, CSRF, campos extras, arquivamento sem exclusão, histórico sem cascatas, total apenas das metas ativas, nenhuma alteração em saldo/projeção e migration preservando os dados financeiros existentes ([detalhes](docs/orcamentos.md)).
- execução orçamentária: metas vazias e sem movimentos, realizado, comprometido e combinação exata, limite ultrapassado, disponível negativo, meta zero, realização pelo valor previsto ou divergente sem duplicidade, cancelamento e reabertura, competências distintas, cancelados, transferências e categorias sem meta excluídos, categoria inativa, recursos arquivados, isolamento, snapshot concorrente `REPEATABLE READ` somente leitura e agregações por categoria sem N+1.

O frontend tem testes com Vitest (`npm run test:client`, incluídos em `npm test`): [Agenda Financeira](docs/agenda-financeira.md) (mês atual de São Paulo, navegação mensal e entre anos, período correto, seleção inclusive de dias vazios, estados realizado/pendente/vencido, filtros por conta e tipo, resumos vindos da API, mês vazio, falha, valores decimais exatos, troca de família e descarte de respostas obsoletas); saldo projetado (data-alvo inicial no fim do mês, nova consulta ao trocar a data e quando as finanças mudam, projetado, previstas e vencidas em linhas próprias, valores exatos sem cálculo, negativo destacado, indisponível sem zero; previsões avisando a projeção ao criar e cancelar), realização ("Realizar" só em pendentes, formulário com valor previsto e hoje, confirmação, valor/data inválidos, orientação da API, realizadas sob pedido com previsto e efetivo separados e acesso ao lançamento, "Vencida" só para pendentes, lançamento vinculado sem edição e com aviso de reabertura ao cancelar, recarga cruzada entre seções), previsões ("Data prevista", "Vencida" só para pendente anterior a hoje em America/Sao_Paulo, canceladas sob demanda, filtros e paginação, cancelamento com confirmação, receita futura e despesa vencida com valor em texto exato, categorias pelo tipo, valor inválido, data obrigatória, orientação sem opções ativas, edição mantendo vínculos inativos e valor máximo, 409; sem chamadas a saldo ou lançamentos), saldo inicial como texto exato (positivo, negativo, máximo e centavos exibidos e reenviados sem `number` ao editar só o nome; remoção enviando `null`), valor dos lançamentos como texto decimal exato (centavos e valor máximo exibidos, edição preenchida e reenviada sem `number`), transferências ("De [conta] para [conta]" com escopo, valor exato sem sinal, canceladas sob demanda, filtros e paginação, cancelamento com confirmação, criação PJ → PF com valor em texto exato e sem categoria, só contas ativas, origem = destino, valor zero/negativo/3 casas e data futura recusados, aviso para atualizar os saldos), saldo realizado (separado do saldo inicial, valor exato formatado a partir do texto decimal sem `number`, negativo destacado e não como crédito, indisponível com orientação e sem zero, nova consulta após mudar lançamentos ou saldo inicial), lançamentos (lista com sinal e referências inativas, cancelados sob demanda, filtros e paginação, cancelamento com confirmação, valor enviado como texto decimal exato, categorias filtradas pelo tipo, valor zero/negativo/3 casas e data futura recusados, orientação sem conta ou categoria ativa e edição mantendo vínculos inativos), categorias (receitas e despesas separadas, nenhuma criada automaticamente, inativas sob demanda, criação com tipo da seção, duplicidade 409 exibida, renomear sem enviar o tipo e confirmação ao desativar e reativar), contas (lista, "Conjunta", inativas sob demanda, confirmação ao desativar e corpo enviado; saldo inicial "não informado" vs. valor e data de referência, negativo e zero, formato brasileiro sem arredondamento e erros de configuração incompleta), captura do token do fragmento, continuidade após o login (o token sobrevive no `sessionStorage` e o retorno é para `/convite`), aceite só com clique explícito e token apenas no corpo, e limpeza após sucesso ou erro definitivo.

A cobertura da Agenda também inclui o painel acessível de detalhes, ações por origem e situação, realização com valor
efetivo diferente, edição e cancelamento, reabertura de previsão vinculada e atualização coordenada de resumo e saldo.

**Orçamentos**: a seção React dentro de cada família permite navegar por competências, criar o orçamento, cadastrar/editar/arquivar metas e acompanhar a execução retornada pela API; os valores permanecem strings decimais e a tela não recalcula indicadores. Consulte [docs/orcamentos.md](docs/orcamentos.md).

**Login real via curl** (com API e frontend rodando). O script pede usuário e senha sem eco:

```bash
bash scripts/smoke-login.sh                    # Authelia do servidor
bash scripts/smoke-login.sh --authelia-local   # Authelia local
```

O smoke test não substitui a validação no navegador: abra <http://localhost:5173>, clique em **Entrar**, autentique no Authelia, confira o nome exibido, recarregue a página e clique em **Sair**.

Cartões de crédito estão disponíveis na navegação de cada família. A interface consome os endpoints existentes para cadastro/arquivamento, compras, faturas, pagamentos, limite e projeção de seis meses; valores monetários são exibidos a partir de strings decimais retornadas pela API, sem cálculos financeiros no React. Consulte [docs/cartoes-credito.md](docs/cartoes-credito.md).

Compras no cartão e parcelas são cadastradas separadamente pela API `/api/families/{familyId}/credit-cards/{cardId}/purchases`; elas representam obrigações de crédito e não movimentam o saldo bancário.
Os alertas de orçamento da execução são calculados no backend a partir do consumo projetado bruto; o resumo informa o pior estado das metas e as contagens de aproximação, limite atingido e excedência.
