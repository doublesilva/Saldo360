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
sudo install -d -o root   -g root   -m 0755 /etc/saldo360
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

### 5. Wrapper privilegiado estável

O arquivo instalado em `/usr/local/sbin` é o wrapper pequeno e estável. A lógica evolutiva continua em
`deploy/server/deploy-saldo360`, dentro do artefato de cada release. Não instale esse segundo arquivo como comando
privilegiado.

Gere a chave RSA 3072 dedicada numa estação administrativa com PowerShell 7 e `gh` autenticado:

```powershell
pwsh -NoProfile -File scripts/new-deploy-signing-key.ps1 `
  -PublicKeyPath dev/certs/deploy-release-public.pem
```

O script envia a chave privada diretamente da memória para `DEPLOY_RELEASE_SIGNING_KEY` no environment
`saldo360-production`, sem arquivo privado ou saída sensível. Somente a chave pública é salva, em diretório
ignorado pelo Git. O script recusa sobrescrever material existente; não utilizá-lo para rotação implícita.
Instale somente a chave pública e o wrapper no servidor:

```bash
sudo install -o root -g root -m 0644 \
  deploy-release-public.pem \
  /etc/saldo360/deploy-release-public.pem
sudo install -o root -g root -m 0755 \
  /caminho/do/repo/deploy/server/deploy-saldo360-wrapper \
  /usr/local/sbin/deploy-saldo360
```

Alternativamente, use `deploy/server/bootstrap-signed-deploy` com os hashes revisados conforme o
[relatório T15.6.8](T15.6.8-bootstrap-deploy-assinado.md). Ele preserva backup administrativo e valida sudoers.
A chave privada nunca deve existir no servidor. O usuário `deploy` não precisa pertencer ao grupo `docker`: toda operação
Docker continua ocorrendo depois da fronteira `sudo`.

O wrapper aceita somente `--protocol-version` ou um SHA minúsculo de exatamente 40 caracteres. Em versões atuais
do sudo, limite também os argumentos em `/etc/sudoers.d/deploy-saldo360`:

```text
deploy ALL=(root) NOPASSWD: /usr/local/sbin/deploy-saldo360 --protocol-version
deploy ALL=(root) NOPASSWD: /usr/local/sbin/deploy-saldo360 ^[0-9a-f]{40}$
```

Valide com `sudo visudo -cf /etc/sudoers.d/deploy-saldo360`. Se a versão local do sudo não aceitar expressões
regulares para argumentos, mantenha temporariamente `... deploy-saldo360 *`; a validação estrita do wrapper ainda
bloqueia argumentos adicionais, mas atualizar o sudo é a opção recomendada.

O wrapper verifica o protocolo, rejeita links simbólicos, valida a propriedade e as permissões da chave pública,
confere a assinatura do manifesto e exige que o manifesto corresponda ao SHA solicitado, ao hash do arquivo da
release e ao hash real do script. Antes de validar, copia o pacote completo para um diretório temporário root-only;
depois da cópia, não volta a ler o staging gravável por `deploy`. O script autenticado usa exclusivamente a release
extraída nessa área e recebe ambiente limpo. Isso elimina a janela entre verificação e uso também para Dockerfiles,
compose e código-fonte: alterar arquivos no staging não permite escolher outro executável, trocar conteúdo depois
da conferência nem produzir uma release autenticada.

Cheque o bootstrap sem publicar nada:

```bash
sudo /usr/local/sbin/deploy-saldo360 --protocol-version
# saída esperada: 1
```

## GitHub Actions

### Secrets

O repositório precisa dos mesmos valores de conexão usados pelos pipelines existentes do servidor:

- `TS_OAUTH_CLIENT_ID`
- `TS_AUDIENCE`
- `DEPLOY_SSH_KEY`
- `DEPLOY_RELEASE_SIGNING_KEY` (chave privada PEM; protegida pelo environment `saldo360-production`)

O secret de assinatura e `DEPLOY_SSH_KEY` são raízes de confiança equivalentes: quem puder alterar o workflow e
executá-lo com acesso ao environment poderá publicar uma release válida. Na verificação de 2026-10-19, o environment
`saldo360-production` não tinha reviewers nem política de branch, e a API informou que branch protection para o
repositório privado exige mudança de plano. Até que esses controles estejam disponíveis, restrinja rigorosamente
quem possui escrita/administração no repositório e revise manualmente cada execução. O workflow de deploy também
valida o `ci-success` do SHA exato antes de qualquer acesso ao servidor; isso reduz o risco de publicar um commit
não validado, mas não substitui proteção de branch ou aprovação independente do environment. Quando possível,
configure reviewers obrigatórios no environment e limite deployments à `main`. A checagem
`GITHUB_REF=refs/heads/main` do workflow é defesa adicional, não substitui esses controles.

### Repository variables

Para o servidor atual:

```text
SALDO360_DEPLOY_HOST=100.115.190.21
SALDO360_DEPLOY_USER=deploy
SALDO360_SSH_HOST_FINGERPRINT=SHA256:NWtsdN5troZd1Q9bs8ztzp7bBt6NV6YsGLLKvPbwd5s
SALDO360_TAILSCALE_TAG=tag:homeautomation-ci
```

## Pipeline

`.github/workflows/ci.yml` roda em PR, push em `main` e manualmente. O job `changes` classifica o impacto em
`backend`, `frontend` e `infra`; somente os jobs relevantes são executados. O check estável
`ci-success` passa apenas quando `changes` e todos os jobs executados terminam com sucesso (jobs não impactados
podem ficar `skipped`). `workflow_dispatch` força a validação completa.

`.github/workflows/deploy.yml` é manual e aceita somente `main`. Antes de abrir Tailscale ou acessar o servidor,
o workflow consulta os check-runs do SHA exato a publicar e exige `ci-success` com
`status=completed` e `conclusion=success`. Check ausente, pendente, cancelado ou vermelho bloqueia o deploy no
próprio runner.

Depois desse gate:

1. faz checkout explícito do SHA aprovado;
2. entra na Tailnet;
3. valida a chave SSH do servidor por fingerprint;
4. confirma o bootstrap usando `sudo -n /usr/local/sbin/deploy-saldo360 --protocol-version`, sem permitir prompt interativo;
5. cria um manifesto com protocolo, SHA, hash do pacote e hash do script versionado, e o assina;
6. envia os arquivos versionados mais o manifesto e sua assinatura;
7. coloca pacote, manifesto e assinatura no staging `/srv/saldo360-deploy`;
8. chama `sudo -n /usr/local/sbin/deploy-saldo360 <sha>`.

No servidor, o wrapper autentica a release e chama o script versionado. Esse script:

1. valida e constrói as imagens;
2. cria backup `pg_dump` antes de migrations quando já existir banco;
3. mantém os 30 backups pré-deploy mais recentes;
4. aplica migrations pelo EF migration bundle;
5. pré-valida containers candidatos da API e do web;
6. troca para as novas imagens e exige healthcheck saudável;
7. grava o SHA publicado em `/srv/saldo360-deploy/deployed-sha`.

O compose de produção exige `SALDO360_IMAGE_TAG`: não existe fallback para `local`. O comando de deploy sempre
exporta o SHA Git completo antes de validar, construir, migrar ou publicar as imagens.

Atualizações normais em `deploy/server/deploy-saldo360` entram na release assinada e não exigem reinstalar o
wrapper. Reinstale o wrapper apenas quando seu protocolo ou sua própria fronteira de segurança mudar. Rotação da
chave exige trocar o secret do GitHub e a chave pública root-owned de maneira coordenada; releases assinadas pela
chave antiga deixam de ser aceitas depois da troca.

Falha de protocolo, assinatura, SHA, hash, caminho ou permissão ocorre antes de Docker, backup ou migration. Corrija
o bootstrap ou gere novamente a release e repita o workflow; nunca contorne a falha copiando o script evolutivo para
`/usr/local/sbin`. Se a chave privada for perdida ou exposta, gere um novo par, substitua primeiro o secret e a chave
pública numa janela coordenada e invalide o material antigo.

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

### Agente de faturas OpenAI: configuração não é ativação

O serviço `api` monta somente `/etc/jarvis/secrets/openai-api-key` em
`/run/secrets/openai-api-key:ro`. Não copiar a chave para este projeto, `.env`, imagem ou logs.
O segredo compartilhado permanece sob administração do Jarvis, sem `chmod`/`chown` pelo deploy.
O provisionamento informado é `root:1654`, modo `0440`, com API `1654:1654` e grupo adicional `1654`.

As configurações iniciais são `CREDIT_CARD_STATEMENT_AGENT_ENABLED=false`,
`CREDIT_CARD_STATEMENT_AGENT_MODE=Disabled` e modelo `gpt-4o-mini`.
O provider é `OpenAI` e `ApiKeyFile` é fixo no caminho montado. O provider existente falha se esse
arquivo faltar ou estiver vazio; não recorre à credencial inline quando `ApiKeyFile` está configurado.

O script de release verifica arquivo regular/não vazio **antes do build**. Depois do build e antes
do backup/migrations/candidatos/cutover, executa um probe de shell com a imagem/configuração da API,
validando UID/GID e leitura. O entrypoint da API não é iniciado nesse probe e não há chamada OpenAI.
Falha encerra o deploy, inclusive com o agente desabilitado: a montagem é obrigatória e não deve criar
diretório em lugar de segredo ausente. O deploy não provisiona, copia ou repara o segredo.
O probe é um container efêmero e não modifica a API ativa nem registra conteúdo do arquivo.

Somente em janela futura explicitamente autorizada, alterar **apenas configuração não secreta**
em `/srv/saldo360-deploy/.env`:

```dotenv
CREDIT_CARD_STATEMENT_AGENT_ENABLED=true
CREDIT_CARD_STATEMENT_AGENT_MODE=AgentPrimary
CREDIT_CARD_STATEMENT_AGENT_MODEL=gpt-4o-mini
```

Aplicar pela arquitetura existente de release assinada/deploy autorizado, com CI do SHA aprovado,
preflight, backup, candidatos e readiness; editar `.env` sozinho não muda containers já iniciados.
Confirmar flags efetivas sem imprimir todas as variáveis do container ou o conteúdo do segredo.
Classificação documental, agentes de contas de serviço e scheduler permanecem independentes.
Para desativação controlada, restaurar `false`/`Disabled` e aplicar a configuração pela mesma operação
autorizada. Nunca ativar automaticamente por sucesso de CI/deploy. Rollback de release mantém as
ressalvas de compatibilidade de schema descritas na seção Recuperação; não restaurar banco por mera
alteração dessas flags.

Preparação e evidências locais: [configuração segura do agente](T16-deploy-agente-openai.md).

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

Os dados ficam no volume Docker `saldo360-pgdata`; substituir o pacote assinado no staging não remove o volume.

Antes de cada deploy que já possui banco, é gerado um dump em `/srv/saldo360-deploy/backups`. Uma migration pode
não ser reversível para uma versão anterior da aplicação, por isso rollback de código não é tratado como
automaticamente seguro. Em falha após migration, diagnostique o schema e restaure o dump somente se realmente
necessário.
