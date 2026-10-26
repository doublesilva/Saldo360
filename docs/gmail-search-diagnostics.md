# Diagnóstico seguro de `/imports/email/latest`

26/10/2026 — Correção local do diagnóstico da busca mais recente. Não houve consulta real ao Gmail,
leitura de credenciais reais, chamada OpenAI ou intervenção no servidor.

## Contrato

O envelope HTTP 200 existente permanece compatível; isso **não** indica sucesso do processamento.
O resultado usa `status=Unavailable`, `Error`, `Busy` ou `NeedsReview` quando aplicável, com
`failureCode`, `failureStage` e `failureCategory`. A autorização/CSRF e o HTTP 404 dos recursos
inacessíveis permanecem anteriores ao acesso à caixa.

Exemplo sintético:

```json
{"status":"Unavailable","failureCode":"GmailOAuthForbidden","failureStage":"OAuthRefresh","failureCategory":"Forbidden"}
```

| Etapa | Exemplos de código |
|---|---|
| Configuração | `GmailConfigurationMissing`; `QueryMissing` mantém compatibilidade |
| Credenciais | `GmailCredentialsMissing`, `GmailCredentialsFileNotFound`, `GmailCredentialsUnreadable`, `GmailCredentialsEmpty` |
| Renovação OAuth | `GmailOAuthUnauthorized`, `GmailOAuthForbidden`, `GmailOAuthRateLimited`, `GmailOAuthTimeout`, `GmailOAuthInvalidResponse` |
| Busca Gmail | `GmailSearchUnauthorized`, `GmailSearchForbidden`, `GmailSearchRateLimited`, `GmailSearchNetwork`, `GmailSearchTimeout` |
| Mensagem/metadados | `GmailMessageUnauthorized`, `GmailMessageForbidden`, `GmailMessageInvalidResponse` |
| Inventário de anexos | `GmailAttachmentsForbidden`, `GmailAttachmentsFailed`, `AttachmentLimitReached` |
| Download/leitura do PDF | `EmailPdfDownloadUnauthorized`, `EmailPdfDownloadRateLimited`, `EmailPdfDownloadIo`, `EmailPdfDownloadInvalidResponse` |
| Consulta/recepção Inbox | `EmailInboxLookupFailed`, `EmailInboxReceptionConflict`, `EmailInboxReceptionRejected` |
| Preparação documental | `EmailDocumentValidationFailed`, `EmailDocumentValidationRejected` |
| Processamento documental | `EmailDocumentProcessingFailed`, `EmailDocumentProcessingRejected`, `EmailDocumentProcessingConflict`, `EmailDocumentProcessingBusy` |
| Lock da operação | `EmailOperationLockFailed`; `AlreadyRunning` mantém compatibilidade |

401, 403 e 429 tornam-se respectivamente `Unauthorized`, `Forbidden` e `RateLimited` na etapa
efetiva. Outros status HTTP usam `Upstream`; não se interpreta nem se registra o corpo de erro
OAuth/Gmail para inferir causas adicionais. `Timeout` distingue cancelamento do transporte de
cancelamento solicitado pelo cliente: este último continua propagado, sem retry.

A consulta de anexos Gmail obtém a mensagem por `messages/{id}`, mas mantém a etapa
`GmailAttachments`; leitura de metadados/corpo usa `GmailMessage`. O download usa `PdfDownload`. A exceção
tipada conserva a etapa interna (incluindo OAuth/credenciais), em vez de ser reclassificada como busca.

## Segurança e resultado do processamento

O evento estruturado `16051 / CardLatestEmailFailure`, nível Warning, contém somente `Stage`,
`Category` e `FailureCode`. Não recebe objeto de exceção, inner exception, tipo arbitrário, IDs Gmail,
workspace/cartão, nomes de arquivos, caminhos, queries, headers, tokens, conteúdo financeiro ou
payload de resposta. Os loggers HTTP dos dois clientes Gmail continuam desabilitados.

`GmailSecretReader` não constrói mais mensagens com caminhos. Arquivo configurado ausente,
ilegível ou vazio continua falhando **sem** recorrer ao valor inline. Nenhuma credencial alternativa
é procurada. JSON/token OAuth inválido e anexo sem dados não viram busca vazia ou download bem-sucedido.

Falhas técnicas interrompem a operação: não há retry, troca de cartão, busca em e-mail mais antigo
ou fallback de parser para mascará-las. No perfil documental, rejeição de leitura do PDF também
interrompe com diagnóstico. Filtragem normal de anexos não PDF/cartões incompatíveis permanece.

Documento recebido não implica processamento aprovado. Exceções e resultados explícitos de falha,
rejeição, indisponibilidade ou conflito não retornam `Found`/`AlreadyReceived`. Se a recepção já
ocorreu, o ID autorizado do documento e a indicação de duplicidade são preservados para continuação
idempotente; os diagnósticos não executam nova importação. Senha requerida/rejeitada permanece
recuperável, como `NeedsReview`, conservando o contrato existente sem alegar sucesso de processamento.
Pendência documental legítima não é confundida com importação financeira concluída.
Replay comprovado pelo repositório (`AlreadyImported`) permanece sucesso idempotente, mesmo quando
o analisador legado recusa reanalisar o documento importado com `Conflict`. Conflito sem essa prova
continua bloqueado. Os resultados válidos `NewVersionConsolidated` e `InvoicesLinked` também são
preservados; não se confunde confirmação financeira existente com falha técnica.

Nenhum writer, cálculo financeiro, checkpoint, política de associação, autenticação, regra de CSRF,
parser ou modelo OpenAI foi alterado. A dependência de logging adicionada à Application é somente
`Microsoft.Extensions.Logging.Abstractions`, na mesma versão 10.0.13 usada pela solução.

## Validação local

Rodada final: **218/218 aprovados**, zero falhas/ignorados, Release, 95 s de testhost
(104,385 s de processo). Artefato ignorado pelo Git:
`.artifacts/gmail-diagnostics/gmail-diagnostics-final-stable.trx`.

- 100 casos de serviço/Gmail/autorização/endpoint: credenciais ausentes, arquivo ausente/vazio/
  ilegível, prioridade do arquivo sem fallback, HTTP 401/403/429, outros HTTP, timeout, cancelamento,
  JSON/token/base64 inválidos, download, recepção rejeitada e processamento falhado. Logs inspecionados
  como dados estruturados; nenhum objeto de exceção ou conteúdo arbitrário é propagado.
- Outros 118 casos: coletores, busca histórica, Inbox, parcelas conhecidas, configuração documental
  e contratos de seleção da CI. Preservados isolamento, CSRF, atomicidade, replay e nove parcelas
  sintéticas com datas desconhecidas. Não se executou a suíte backend completa nem a suíte frontend.
- PostgreSQL real: 64 bancos consumidores descartáveis nos dois fixtures existentes, sem alteração
  de schema/migration versionada nem acesso a banco produtivo. Nenhum container de teste permaneceu.
- Build de toda `Saldo360.slnx`, Release com recompilação integral: **zero avisos/erros**, 127,786 s
  de processo. Fontes e testes foram conferidos por hash antes/depois dessa execução estável,
  sem edição concorrente durante a validação final. `git diff --check` aprovado.

SHA-256 do assembly de testes final:
`6df7ab7f0151061e0d498c19b7b66f78d190e9f35cd975e53d4a0d82081682a5`.
SHA-256 do TRX final:
`e59f5c0690c9f3126718a4aabbc127f6f4f6e6841cb4a49b7377e664e8e52016`.

As suítes usam handlers HTTP e provedores documentais sintéticos. O teste HTTP autorizado com
credenciais ausentes falha antes do transporte OAuth/Gmail; o teste de recepção real verifica que
processamento indisponível retorna `Error`, mas repetir o documento mantém o mesmo ID e nenhuma
compra/fatura/pagamento é criado nesse cenário. Nenhum teste depende de Gmail/OpenAI reais.

Resultados intermediários foram preservados, sem reduzir as expectativas: a leitura de diretório
no Windows exigiu classificação explícita como `Unreadable`; a rodada ampliada expôs o replay
legítimo `Conflict`/`AlreadyImported`, agora preservado sem permitir conflitos comuns. Houve ainda
execuções durante a composição de fontes/testes com assemblies anteriores ao último ajuste;
elas não foram usadas como aprovação. A recompilação final serializada passou integralmente.
O ajuste inicial do analyzer xUnit foi somente o uso da sobrecarga correta de `Assert.Single`.

O erro histórico `EmailSearchUnavailable` não permite recuperar retroativamente a causa perdida.
Esta correção torna uma próxima tentativa operacional diagnosticável; não afirma que a credencial,
o OAuth ou a API Gmail do servidor atual tenham sido corrigidos ou verificados nesta execução.
