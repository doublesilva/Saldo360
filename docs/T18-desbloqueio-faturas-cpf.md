# T18 — Desbloqueio de faturas com CPF

## Escopo e comportamento

**T18 concluída localmente em 24/10/2026**, com testes direcionados e builds aprovados.
O desbloqueio reutiliza a extração de PDF existente e o serviço interno
`ICurrentUserCpf` da T17. Nenhum componente novo consulta diretamente o AccessGateway.

`PdfPasswordReader` primeiro verifica se o PDF abre sem senha, sem consultar CPF nesse caso.
Para PDF protegido, uma senha manual tem prioridade exclusiva: se incorreta, permite nova entrada manual,
sem buscar CPF como alternativa silenciosa. Sem senha manual, o provider autenticado normaliza e valida
os onze dígitos e seus verificadores; tenta primeiro três, depois cinco dígitos, interrompendo no primeiro
desbloqueio. Não há outras combinações ou tentativas de força bruta.

O provider exige sessão autenticada, ApplicationUser.Id correspondente, owner do workspace e acesso ao
cartão/documento informado. Não usa o HolderUserId para consultar CPF de terceiro. Os candidatos existem
somente em memória, não são serializados e não aparecem em ToString, logs ou respostas ao frontend.

## Integração

- Pré-visualização tradicional, extração pelo agente e comparação shadow usam o mesmo leitor.
- Processamento autenticado da Inbox usa o contexto do documento autorizado. Quando a classificação já
  abriu o PDF, a pré-visualização reutiliza o texto extraído e mantém a validação do cartão, evitando
  repetir o desbloqueio nessa etapa.
- A extração, identificação documental, conciliação e consolidação financeira existentes não tiveram
  seus cálculos alterados. Desbloquear não confirma a identidade documental do cartão.
- Coletores sem sessão OIDC não recebem acesso ao CPF por este fluxo. Nenhum scheduler, flag ou
  processamento automático foi habilitado.
- `PreviewDiagnosticAsync` desabilita explicitamente a resolução automática. A CLI
  `Saldo360.StatementDiagnostic` conserva `--password-file`: não simula usuário ou CPF administrativo.

## Falhas e fallback manual

O contrato mantém `PasswordRequired` para permitir a entrada manual. O motivo adicional seguro
`PasswordResolutionCode` diferencia `CpfNotRegistered`, `CpfInvalid`, `CpfSessionInvalid`,
`CpfAccessDenied`, `CpfUnavailable` e `PasswordNotRecognized`. Falha externa não vira ausência confirmada.
As telas de busca por e-mail, análise e detalhe da Inbox mostram mensagens distintas para os estados do perfil.
Nenhum prefixo é retornado. O campo adicional nulo é omitido da análise serializada para preservar a
representação anterior dos journals, sem reescrever histórico.

## Arquivos alterados

- API: `AuthenticatedDocumentPasswordProvider` e registro scoped em `AuthenticationSetup`.
- Application/CreditCards: `PdfPasswordReader`, contratos e serviços de preview tradicional/agente/shadow,
  `FinancialDocumentInbox`, `InboxStatementAnalysisService` e `StatementCategorization`.
- Frontend: contrato de análise, `PdfPasswordHint`, `CardLatestEmailSearch` e
  `InboxStatementAnalysisPanel` e `FinancialDocumentInbox`.
- Testes: `AuthenticatedDocumentPasswordProviderTests`, `CreditCardPdfPreviewServiceTests`,
  `CreditCardPdfPreviewTests` e `PdfPasswordHint.test.tsx`.

## Verificações

Os testes usam exclusivamente identidades, CPFs e documentos sintéticos. Há um PDF criptografado real
da fixture existente para comparar a interpretação financeira antes/depois do desbloqueio. Os cenários
cobrem PDF livre, ambos os prefixos e parada antecipada, senha manual válida/inválida, normalização,
CPF inválido/ausente, erros externos, isolamento por usuário/workspace/cartão/documento, ausência de
sessão, não exposição dos candidatos e bloqueio de CPF no diagnóstico administrativo.

- Frontend dirigido final: **44/44**, quatro arquivos, incluindo as telas afetadas.
- Build frontend aprovado; aviso não bloqueante de bundle acima de 500 kB.
- Lint frontend: zero erros, nove avisos preexistentes.
- Backend dirigido final: **128/128**, zero falhas/ignorados, Release, em 2 min 39 s.
  Artefato privado local: `.artifacts/t18-cpf/t18-cpf-final.trx`.
  Inclui preview tradicional, agente/shadow, Inbox, consolidação oficial, isolamento do CPF e regressão
  dirigida dos contratos de perfil/OIDC da T17. Não foi executada regressão completa de backend.
- Builds Release da API e `Saldo360.StatementDiagnostic`: zero avisos e zero erros.
- `git diff --check` aprovado; somente avisos Git de conversão de finais de linha no Windows.

Comandos de validação frontend (em `client`):

```powershell
npm run test -- --run src/components/PdfPasswordHint.test.tsx src/components/CardLatestEmailSearch.test.tsx src/components/InboxStatementAnalysisPanel.test.tsx src/components/FinancialDocumentInbox.test.tsx --maxWorkers=2
npm run build
npm run lint
```

Filtros backend usados em uma única execução de `dotnet test -c Release --no-restore`:
`AuthenticatedDocumentPasswordProviderTests`, `CreditCardPdfPreviewServiceTests`,
`CreditCardStatementAgentPreviewTests`, `CreditCardStatementAgentShadow`, `InboxStatementAnalysisTests`,
`FinancialDocumentInboxServiceTests`, `OfficialStatementConsolidationTests`, `CreditCardPdfPreviewTests`,
`OidcCallbackTests` e `AccessGatewayProfileClientTests`.

Uma primeira rodada backend teve 96 aprovados e sete falhas no teste novo de sigilo: a senha sintética
era a palavra `manual`, também presente em nome de parâmetro do stack trace. O fixture passou a usar
um valor exclusivo; a asserção de não exposição da senha foi preservada.

## Limites operacionais

Não houve consulta real ao AccessGateway, Gmail ou OpenAI, nem uso de CPF ou fatura real. Não há evidência
de ensaio operacional publicado ou revisão visual em navegador nesta tarefa. A implantação permanece
separada. Nenhuma migration, modificação do modelo financeiro, importação, alteração de pagamentos ou das
89 parcelas históricas, mudança de permissões de infraestrutura, push ou deploy foi executada.
