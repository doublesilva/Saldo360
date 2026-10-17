# Coleta de documentos financeiros pelo Gmail

O `FinancialEmailCollector` é um adaptador de transporte entre Gmail e a Inbox. Ele não interpreta banco, cartão, senha, parcela, valor ou competência e nunca confirma operações financeiras.

## Arquitetura

`IFinancialEmailSource` separa a Application da API Gmail e oferece pesquisa, metadados de anexos e download. `GmailFinancialEmailSource` usa somente endpoints GET da Gmail API. Cada PDF é encaminhado individualmente para `FinancialDocumentInboxService.ReceiveAutomationAsync`, que calcula hash, deduplica, grava no storage privado e executa os parsers existentes.

O identificador externo é `gmail:{messageId}:{attachmentId}`. Assunto e nome do arquivo não são usados como identidade. Uma constraint da Inbox protege o identificador e outra protege o hash por família, inclusive sob concorrência.

## Autenticação

A execução autônoma requer um cliente OAuth 2.0 do Google e um refresh token obtido com consentimento offline para a mailbox configurada, limitado ao escopo `https://www.googleapis.com/auth/gmail.readonly`. Mailbox, client ID, client secret e refresh token devem vir de user-secrets, secrets de contêiner ou variáveis de ambiente. Não se usa senha Gmail nem IMAP.

Exemplos de variáveis:

```text
FinancialEmailImport__Enabled=true
FinancialEmailImport__Provider=Gmail
FinancialEmailImport__Gmail__Mailbox=<mailbox-explicita>
FinancialEmailImport__Gmail__ClientId=<secret>
FinancialEmailImport__Gmail__ClientSecret=<secret>
FinancialEmailImport__Gmail__RefreshToken=<secret>
FinancialEmailImport__Sources__0__Name=Bills
FinancialEmailImport__Sources__0__FamilyId=<uuid-da-familia>
FinancialEmailImport__Sources__0__Query=has:attachment filename:pdf
FinancialEmailImport__Sources__0__Enabled=true
```

Nenhuma credencial real é versionada. Service accounts só são apropriadas quando o domínio Google Workspace permite delegação em todo o domínio; a configuração atual usa OAuth de usuário com refresh token.

## Busca incremental

Cada perfil possui família, nome e query explícitos. O checkpoint persiste a data da mensagem mais recente concluída e os contadores da última execução. No bootstrap, a busca fica limitada por `LookbackDays`; toda execução limita `MaxMessages`. A query configurada recebe `after:{epoch}` com uma sobreposição conservadora de cinco minutos nas execuções seguintes. A identidade Gmail e a Inbox continuam sendo a defesa definitiva contra reprocessamento nas fronteiras do cursor. Se houver falha no lote, o cursor não avança, permitindo nova tentativa sem perder mensagens posteriores.

Somente anexos `application/pdf` ou com extensão `.pdf`, positivos e até 10 MB, são candidatos. Eles são baixados e processados sequencialmente. Imagens, HTML, ZIP, links e planilhas são ignorados.

## Execução manual e dry-run

```text
POST /api/financial-email-import/{profile}/run?dryRun=true
POST /api/financial-email-import/{profile}/run?dryRun=false
```

O endpoint exige autenticação, CSRF e participação na família configurada. `dryRun=true` pesquisa e lista apenas IDs técnicos dos candidatos; não baixa anexos, não grava Inbox e não avança checkpoint. A execução efetiva retorna contadores de mensagens, anexos, aceitos, duplicados, itens em revisão e falhas sanitizadas.

Uma falha não interrompe os demais anexos. PDF protegido é preservado na Inbox como `NeedsReview` com código sanitizado, sem CPF ou senha. O Gmail não é marcado como lido, movido, rotulado, respondido ou excluído.

## Limitações operacionais

Não há HostedService, cron, confirmação automática ou credenciais configuradas no repositório. A prova manual real depende do provisionamento externo do cliente OAuth, refresh token, mailbox e família. Assuntos/remetentes específicos deverão ser acrescentados apenas após validação operacional. Tokens, corpo da mensagem, conteúdo do PDF e assuntos não entram nos resultados nem nos logs.
