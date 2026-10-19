# Deploy de produção

O Saldo360 é publicado no `home-server` em containers Docker. O Caddy é a única entrada HTTPS e encaminha
`https://saldo360.home.doublesilva.com` para `127.0.0.1:5600`.

A API e o frontend permanecem na mesma origem pública. O PostgreSQL e a API não publicam portas no host.

## Arquitetura

```text
navegador
   |
   | HTTPS
   v
Caddy :443
   |
   | http://127.0.0.1:5600
   v
saldo360-web (nginx)
   |                  \
   | arquivos React    \ /api, /auth, /signin-oidc, /health
   |                    v
   |                 saldo360-api
   |                    |
   |                    v
   +--------------> PostgreSQL 17
```

A API usa `https://auth.home.doublesilva.com` como issuer OIDC e monta o callback público a partir dos
forwarded headers do proxy. A CA interna do Caddy é montada somente para leitura no container.

## Bootstrap único do servidor

Estes passos são executados uma única vez como administrador do servidor. O pipeline não tenta se conceder
permissões nem instalar o próprio comando privilegiado.

### 1. Diretórios

```bash
sudo install -d -o deploy -g deploy -m 0750 /srv/saldo360-deploy
sudo install -d -o deploy -g deploy -m 0750 /srv/saldo360-deploy/release
sudo install -d -o root   -g root   -m 0700 /etc/saldo360/secrets
```

Copie `.env.production.example` para `/srv/saldo360-deploy/.env`, troque a senha do PostgreSQL e mantenha:

```dotenv
POSTGRES_DB=saldo360
POSTGRES_USER=saldo360
POSTGRES_PASSWORD=<senha-forte>
SALDO360_WEB_PORT=5600
SALDO360_PUBLIC_HOST=saldo360.home.doublesilva.com
SALDO360_IMAGE_TAG=0000000000000000000000000000000000000000
```

O arquivo deve pertencer ao usuário de deploy e não ser legível por outros usuários:

```bash
sudo chown deploy:deploy /srv/saldo360-deploy/.env
sudo chmod 0600 /srv/saldo360-deploy/.env
```

### 2. Segredo OIDC

O client `saldo360` já existe no Authelia. Para disponibilizar o segredo ao container, grave o texto puro em:

```text
/etc/saldo360/secrets/oidc-client-secret
```

Permissões:

```bash
sudo chown root:root /etc/saldo360/secrets/oidc-client-secret
sudo chmod 0600 /etc/saldo360/secrets/oidc-client-secret
```

Se for necessário gerar um novo segredo, execute localmente:

```powershell
pwsh scripts/new-oidc-client-secret.ps1 -OutFile <arquivo-temporario>
```

O script também atualiza o user-secrets de desenvolvimento e mostra somente o digest que deve substituir o
`client_secret` no Authelia. Depois copie o arquivo temporário para o caminho acima e apague a cópia local.

### 3. Redirect URI do Authelia

No client `saldo360`, mantenha as duas URIs:

```yaml
redirect_uris:
  - http://localhost:5173/signin-oidc
  - https://saldo360.home.doublesilva.com/signin-oidc
```

Valide a configuração candidata antes de substituir a ativa e reinicie o Authelia apenas após a validação.

### 4. Caddy e DNS

No Caddy:

```caddyfile
saldo360.home.doublesilva.com {
    reverse_proxy 127.0.0.1:5600
}
```

O DNS interno deve resolver `saldo360.home.doublesilva.com` para o `home-server`.

### 5. Comando privilegiado de deploy

Copie `deploy/server/deploy-saldo360` para um caminho controlado por root:

```bash
sudo install -o root -g root -m 0755   /caminho/do/repo/deploy/server/deploy-saldo360   /usr/local/sbin/deploy-saldo360
```

O usuário `deploy` precisa poder executar somente esse comando via sudo. Exemplo de entrada dedicada em
`/etc/sudoers.d/deploy-saldo360`:

```text
deploy ALL=(root) NOPASSWD: /usr/local/sbin/deploy-saldo360 *
```

O script aceita exclusivamente um SHA Git de 40 caracteres.

## GitHub Actions

### Secrets

O repositório precisa dos mesmos valores de conexão usados pelos pipelines existentes do servidor:

- `TS_OAUTH_CLIENT_ID`
- `TS_AUDIENCE`
- `DEPLOY_SSH_KEY`

### Repository variables

Para o servidor atual:

```text
SALDO360_DEPLOY_HOST=100.115.190.21
SALDO360_DEPLOY_USER=deploy
SALDO360_SSH_HOST_FINGERPRINT=SHA256:NWtsdN5troZd1Q9bs8ztzp7bBt6NV6YsGLLKvPbwd5s
SALDO360_TAILSCALE_TAG=tag:homeautomation-ci
```

## Pipeline

`.github/workflows/ci.yml` roda em PR, push em `main` e manualmente:

1. instala dependências;
2. executa `npm run check`;
3. valida o compose de produção;
4. constrói as imagens de produção da API e do frontend.

`.github/workflows/deploy.yml` é manual e aceita somente `main`:

1. entra na Tailnet;
2. valida a chave SSH do servidor por fingerprint;
3. envia somente os arquivos versionados do commit;
4. prepara `/srv/saldo360-deploy/release`;
5. chama `sudo /usr/local/sbin/deploy-saldo360 <sha>`.

No servidor, o comando:

1. valida e constrói as imagens;
2. cria backup `pg_dump` antes de migrations quando já existir banco;
3. mantém os 30 backups pré-deploy mais recentes;
4. aplica migrations pelo EF migration bundle;
5. pré-valida containers candidatos da API e do web;
6. troca para as novas imagens e exige healthcheck saudável;
7. grava o SHA publicado em `/srv/saldo360-deploy/deployed-sha`.

O compose de produção exige `SALDO360_IMAGE_TAG`: não existe fallback para `local`. O comando de deploy sempre
exporta o SHA Git completo antes de validar, construir, migrar ou publicar as imagens.

Durante a pré-validação, a API candidata entra na rede de backend apenas para alcançar o PostgreSQL. Ela não recebe
o alias DNS `api`; o script verifica essa condição e aborta antes do cutover se o alias aparecer. Portanto, o web
ativo continua resolvendo exclusivamente a API primária até `compose up -d api web` concluir a troca.

## Liberação controlada de contas de serviço

As chaves abaixo ficam `false` por padrão no arquivo de ambiente. Para uma janela supervisionada, habilite apenas
as necessárias e preserve `FinancialEmailImport__Enabled=false`, mantendo o scheduler desligado:

```dotenv
FINANCIAL_DOCUMENT_CLASSIFICATION_ENABLED=true
FINANCIAL_DOCUMENT_CLASSIFICATION_AGENT_ENABLED=false
SERVICE_BILL_EXTRACTION_ENABLED=true
SERVICE_BILL_EXTRACTION_AGENT_ENABLED=false
SERVICE_BILL_RECONCILIATION_ENABLED=true
SERVICE_BILL_CONFIRMATION_ENABLED=true
SERVICE_BILL_BINDING_ENABLED=true
```

Classificação, extração e matching continuam sem efeito financeiro. Confirmação e binding exigem autenticação,
CSRF e ação explícita. Concluir a validação manual não autoriza ligar o coletor periódico.

## Primeiro deploy

Depois do bootstrap e do merge em `main`, execute manualmente o workflow **Deploy Saldo360**.

Validações esperadas:

```bash
curl -fsS https://saldo360.home.doublesilva.com/health
```

Depois faça o login pelo navegador. O callback deve voltar para:

```text
https://saldo360.home.doublesilva.com/signin-oidc
```

## Recuperação

Os dados ficam no volume Docker `saldo360-pgdata`; substituir o conteúdo de `release` não remove o volume.

Antes de cada deploy que já possui banco, é gerado um dump em `/srv/saldo360-deploy/backups`. Uma migration pode
não ser reversível para uma versão anterior da aplicação, por isso rollback de código não é tratado como
automaticamente seguro. Em falha após migration, diagnostique o schema e restaure o dump somente se realmente
necessário.
