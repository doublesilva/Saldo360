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
docs/                       Autenticação (provedor, CA, proxy, logs) e cadastro do client no Authelia
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
| POST | `/api/families/{id}/accounts` | membro + CSRF | Corpo `{ name, institution?, type, financialScope, responsibleMemberId? }`. `type`: `Checking`, `Savings` ou `Cash`; `financialScope`: `PF` ou `PJ`. Responsável nulo significa conta conjunta. Moeda BRL. Criador e família vêm do servidor. `201`; `400` com `errors.<campo>` (inclusive "Responsável inválido." para vínculo de outra família) ou com campos extras. |
| GET | `/api/families/{id}/accounts[?includeInactive=true]` | membro | Contas da família (só as ativas, por padrão): `[{ id, name, institution, type, financialScope, responsible: { memberId, displayName } \| null, isActive, currency, createdAt }]`. |
| GET | `/api/families/{id}/accounts/{accountId}` | membro | Uma conta; `404` se não existir nessa família ou se o usuário não for membro. |
| PUT | `/api/families/{id}/accounts/{accountId}` | membro + CSRF | Mesmos campos do POST mais `isActive` (obrigatório): edita e ativa/desativa. Não há exclusão física. |

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
- contas financeiras: dois membros veem e editam as mesmas contas, não participante recebe 404 (inclusive pela rota da própria família), responsável de outra família recusado pela API (400) e pela FK composta no PostgreSQL, checks de tipo, escopo, moeda e nome, desativação que preserva o registro, reativação, CSRF e campos do servidor recusados;
- convites: só Owner cria ou revoga (403 para Member, 404 para não participante), CSRF, token só como hash, estados expirado, revogado e usado com a mesma resposta genérica, aceitações simultâneas gerando um único vínculo, corrida entre revogação e aceite, membro existente sem consumir o convite, falha no vínculo sem consumir o convite e família com mais de duas pessoas.

O frontend tem testes com Vitest (`npm run test:client`, incluídos em `npm test`): contas (lista sem saldo, "Conjunta", inativas sob demanda, confirmação ao desativar e corpo enviado), captura do token do fragmento, continuidade após o login (o token sobrevive no `sessionStorage` e o retorno é para `/convite`), aceite só com clique explícito e token apenas no corpo, e limpeza após sucesso ou erro definitivo.

**Login real via curl** (com API e frontend rodando). O script pede usuário e senha sem eco:

```bash
bash scripts/smoke-login.sh                    # Authelia do servidor
bash scripts/smoke-login.sh --authelia-local   # Authelia local
```

O smoke test não substitui a validação no navegador: abra <http://localhost:5173>, clique em **Entrar**, autentique no Authelia, confira o nome exibido, recarregue a página e clique em **Sair**.
