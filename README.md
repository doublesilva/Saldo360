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
dev/authelia/               Configuração do Authelia de desenvolvimento (provedor OIDC)
scripts/                    Setup do provedor de desenvolvimento e smoke test de login
docs/autenticacao.md        Configuração de autenticação, URLs de callback, produção
docker-compose.yml          PostgreSQL e Authelia para desenvolvimento
```

Dependências: `Api → Application, Infrastructure`; `Infrastructure → Application, Domain`; `Application → Domain`.

## Pré-requisitos

- .NET SDK 10
- Node.js 20+ e npm
- Docker
- PowerShell 7.4+ (para `scripts/setup-dev-auth.ps1`)

## Configuração de desenvolvimento

Nenhuma credencial fica no repositório. Os valores reais ficam em arquivos ignorados pelo git (`.env`, `dev/authelia/secrets/`, `dev/authelia/data/`) ou no user-secrets do .NET.

1. **Banco de dados** — copie o exemplo e troque a senha:

   ```bash
   cp .env.example .env        # edite POSTGRES_PASSWORD
   ```

2. **Provedor OIDC (Authelia)** — gere certificados, segredos e um usuário de teste. O script também grava o client secret no user-secrets da API:

   ```powershell
   ./scripts/setup-dev-auth.ps1
   ```

   A senha do usuário `familia` fica em `dev/authelia/secrets/dev_user_password.txt`.

3. **Suba os serviços** (PostgreSQL e Authelia):

   ```bash
   docker compose up -d --wait
   ```

4. **Connection string da API** — guarde no user-secrets (fica no seu perfil de usuário, fora do projeto). Use os mesmos valores do `.env`:

   ```bash
   dotnet user-secrets set "ConnectionStrings:Saldo360" \
     "Host=localhost;Port=5432;Database=saldo360;Username=saldo360;Password=<sua-senha>" \
     --project src/Saldo360.Api
   ```

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

7. **Certificado do provedor no navegador** — o Authelia usa uma CA local (`dev/authelia/secrets/tls/ca.public.crt`). Aceite o aviso do navegador em `https://auth.saldo360.localhost:9091` ou confie na CA apenas para o seu usuário:

   ```powershell
   Import-Certificate -FilePath dev/authelia/secrets/tls/ca.public.crt -CertStoreLocation Cert:\CurrentUser\Root
   ```

   A API não depende disso: ela confia somente nessa CA, configurada em `Authentication:Oidc:BackchannelCaCertificatePath`.

Em outros ambientes, use variáveis de ambiente (`ConnectionStrings__Saldo360`, `Authentication__Oidc__ClientSecret`, …). As chaves esperadas estão em [src/Saldo360.Api/appsettings.Example.json](src/Saldo360.Api/appsettings.Example.json) (só referência; não é carregado nem publicado). Detalhes em [docs/autenticacao.md](docs/autenticacao.md).

## Executando

O `package.json` da raiz reúne os atalhos. Depois de criar o `.env` e a connection string (passos 1 e 4):

```bash
npm install          # instala o orquestrador (concurrently)
npm run setup        # segredos do Authelia, dotnet tools e dependências do frontend (uma vez)
npm run dev          # sobe PostgreSQL + Authelia no Docker, aplica migrations e roda API + frontend
```

| Script | O que faz |
| --- | --- |
| `npm run dev` | `docker:up` → `db:migrate` → `start` |
| `npm start` | API (`:5080`) e frontend (`:5173`) juntos, sem mexer no Docker |
| `npm run docker:up` / `docker:down` | Sobe/para PostgreSQL e Authelia (os dados ficam nos volumes) |
| `npm run docker:logs` / `docker:ps` | Logs e status dos containers |
| `npm run db:migrate` | Aplica as migrations no banco de desenvolvimento |
| `npm run api` / `npm run web` | Só a API / só o frontend |
| `npm run check` | Build, testes e lint |
| `npm run smoke:login` | Login real contra o Authelia (com `npm run dev` rodando) |

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

Os testes não precisam de PostgreSQL nem do provedor: usam EF Core InMemory e um provedor OIDC simulado (`FakeOidcProvider`, que assina ID tokens e atende token/userinfo). Eles cobrem `/api/me` (401/200 com `issuer` e `subject`), o callback OIDC completo (o `iss` validado sobrevive às `ClaimActions`), identidades com o mesmo `subject` em emissores diferentes, recusa de sessões antigas sem `issuer` ou com identidade divergente, logout com e sem CSRF, invalidação no servidor (cookie antigo reenviado → 401), atributos do cookie e o redirecionamento de login (code + PKCE).

**Login real** contra o Authelia (com serviços, API e frontend rodando):

```bash
bash scripts/smoke-login.sh
```
