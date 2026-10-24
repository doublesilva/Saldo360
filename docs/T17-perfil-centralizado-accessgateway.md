# T17 — Perfil centralizado AccessGateway

24/10/2026 — **T17 concluída localmente**, com critérios essenciais e testes dirigidos aprovados.
Sem push, deploy ou alteração de produção. Validação operacional real permanece separada.

## Contratos e diagnóstico anterior

Inspecionados OIDC Authorization Code + PKCE, callback, filtro de claims, `DatabaseTicketStore`,
`ExternalIdentity`, `CurrentUser` e `/api/me`. Antes desta entrega, `SaveTokens=false` descartava
access token; a sessão persistia somente identidade/claims de exibição. Logo, sessão antiga não é
prova de possuir um token válido para o perfil. O ID token nunca é usado como Bearer.

Contrato conferido no checkout local `D:/PROJETOS/DoubleSilva.AccessGateway`:
`ProfileEndpoints.cs`, esquema/política Profile em `Program.cs` e documentos T09.10.8.
`GET https://gateway.home.doublesilva.com/api/me` exige JWT RS256 `at+jwt`, emissor válido,
audience `accessgateway-profile`, validade e `client_id`/`azp` autorizado. A concessão `cpf` pertence
à allowlist de atributos do cliente no Gateway, **não** a um scope OAuth `cpf` presumido.

Resposta: `issuer`, `subject`; com permissão, `cpfRegistered=false` ou `cpfRegistered=true` com
`cpf` de 11 dígitos. Sem permissão de atributo, os campos CPF são omitidos. O endpoint não aceita
parâmetros que selecionem outra pessoa. A homologação produtiva foi informada pelo responsável;
não foi revalidada por chamada autenticada nesta tarefa. O acesso pela ferramenta web não estava
disponível; a inspeção de contrato utilizou o código local, não um suposto retorno real.

## Token e identidade

`SaveTokens=false` continua: não passa a reter indiscriminadamente tokens do login. No callback,
somente o **access_token** do token endpoint, com token_type Bearer, pode ser guardado após validação:

- Assinatura RS256/JWKS do mesmo OIDC já usado no login, `typ=at+jwt`, audience de perfil,
  expiração/nbf e emissor.
- `iss` e `sub` exatamente iguais à identidade do ID token validado (comparação ordinal/opaca).
- `client_id`, ou `azp` na ausência dele, correspondente ao cliente OIDC configurado (`saldo360`
  em produção). Não toma audience de login como audience de perfil.
- Tokens com claims CPF são recusados para retenção, evitando CPF oculto no ticket.

Só o access token elegível fica nas properties do ticket já protegido pelo DataProtection no
`UserSessions.ProtectedTicket`, **nunca no cookie do navegador**. Nenhuma coluna/tabela nova.
ID token/refresh token e CPF não são guardados. Principal financeiro e `(issuer, subject)` continuam
iguais; e-mail/nome/CPF não viram chave, owner ou cadastro financeiro. Logout remove o ticket/token.

Antes de cada uso, o token da sessão cookie autenticada é novamente validado. Não existe fallback
para ID token, Authorization recebido do browser, credencial administrativa, client_credentials,
variável de ambiente ou token de outro usuário. Token inadequado não é enviado ao Gateway.
Login continua bem-sucedido mesmo quando o access token não é elegível para perfil.

## Serviço interno e HTTP

`ICurrentUserCpf.GetAsync` não recebe ID de outra pessoa: usa exclusivamente a requisição autenticada.
Retorna `UserCpfResult` com estado e `FullValue` disponível somente para uso interno autorizado.
`FullValue` tem `JsonIgnore` e `ToString` redigido. O resultado é cacheado apenas na instância scoped
da requisição, sem cache global, cookie, banco ou armazenamento do frontend.

O cliente tipado `AccessGatewayProfileClient` chama somente `/api/me`, sem query/ID. Valida identidade
retornada **antes de utilizar CPF**; valida CPF completo por dígitos verificadores. Campos ausentes,
JSON inválido, duplicatas e resposta incoerente não viram “CPF não cadastrado”.

TLS usa a mesma CA configurada em `Authentication:Oidc:BackchannelCaCertificatePath`, com a validação
existente de nome/cadeia/serverAuth; sem CA customizada, usa confiança TLS do sistema. Não ignora erros
de certificado. Cliente de perfil não segue redirects, não mantém cookies e não registra requisições,
headers ou corpo em loggers HTTP. Timeout inclui corpo da resposta; limite de corpo 8 KiB e orçamento
total curto. Não muda comportamento de redirects do backchannel OIDC existente.

## Contrato público

`/api/me` preserva os campos anteriores e acrescenta somente `cpfStatus`:

| Estado | Significado |
| --- | --- |
| Available | CPF retornado, válido e identidade conferida |
| NotRegistered | Gateway confirmou explicitamente CPF não cadastrado |
| AccessDenied | Token sem audience/tipo/cliente elegível, HTTP 403 ou atributo não concedido |
| SessionInvalid | Sem token reutilizável, token vencido/inválido, HTTP 401 ou identidade divergente |
| Unavailable | Gateway/metadata indisponível, timeout, resposta inválida ou CPF inconsistente |

`SessionInvalid` aqui se refere à consulta de perfil: não derruba uma sessão financeira válida.
Sem cookie autenticado, `/api/me` mantém HTTP 401. Com sessão financeira válida, falha de perfil
mantém HTTP 200 e os dados usuais de identidade, com estado específico; login e endpoints financeiros
não ganham dependência obrigatória do Gateway. `/api/me` permanece `Cache-Control: no-store`.

Não foi necessária máscara de CPF nesta UX: **nenhum dígito ou prefixo é exposto ao frontend**.
O contrato TypeScript acrescenta apenas estado opcional, compatível com consumidores/fixtures anteriores.
Não foram criadas tela de cadastro, rota pública de CPF ou integração adicional de outro módulo.

## Operação e limitações

Sessões anteriores não possuem access token: para usar o perfil após futura implantação autorizada,
é necessário novo login. O cliente já autorizado deve receber o JWT de acesso com a audience indicada;
o código valida isso, mas nenhum token real foi consultado nesta tarefa. Não alteramos scopes,
configuração do cliente no Authelia, AccessGateway, Caddy ou LLDAP.

Expiração do access token exige reautenticação para recuperar CPF; não há refresh automático/token
exchange nesta entrega. A sessão financeira pode continuar válida até seu prazo normal. Não há
fallback para CPF local/mock, senha, outro cadastro ou outra identidade. CPF não é persistido para
contornar indisponibilidade. Chamadas financeiras futuras que usem o serviço devem manter suas
próprias autorizações; esta tarefa não habilita novos fluxos de pagamento/importação.

Backlog não bloqueante, somente registro: avaliar refresh delegado/reauth UX e métricas por estado
sem dados pessoais, caso necessário. Sem implementação ou novas tarefas de refinamento.

## Testes e arquivos

Rodada final: **55/55**, sem ignorados, Release, 37 s;
`.artifacts/t17-profile/t17-profile-final.trx`. Inclui OIDC real contra provedor simulado, callbacks,
sessões/logout/CSRF, Gateway simulado, uso interno do CPF, TLS e workspaces financeiros em PostgreSQL
descartável. Primeira rodada 43/43 é sobreposta à final, não somar como testes únicos.

Comprova tokens de audience/tipo/cliente/assinatura/identidade/validade inadequados não enviados,
CPF não cadastrado versus ausência de grant/401/403/5xx/falha de rede, identidade divergente e CPF
inválido; autenticação preservada em todas as falhas de perfil. Confirma ticket contendo somente
access token elegível protegido, ausência de CPF/ID token/refresh token na sessão, ausência de
CPF/token no JSON público e nos logs, cookie contendo somente referência, resultado interno com
FullValue não serializável e dados financeiros/identidades anteriores preservados.

Simulação local de OIDC/JWKS/Gateway, sem tokens/CPFs reais. Backend e frontend compilados;
lint frontend sem erros, nove avisos preexistentes. Aviso de bundle >500 kB permanece.
Sem regressão completa, alteração de modelo ou migration. Diff/whitespace conferidos.

Arquivos principais:

- Application/Users: `ICurrentUserCpf`, status e resultado sensível não serializável.
- API/Authentication: `AccessGatewayProfile`, `AuthenticationSetup`, `AuthEndpoints`.
- Frontend: `api/auth.ts` (somente tipagem do estado).
- Testes: `OidcCallbackTests`, `AccessGatewayProfileClientTests`, `FakeOidcProvider`,
  `FakeProfileGateway` e `Saldo360ApiFactory`.
- Este relatório. Nenhum modelo/migration/configuração de deploy alterado.

Critérios operacionais reais não são alegados: nenhuma leitura de CPF/token real, chamada autenticada
ao Gateway produtivo, push, deploy ou alteração financeira. Nenhuma modificação no repositório/servidor
do AccessGateway ou infraestrutura de identidade.
