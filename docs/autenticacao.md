# Autenticação

O Saldo360 usa **OpenID Connect** (authorization code + PKCE) com um provedor configurável. A API é a cliente OIDC (confidencial) e mantém a sessão; o navegador nunca vê tokens do provedor.

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
  "issuer": "https://auth.saldo360.localhost:9091",
  "subject": "df0ca80a-…",
  "displayName": "Família Teste",
  "username": "familia",
  "email": "familia@saldo360.localhost"
}
```

### Sessões anteriores a esta mudança

A migration `AddIssuerToUserSessions` adiciona a coluna `issuer` como **nula** e não apaga nenhuma sessão ou tabela. Sessões criadas antes dela (sem `issuer` na linha ou sem `iss` no ticket) são recusadas na primeira vez em que forem usadas e removidas. **O usuário precisa fazer login de novo**, uma única vez.

## Configuração

| Chave | Exemplo | Sensível | Observação |
| --- | --- | --- | --- |
| `Authentication:Oidc:Authority` | `https://auth.exemplo.com.br` | não | Emissor (issuer). A discovery é lida de `/.well-known/openid-configuration`. |
| `Authentication:Oidc:ClientId` | `saldo360` | não | |
| `Authentication:Oidc:ClientSecret` | — | **sim** | Só via user-secrets ou a variável `Authentication__Oidc__ClientSecret`. |
| `Authentication:Oidc:Scopes` | `["openid","profile","email"]` | não | |
| `Authentication:Oidc:CallbackPath` | `/signin-oidc` | não | Precisa casar com a redirect URI registrada. |
| `Authentication:Oidc:BackchannelCaCertificatePath` | `../../dev/authelia/secrets/tls/ca.public.crt` | não | Opcional. CA (PEM) do provedor; quando definida, é a **única** CA aceita no canal servidor→provedor. Caminho relativo ao content root da API. |
| `Authentication:Session:CookieName` | `__Host-saldo360.session` | não | Em desenvolvimento: `saldo360.session`, porque `__Host-` exige HTTPS. |
| `Authentication:Session:IdleTimeout` | `08:00:00` | não | Expiração por inatividade (renovação deslizante). |

A API não inicia se `Authority`, `ClientId` ou `ClientSecret` estiverem ausentes. A mensagem de erro cita só as chaves, nunca os valores.

## Cookies

| Cookie | HttpOnly | Secure | SameSite | Conteúdo |
| --- | --- | --- | --- | --- |
| Sessão (`__Host-saldo360.session`) | sim | produção: sempre; desenvolvimento: só em HTTPS | Lax | Referência à sessão no banco, protegida |
| CSRF (`__Host-saldo360.csrf`) | sim | idem | Strict | Metade "cookie" do token antiforgery |
| Correlação/nonce do OIDC | sim | idem | Lax | Temporários durante o login |

`SameSite=Lax` com retorno por query string (navegação GET) permite que o callback vindo de outro site leia os cookies de correlação. Fora do ambiente `Development`, todos os cookies exigem HTTPS.

## URLs de callback

Registre no provedor uma **redirect URI** por origem em que o frontend é servido:

| Ambiente | Redirect URI |
| --- | --- |
| Desenvolvimento (Vite) | `http://localhost:5173/signin-oidc` |
| Produção | `https://<domínio-do-saldo360>/signin-oidc` |

A API monta a URI a partir do host da requisição. Por isso, em desenvolvimento o proxy do Vite preserva o host (`changeOrigin: false`). Em produção, atrás de um proxy reverso, frontend e API precisam estar na **mesma origem**, e a API deve receber o esquema e o host originais: defina `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` e garanta que o proxy envie `X-Forwarded-Proto`/`X-Forwarded-Host`.

## Registro do cliente no provedor

Requisitos do cliente, em qualquer provedor OIDC:

- cliente **confidencial**, `token_endpoint_auth_method: client_secret_post`
- `response_types: [code]`, `grant_types: [authorization_code]`, `response_modes: [query]`
- PKCE obrigatório com `S256`
- escopos `openid profile email`
- redirect URIs da tabela acima

Exemplo para Authelia (o client secret guardado é um digest; o valor em texto puro vai só para a API):

```yaml
identity_providers:
  oidc:
    clients:
      - client_id: saldo360
        client_name: Saldo360
        client_secret: '$pbkdf2-sha512$310000$…'   # authelia crypto hash generate pbkdf2 --random
        public: false
        authorization_policy: one_factor           # ou two_factor
        require_pkce: true
        pkce_challenge_method: S256
        redirect_uris:
          - https://saldo360.exemplo.com.br/signin-oidc
        scopes: [openid, profile, email]
        response_types: [code]
        grant_types: [authorization_code]
        response_modes: [query]
        token_endpoint_auth_method: client_secret_post
```

## Provedor de desenvolvimento

O `docker-compose.yml` sobe um Authelia em `https://auth.saldo360.localhost:9091`. Os navegadores e o .NET 10 resolvem `*.localhost` para loopback. Os arquivos envolvidos:

- `dev/authelia/configuration.yml`: versionado e sem segredos. Lê os segredos de `dev/authelia/secrets/` via filtro `template`.
- `scripts/setup-dev-auth.ps1`: gera a CA e o certificado TLS, os segredos de sessão, armazenamento e HMAC, a chave JWKS e o client secret (cujo texto puro vai direto para o user-secrets da API), além do usuário `familia` com senha aleatória. Use `-Force` para regerar tudo.
- `scripts/smoke-login.sh`: login real ponta a ponta (curl → Vite → API → Authelia → callback → `/api/me` → logout).

## O que não vai para logs

- O client secret e os tokens não são registrados; a API não guarda tokens.
- Os códigos de autorização não aparecem nos logs nos níveis padrão.
- Uma falha de login gera só um `warn` com o erro OIDC (ex.: `invalid_request`), sem parâmetros sensíveis.
- O logout registra apenas o `sub` do usuário.

Os testes verificam que o client secret e o access token não aparecem em logs nem no redirecionamento. O smoke test foi acompanhado de uma varredura que procurou o client secret, a senha do usuário e a senha do banco nos logs da API e do Authelia, sem nenhuma ocorrência.
