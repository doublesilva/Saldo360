# Coleta de documentos financeiros pelo Gmail

O `FinancialEmailCollector` é um adaptador de transporte entre Gmail e a Inbox. Ele não interpreta banco, cartão, senha, parcela, valor ou competência e nunca confirma operações financeiras.

## Arquitetura

`IFinancialEmailSource` separa a Application da API Gmail e oferece pesquisa, metadados de anexos e download. `GmailFinancialEmailSource` usa somente endpoints GET da Gmail API. Cada PDF é encaminhado individualmente para `FinancialDocumentInboxService.ReceiveAutomationAsync`, que calcula hash, deduplica, grava no storage privado e executa os parsers existentes.

O identificador externo é `gmail:{messageId}:part:{partId}`. O `partId` vem da estrutura MIME estável da mensagem. O `attachmentId` bruto existe apenas em memória durante a chamada de download e não participa da identidade, não é persistido nem retornado pelo dry-run. Assunto e nome do arquivo não são usados como identidade. Uma constraint da Inbox protege o identificador e outra protege o hash do conteúdo por família, inclusive sob concorrência.

## Autenticação

A execução autônoma requer um cliente OAuth 2.0 do Google e um refresh token obtido com consentimento offline para a mailbox configurada, limitado ao escopo `https://www.googleapis.com/auth/gmail.readonly`. Não se usa senha Gmail nem IMAP. Em produção, as três credenciais OAuth são lidas preferencialmente de arquivos montados somente leitura; quando um caminho de arquivo é configurado, arquivo ausente ou vazio falha de forma fechada e não recorre ao valor inline.

Exemplos de variáveis:

```text
FinancialEmailImport__Enabled=false
FinancialEmailImport__Provider=Gmail
FinancialEmailImport__TimeZone=America/Sao_Paulo
FinancialEmailImport__Schedule__0=09:00
FinancialEmailImport__Schedule__1=13:00
FinancialEmailImport__Schedule__2=18:00
FinancialEmailImport__Schedule__3=23:00
FinancialEmailImport__Gmail__Mailbox=<mailbox-explicita>
FinancialEmailImport__Gmail__ClientIdFile=/etc/saldo360/secrets/gmail/client-id
FinancialEmailImport__Gmail__ClientSecretFile=/etc/saldo360/secrets/gmail/client-secret
FinancialEmailImport__Gmail__RefreshTokenFile=/etc/saldo360/secrets/gmail/refresh-token
FinancialEmailImport__Sources__0__Name=Bills
FinancialEmailImport__Sources__0__FamilyId=<uuid-da-familia>
FinancialEmailImport__Sources__0__Query=has:attachment filename:pdf
FinancialEmailImport__Sources__0__Enabled=true
FinancialEmailImport__Sources__0__CollectionMode=Attachments
```

`CollectionMode` aceita `Attachments` (padrão retrocompatível), `Links` ou `AttachmentsAndLinks`. Para links, cada perfil deve declarar `AllowedLinkHosts` e, opcionalmente, `AllowedLinkPathPrefixes`. Exemplo DMAE: host `dmaeagvrt.procempa.com.br` e prefixo `/gsan/`.

Links são extraídos do corpo HTML/texto sem executar conteúdo. Somente HTTPS absoluto é aceito; esquemas inseguros, credenciais na URL, localhost, IP literal e caminhos fora da allowlist são rejeitados. O download usa GET sem OAuth/cookies, redirects manuais, limite de tamanho, timeout, validação `%PDF-` e bloqueio DNS de endereços privados, loopback, link-local e metadata. Cada redirect é revalidado.

A identidade persistida de um documento vinculado é `gmail:{messageId}:link:{ordinal}`. URL, query string e tokens nunca compõem a identidade nem os erros. O `attachmentId` Gmail continua restrito ao download de anexos em memória. O hash do conteúdo permanece como segunda barreira de deduplicação na Inbox.

Nenhuma credencial real é versionada. Service accounts só são apropriadas quando o domínio Google Workspace permite delegação em todo o domínio; a configuração atual usa OAuth de usuário com refresh token.

## Busca incremental

Cada perfil possui família, nome e query explícitos. O checkpoint persiste a data da mensagem mais recente concluída e os contadores da última execução. No bootstrap, a busca fica limitada por `LookbackDays`; toda execução limita `MaxMessages`. A query configurada recebe `after:{epoch}` com uma sobreposição conservadora de cinco minutos nas execuções seguintes. A identidade Gmail e a Inbox continuam sendo a defesa definitiva contra reprocessamento nas fronteiras do cursor. Se houver falha no lote, o cursor não avança, permitindo nova tentativa sem perder mensagens posteriores.

Anexos `application/pdf` ou com extensão `.pdf` case-insensitive, positivos e até 10 MB, são candidatos. Isso contempla o Itaú observado com MIME `application/octet-stream`. Depois do download, os primeiros bytes devem conter `%PDF-`; extensão e MIME sozinhos não autorizam a Inbox. Os anexos são processados sequencialmente. Imagens, ZIP e planilhas são ignorados; links somente são considerados nos perfis que habilitam explicitamente `Links` ou `AttachmentsAndLinks`.

## Classificação documental experimental

`POST /api/families/{familyId}/financial-documents/inbox/{id}/classify-preview` classifica, em modo somente leitura, um PDF já armazenado. Uma senha efêmera opcional pode ser enviada em `multipart/form-data`; ela nunca é persistida. O endpoint não altera status, parser, cartão, competência ou qualquer dado financeiro.

Os tipos genéricos são `CreditCardStatement`, `ElectricityBill`, `WaterBill`, `BillingDocument` e `Unknown`. Sinais fortes combinados classificam localmente faturas de cartão, contas de energia, contas de água e cobranças. Palavra isolada, nome do arquivo ou fonte Gmail não são prova. Quando a decisão local é inconclusiva e `FinancialDocumentClassification.AgentEnabled=true`, o provider estruturado já usado pelo agente de faturas recebe apenas texto paginado e prompt versionado. Resposta inválida, provider indisponível, baixa confiança, texto insuficiente ou PDF protegido produzem `Unknown` com código sanitizado.

`FinancialDocumentClassification.Enabled=false` mantém a análise produtiva no comportamento legado. `AgentEnabled=false` é o padrão e mantém o classificador exclusivamente determinístico. O endpoint `classify-preview` permanece somente leitura e nunca persiste seu resultado.

Quando `FinancialDocumentClassification.Enabled=true`, a Inbox persiste tipo, confiança, origem, código de falha e instante da classificação, sem armazenar evidências. A classificação ocorre antes do roteamento: somente `CreditCardStatement` segue para o pipeline de cartão. Energia, água e cobrança permanecem em `NeedsReview` com `RecognizedDocumentWithoutExtractor`; `Unknown` preserva o motivo seguro. A reanálise substitui integralmente a classificação e remove associação, parser e competência de cartão quando o novo tipo não é cartão. Documentos históricos continuam com campos nulos até serem reanalisados.

## Preview de contas de serviço

`TelecomBill` complementa os tipos de energia e água, sempre identificado por sinais combinados do conteúdo — nome de operadora isolado não é suficiente. Com `ServiceBillExtraction.Enabled=true`, o endpoint `POST /api/families/{familyId}/financial-documents/inbox/{id}/service-bill-preview` aceita somente esses três tipos e devolve emissor, competência, vencimento, valor decimal canônico e referências opcionais, acompanhados de `Reliable`, `NeedsReview` ou `Rejected`.

A extração textual determinística procura rótulos explícitos como valor a pagar, referência, vencimento, unidade consumidora, matrícula, contrato, hidrômetro ou linha. Se incompleta e `AgentEnabled=true`, reutiliza o provider OpenAI e Structured Outputs existentes, enviando somente tipo classificado e texto paginado. O preview e a senha são efêmeros: não alteram Inbox, recorrências, transações ou pagamentos. CEEE, DMAE e Vivo não possuem parsers por fornecedor nesta etapa.

## Pré-associação com despesas recorrentes

Com `ServiceBillReconciliation.Enabled=true`, `POST /api/families/{familyId}/financial-documents/inbox/{id}/service-bill-match-preview` reaproveita a extração de conta e procura, em modo somente leitura, previsões pendentes materializadas por recorrências de despesa na mesma família e competência. O recurso inicia desabilitado.

O resultado é `Matched`, `NotFound`, `Ambiguous`, `NeedsReview` ou `Rejected` e inclui os IDs explícitos da ocorrência e da recorrência, valores previsto e faturado como strings decimais, datas e evidências determinísticas. Competência, natureza de despesa, origem recorrente e compatibilidade do tipo documental são obrigatórias. Um vínculo automático exige ainda uma única evidência forte de identidade do emissor na descrição/categoria; vencimento próximo é apenas evidência auxiliar. O valor nunca funciona como identidade e diferenças de valor são apresentadas para revisão.

Ocorrências realizadas ou canceladas, competências distintas, tipos incompatíveis e documentos cuja extração não seja `Reliable` não são vinculados. Havendo mais de uma candidata forte, o resultado é `Ambiguous`; candidata apenas plausível produz `NeedsReview`. A consulta não altera previsão, recorrência, Inbox, orçamento, lançamento ou saldo e não usa agente/LLM para decidir o vínculo. Referências de unidade/contrato ainda não podem ser confrontadas porque o modelo atual de recorrência não possui campo correspondente.

Os perfis iniciais de exemplo são `ItauCards`, com `has:attachment subject:"fatura do seu cartão"`, e `NuEmpresas`, com `has:attachment subject:"fatura do seu cartão Nu Empresas"`. Ambos permanecem desabilitados até receberem família, mailbox e credenciais válidas. Nenhum final de cartão, nome, competência ou valor faz parte das queries.

## Execução manual e dry-run

```text
POST /api/financial-email-import/{profile}/run?dryRun=true
POST /api/financial-email-import/{profile}/run?dryRun=false
```

O endpoint exige autenticação, CSRF e participação na família configurada. Ele permanece disponível quando `FinancialEmailImport.Enabled=false`; essa chave controla exclusivamente o hosted service. O perfil solicitado ainda precisa estar habilitado. Assim, o bootstrap pode ser validado manualmente sem ativar o scheduler. `dryRun=true` pesquisa e lista apenas IDs técnicos dos candidatos; para links ele lê o corpo da mensagem, mas não acessa a URL externa, não grava Inbox e não avança checkpoint. A execução efetiva também retorna `linksScanned`, `candidateLinks`, `rejectedLinks` e `linkedDocumentsDownloaded`, além dos contadores anteriores.

Uma falha não interrompe os demais anexos. PDF protegido é preservado na Inbox como `NeedsReview` com código sanitizado, sem CPF ou senha. O Gmail não é marcado como lido, movido, rotulado, respondido ou excluído.

## Agendamento e concorrência

`FinancialEmailCollectorHostedService` espera sempre a próxima janela futura — 09:00, 13:00, 18:00 ou 23:00 em `America/Sao_Paulo`. O startup não coleta imediatamente e não reproduz janelas perdidas. Shutdown cancela a espera e impede novas execuções; operações em andamento recebem o token do host.

Cada perfil adquire `pg_try_advisory_lock` em uma conexão PostgreSQL dedicada, usando chave derivada de `FamilyId + Profile`. O lock permanece durante a execução e funciona entre containers. Contenção retorna `SkippedAlreadyRunning` sem mudar checkpoint. Falha de um perfil é capturada e não impede o próximo.

O checkpoint registra última tentativa e sucesso, duração, resultado e contadores sanitizados. Ele não armazena remetente, assunto, corpo, token ou conteúdo do documento.

## Estado operacional e monitoramento

`GET /api/financial-email-import/status` exige autenticação e retorna somente perfis de famílias das quais o usuário participa, incluindo próxima execução UTC e indicador `delayed`. A rotina é atrasada quando a última conclusão é anterior à última janela esperada e já passou uma hora de tolerância.

O Gmail não participa de `/health/ready`: indisponibilidade externa não derruba a prontidão financeira da API. Para o Uptime Kuma, mantenha o monitor HTTP normal de `/health/ready` e adicione um monitor autenticado externo que consulte o endpoint de status após as janelas, alertando quando `delayed=true` ou `status=Failed`. Não use heartbeat de 150 segundos para uma rotina de quatro execuções diárias.

## Limitações operacionais

Não há confirmação automática ou credenciais configuradas no repositório. A prova manual real depende do provisionamento externo do cliente OAuth, refresh token, mailbox e família. Remetentes específicos deverão ser acrescentados apenas após validação operacional. Tokens, corpo da mensagem, conteúdo do PDF e assuntos não entram nos resultados nem nos logs.
