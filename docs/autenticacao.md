# Autenticação

O Saldo360 usa **OpenID Connect** (authorization code + PKCE) com um provedor configurável. O padrão é o **Authelia do servidor**, `https://auth.home.doublesilva.com`. A API é a cliente OIDC (confidencial) e mantém a sessão; o navegador nunca vê tokens do provedor.

## Fluxo

1. O frontend navega para `GET /auth/login?returnUrl=/`.
2. A API redireciona ao provedor. Se ele anunciar suporte, a API usa PAR (Pushed Authorization Request).
3. O usuário se autentica no provedor, que redireciona para `{origem do frontend}/signin-oidc?code=…`.
4. A API troca o `code` por tokens no canal servidor→provedor, valida o ID token, lê o *userinfo* e cria a sessão:
   - guarda a identidade externa (`iss` + `sub`, ver abaixo) e apenas as claims de exibição `name`, `preferred_username` e `email`; os tokens são descartados (`SaveTokens = false`);
   - grava o ticket da sessão no PostgreSQL (`user_sessions`), protegido com Data Protection, junto com `issuer` e `subject` na própria linha;
   - envia ao navegador um cookie que contém só a referência à sessão.
5. A API redireciona para o `returnUrl`. O frontend consulta `GET /api/me`.

**Logout:** `POST /auth/logout` com o header `X-CSRF-TOKEN` (obtido em `GET /auth/csrf`) apaga a linha da sessão e o cookie. Um cookie copiado antes do logout deixa de valer.

> **Limitação:** o Authelia 4.39 não oferece *RP-initiated logout* (`end_session_endpoint`), então a sessão **no provedor** continua ativa até expirar. Depois de "Sair", clicar em "Entrar" pode logar de novo sem pedir senha. A sessão do Saldo360 é encerrada de fato.

## Identidade externa: issuer + subject

O usuário é identificado pelo **par `issuer` + `subject`**:

- `subject` (claim `sub`) só é único **dentro** de um emissor. Dois provedores podem emitir o mesmo `sub` para pessoas diferentes.
- `issuer` (claim `iss`) é o emissor que autenticou o usuário.

Regras:

- **Origem do issuer:** o `iss` é lido do **ID token já validado** pelo middleware (assinatura, emissor esperado, audiência, nonce), no evento `OnTokenValidated`. Nunca vem da requisição, de parâmetros do navegador, do *userinfo* ou da configuração (`Authority`). Um ID token sem `iss` ou sem `sub` válidos faz o login falhar (`/?login=failed`).
  - Detalhe de implementação: as `ClaimActions` padrão do handler OIDC apagam a claim `iss` depois do `OnTokenValidated`. Por isso a identidade validada é guardada durante o callback e reaplicada ao montar a sessão, em `OnTicketReceived`.
- **Valores opacos:** `issuer` e `subject` são strings opacas, comparadas de forma ordinal, sem conversão para Guid, mudança de caixa, *trim* ou outra normalização.
- **Persistência:** cada sessão guarda o par na linha (`user_sessions.issuer`, `user_sessions.subject`) e no ticket protegido. Na leitura, a API exige `iss` e `sub` nos dois lugares e exige que coincidam. Se não coincidirem, a sessão é recusada e removida.
- **Várias sessões:** o índice `(issuer, subject)` **não é único**, pois um usuário pode ter várias sessões (dispositivos, abas).

`GET /api/me` devolve `issuer` e `subject`. O campo `id` continua igual a `subject` apenas por compatibilidade com o frontend: **`id` sozinho não é uma identidade global**. O vínculo ao núcleo familiar e às permissões (T03) deve usar o par `issuer` + `subject`.

```json
{
  "id": "df0ca80a-…",
  "issuer": "https://auth.home.doublesilva.com",
  "subject": "df0ca80a-…",
  "displayName": "Família Teste",
  "username": "familia",
  "email": "familia@exemplo.com.br"
}
```

### Sessões anteriores a esta mudança

A migration `AddIssuerToUserSessions` adiciona a coluna `issuer` como **nula** e não apaga nenhuma sessão ou tabela. Sessões criadas antes dela (sem `issuer` na linha ou sem `iss` no ticket) são recusadas na primeira vez em que forem usadas e removidas. **O usuário precisa fazer login de novo**, uma única vez.

## Provedor padrão: Authelia do servidor

| Item | Valor |
| --- | --- |
| Authority (issuer) | `https://auth.home.doublesilva.com` |
| ClientId | `saldo360` (exclusivo; não reutilize clients de outros apps) |
| Autenticação do client | `client_secret_post` no token endpoint **e** no PAR |
| TLS | Certificado emitido pela CA interna do Caddy (`Caddy Local Authority - 2026 ECC Root`, via intermediária) |

Discovery conferida em 2026-10-09: issuer exato `https://auth.home.doublesilva.com`; PAR disponível e opcional; sem `end_session_endpoint`.

### 1. Segredo exclusivo do client

```powershell
pwsh scripts/new-oidc-client-secret.ps1            # ou: npm run setup:client-secret
```

O script gera o segredo com a CLI do Authelia (Docker) e grava o texto puro no user-secrets da API (`Authentication:Oidc:ClientSecret`). Na tela ele mostra **só o digest** PBKDF2, que vai para o cadastro no Authelia. O texto puro não aparece no console nem em argumentos de comando. Com `-OutFile <caminho>`, o script também grava o segredo num arquivo, para provisionar outro ambiente com `Authentication__Oidc__ClientSecretFile`, no mesmo padrão do `/run/secrets` do HomeAutomation.

### 2. Cadastro no Authelia do servidor

Use o trecho [docs/authelia-client-saldo360.yml](authelia-client-saldo360.yml). Ele deve ser **acrescentado** a `identity_providers.oidc.clients`, sem substituir os clients existentes:

- code + PKCE obrigatório (`S256`), escopos `openid profile email` e `response_modes: [query]`;
- `token_endpoint_auth_method` e `pushed_authorization_request_endpoint_auth_method` com o valor `client_secret_post`; o padrão do Authelia é `client_secret_basic`, que o .NET não usa;
- redirect URI de desenvolvimento: `http://localhost:5173/signin-oidc`;
- redirect URI de produção: `https://<origem pública do Saldo360>/signin-oidc`. **A origem de produção ainda não foi escolhida**, então a linha fica comentada até existir.

Depois de editar, valide a configuração e reinicie o Authelia. Um `invalid_client` no login (motivo `provedor:invalid_client` no log) indica cadastro ausente, digest que não corresponde ao segredo ou método de autenticação diferente de `client_secret_post`.

**Acesso ao servidor:** o Authelia roda no host `home-server` (192.168.0.162), container `authelia`, com compose em `/home/diego/containers/authelia` e configuração em `config/configuration.yml` (arquivo do root; montado como `/config` no container). Os segredos e certificados do HomeAutomation ficam em `/etc/homeautomation/secrets` e `/etc/homeautomation/certs`.

- A chave SSH do host tem passphrase e está carregada no **agente do Windows**. Use o `ssh.exe` do Windows (`C:\Windows\System32\OpenSSH\ssh.exe home-server`). O `ssh` do Git Bash não enxerga esse agente e falha com `Permission denied (publickey)`, mesmo com o servidor aceitando a chave.
- Edite a configuração sempre a partir de uma cópia (candidata) e valide antes de trocar a ativa: `docker exec authelia authelia validate-config --config /config/<arquivo>`.
- Faça backup no mesmo padrão dos anteriores (`configuration.yml.bak-before-<mudança>-<data>`).
- O Authelia guarda as sessões em memória: o reinício (`docker restart authelia`) exige novo login no SSO. As sessões próprias dos apps não são afetadas.

**Estado:** client `saldo360` cadastrado em 2026-10-10, só com o callback de desenvolvimento, `one_factor` e `consent_mode: implicit`. O backup é `configuration.yml.bak-before-saldo360-20261010-103124`. O callback de produção continua pendente.

### 3. Confiança na CA do Caddy

A API confia **somente** na CA configurada em `Authentication:Oidc:BackchannelCaCertificatePath`, e não no repositório do sistema. A validação continua exigindo o nome do servidor correto, a cadeia até essa CA (a intermediária enviada pelo Caddy entra só para montar a cadeia) e o uso `serverAuth`.

Em desenvolvimento, o caminho padrão é `dev/certs/caddy-local-root.crt`, ignorado pelo git. Obtenha a CA de uma destas formas:

```powershell
# a) Copiar do servidor (mesmo arquivo montado nos containers do HomeAutomation)
scp home-server:/etc/homeautomation/certs/caddy-local-root.crt dev/certs/caddy-local-root.crt

# b) Exportar do Windows, se a CA do Caddy já estiver confiável na máquina
$ca = Get-ChildItem Cert:\CurrentUser\Root | Where-Object Subject -like '*Caddy Local Authority*Root*' | Select-Object -First 1
New-Item -ItemType Directory -Force dev/certs | Out-Null
Set-Content -NoNewline dev/certs/caddy-local-root.crt $ca.ExportCertificatePem()
```

O navegador também precisa confiar nessa CA para abrir `https://auth.home.doublesilva.com` sem aviso. Em produção, monte a CA e aponte `Authentication__Oidc__BackchannelCaCertificatePath` para ela, como `/usr/local/share/ca-certificates/caddy-local-root.crt` nos containers do HomeAutomation.

## Provedor local opcional (Authelia no Compose)

Para trabalhar sem o servidor, existe um Authelia local em `https://auth.saldo360.localhost:9091`, no profile `authelia-local` do Compose:

```bash
npm run setup:authelia-local      # uma vez: CA, segredos e usuários "familia" e "convidado"
npm run dev:authelia-local        # Postgres + Authelia local, migrations, API (profile http-authelia-local) e frontend
npm run smoke:login:authelia-local
```

O launch profile `http-authelia-local` sobrescreve `Authority`, `BackchannelCaCertificatePath` e `ClientSecretFile`, este apontando para `dev/authelia/secrets/oidc_client_secret.txt`. Por isso o user-secrets continua reservado ao segredo do servidor. Os segredos do Authelia local ficam em `dev/authelia/secrets/`, ignorado pelo git.

## Configuração

| Chave | Exemplo | Sensível | Observação |
| --- | --- | --- | --- |
| `Authentication:Oidc:Authority` | `https://auth.home.doublesilva.com` | não | Emissor (issuer). A discovery é lida de `/.well-known/openid-configuration`. |
| `Authentication:Oidc:ClientId` | `saldo360` | não | |
| `Authentication:Oidc:ClientSecret` | — | **sim** | Só via user-secrets ou a variável `Authentication__Oidc__ClientSecret`. |
| `Authentication:Oidc:ClientSecretFile` | `/run/secrets/oidc-saldo360-client-secret` | caminho não; conteúdo **sim** | Opcional; tem precedência sobre `ClientSecret`. Só a quebra de linha final é removida. Arquivo ausente ou vazio impede a inicialização. |
| `Authentication:Oidc:Scopes` | `["openid","profile","email"]` | não | |
| `Authentication:Oidc:CallbackPath` | `/signin-oidc` | não | Precisa casar com a redirect URI registrada. |
| `Authentication:Oidc:BackchannelCaCertificatePath` | `../../dev/certs/caddy-local-root.crt` | não | CA (PEM) do provedor; quando definida, é a **única** CA aceita no canal servidor→provedor. Relativo ao content root da API. |
| `Authentication:Session:CookieName` | `__Host-saldo360.session` | não | Em desenvolvimento: `saldo360.session`, porque `__Host-` exige HTTPS. |
| `Authentication:Session:IdleTimeout` | `08:00:00` | não | Expiração por inatividade (renovação deslizante). |
| `ReverseProxy:KnownProxies` | `["172.20.0.2"]` | não | IPs dos proxies confiáveis. |
| `ReverseProxy:KnownNetworks` | `["172.20.0.0/16"]` | não | Redes confiáveis (CIDR), como a rede Docker do Caddy. |
| `ReverseProxy:AllowedHosts` | `["<host público>"]` | não | Hosts aceitos em `X-Forwarded-Host`; vazio aceita qualquer um vindo de proxy confiável. |

A API não inicia se `Authority`, `ClientId` ou o segredo estiverem ausentes, nem se um valor de `ReverseProxy` for inválido. As mensagens citam só as chaves, nunca os segredos.

## Cookies

| Cookie | HttpOnly | Secure | SameSite | Conteúdo |
| --- | --- | --- | --- | --- |
| Sessão (`__Host-saldo360.session`) | sim | produção: sempre; desenvolvimento: só em HTTPS | Lax | Referência à sessão no banco, protegida |
| CSRF (`__Host-saldo360.csrf`) | sim | idem | Strict | Metade "cookie" do token antiforgery |
| Correlação/nonce do OIDC | sim | idem | Lax | Temporários durante o login |

`SameSite=Lax` com retorno por query string (navegação GET) permite que o callback vindo de outro site leia os cookies de correlação. Fora do ambiente `Development`, todos os cookies exigem HTTPS.

## Proxy reverso e URL de callback

A API monta o `redirect_uri` a partir do esquema e do host da requisição. Em desenvolvimento, o proxy do Vite preserva o host (`changeOrigin: false`), o que gera `http://localhost:5173/signin-oidc`.

Atrás de um proxy reverso como o Caddy, a API recebe HTTP interno e o callback precisa usar a origem pública HTTPS. Para isso:

- O middleware de forwarded headers roda **antes** da autenticação e processa `X-Forwarded-For`, `X-Forwarded-Proto` e `X-Forwarded-Host`.
- Ele só aceita esses headers quando a conexão vem de um IP de `ReverseProxy:KnownProxies`, de uma rede de `ReverseProxy:KnownNetworks` ou do loopback (padrão do ASP.NET Core). De qualquer outra origem, os headers são **ignorados**.
- As listas **não são limpas**: os valores configurados são acrescentados ao padrão.
- `ReverseProxy:AllowedHosts` restringe os hosts aceitos em `X-Forwarded-Host`.
- **Não use** `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true`: essa variável limpa as listas e passa a confiar em qualquer origem.
- Frontend e API devem ficar na **mesma origem** pública, para que os cookies e o callback casem.

Exemplo para produção (valores a definir quando a publicação for escolhida):

```bash
ReverseProxy__KnownNetworks__0=<rede docker do Caddy, ex.: 172.20.0.0/16>
ReverseProxy__AllowedHosts__0=<host público do Saldo360>
```

O Caddy deve encaminhar o host público e `X-Forwarded-Proto: https`. Com isso, o callback gerado fica `https://<host público>/signin-oidc`, exatamente como cadastrado no Authelia.

## Logs de falha de login

Quando o login não termina, por exemplo por erro do provedor, `state` inválido, ID token inválido ou provedor inacessível, a API:

- registra um único `warn` no formato `Login OIDC não concluído. Motivo: <motivo>. Correlação: <id>.`;
- redireciona para `/?login=failed&ref=<id>`, e o frontend mostra esse código de referência.

O `<motivo>` é uma categoria fixa: `provedor:<código OAuth padronizado>` (ex.: `provedor:invalid_client`), `provedor:erro_nao_padronizado`, `id_token_invalido`, `identidade_invalida`, `state_invalido`, `correlacao_invalida`, `provedor_inacessivel` ou `falha_nao_classificada`. O texto do provedor (`error_description`), a mensagem da exceção, o `code`, tokens e segredos **nunca** são registrados. Os logs próprios do handler OIDC do ASP.NET Core, que incluiriam esses dados, ficam desligados.

## O que não vai para logs

- O client secret e os tokens não são registrados, e a API não guarda tokens.
- Códigos de autorização e respostas do provedor também não são registrados (ver seção anterior).
- O logout registra apenas o `sub` e o `iss` do usuário.

Testes automatizados simulam falhas com `code`, token, senha e descrição fictícios e verificam que nada disso aparece nos logs.

## Smoke test de login (curl)

```bash
bash scripts/smoke-login.sh                    # Authelia do servidor; pede usuário e senha (sem eco)
bash scripts/smoke-login.sh --authelia-local   # Authelia local
```

O script lê as variáveis `AUTHORITY`, `CA_FILE`, `SMOKE_USER`, `PASSWORD_FILE` e `FRONT`. A senha vem de arquivo ou do prompt sem eco e chega ao curl pela entrada padrão, nunca em argumentos de comando. O script cobre login, `issuer`/`subject`, refresh, logout com CSRF, replay do cookie antigo e `/health`. Ele **não substitui o navegador**: com `two_factor` ou tela de consentimento, valide pelo navegador.
