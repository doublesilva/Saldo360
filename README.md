# Saldo360

Aplicação de orçamento familiar. Esta é a estrutura inicial: API ASP.NET Core 10 com EF Core + PostgreSQL e frontend React + TypeScript (Vite).

## Estrutura

```
Saldo360.slnx
src/
  Saldo360.Domain/          Entidades e regras de negócio (sem dependências externas)
  Saldo360.Application/     Casos de uso; depende apenas do Domain
  Saldo360.Infrastructure/  EF Core (Npgsql), DbContext, health check do banco
  Saldo360.Api/             Host ASP.NET Core; expõe GET /health
tests/
  Saldo360.Tests/           Testes de integração da API (xUnit + WebApplicationFactory)
client/                     React + TypeScript (Vite)
docker-compose.yml          PostgreSQL para desenvolvimento
```

Dependências: `Api → Application, Infrastructure`; `Infrastructure → Application, Domain`; `Application → Domain`.

## Pré-requisitos

- .NET SDK 10
- Node.js 20+ e npm
- Docker (para o PostgreSQL local)

## Configuração de desenvolvimento

Nenhuma credencial fica no repositório. Os valores reais ficam em arquivos ignorados pelo git (`.env`) ou no user-secrets do .NET.

1. **Banco de dados** — copie o exemplo, troque a senha e suba o PostgreSQL:

   ```bash
   cp .env.example .env        # edite POSTGRES_PASSWORD
   docker compose up -d --wait
   ```

2. **Connection string da API** — guarde no user-secrets (fica no seu perfil de usuário, fora do projeto). Use os mesmos valores do `.env`:

   ```bash
   dotnet user-secrets set "ConnectionStrings:Saldo360" \
     "Host=localhost;Port=5432;Database=saldo360;Username=saldo360;Password=<sua-senha>" \
     --project src/Saldo360.Api
   ```

   Em outros ambientes, use a variável de ambiente `ConnectionStrings__Saldo360`. As chaves esperadas estão documentadas em [src/Saldo360.Api/appsettings.Example.json](src/Saldo360.Api/appsettings.Example.json) (arquivo apenas de referência; não é carregado nem publicado).

3. **Frontend** — instale as dependências:

   ```bash
   cd client && npm install
   ```

   O Vite encaminha `/health` e `/api` para `http://localhost:5080`. Para outro endereço, copie `client/.env.example` para `client/.env.local` e ajuste `API_PROXY_TARGET`.

## Executando

```bash
dotnet run --project src/Saldo360.Api   # API em http://localhost:5080
cd client && npm run dev                # Frontend em http://localhost:5173
```

A página inicial consulta `GET /health` e mostra se a API e o banco estão acessíveis.

### GET /health

```json
{
  "status": "Healthy",
  "totalDurationMs": 12.3,
  "checks": [{ "name": "postgresql", "status": "Healthy", "durationMs": 11.8 }]
}
```

Retorna `200` quando saudável e `503` quando algum check falha. A resposta traz apenas nome, status e duração: mensagens de erro e detalhes de conexão não são expostos.

## Testes e build

```bash
dotnet build Saldo360.slnx
dotnet test Saldo360.slnx
cd client && npm run lint && npm run build
```

Os testes não precisam de PostgreSQL: o cenário saudável usa EF Core InMemory e o cenário de falha aponta para uma porta fechada, verificando que a senha não aparece na resposta nem nos logs.
