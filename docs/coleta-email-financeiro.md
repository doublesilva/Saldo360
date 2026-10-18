# Coleta de documentos financeiros pelo Gmail

O `FinancialEmailCollector` é um adaptador de transporte entre Gmail e a Inbox. Ele não interpreta banco, cartão, senha, parcela, valor ou competência e nunca confirma operações financeiras.

## Arquitetura

`IFinancialEmailSource` separa a Application da API Gmail e oferece pesquisa, metadados de anexos e download. `GmailFinancialEmailSource` usa somente endpoints GET da Gmail API. Cada PDF é encaminhado individualmente para `FinancialDocumentInboxService.ReceiveAutomationAsync`, que calcula hash, deduplica, grava no storage privado e executa os parsers existentes.

O identificador externo é `gmail:{messageId}:{attachmentId}`. Assunto e nome do arquivo não são usados como identidade. Uma constraint da Inbox protege o identificador e outra protege o hash por família, inclusive sob concorrência.

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
```

Nenhuma credencial real é versionada. Service accounts só são apropriadas quando o domínio Google Workspace permite delegação em todo o domínio; a configuração atual usa OAuth de usuário com refresh token.

## Busca incremental

Cada perfil possui família, nome e query explícitos. O checkpoint persiste a data da mensagem mais recente concluída e os contadores da última execução. No bootstrap, a busca fica limitada por `LookbackDays`; toda execução limita `MaxMessages`. A query configurada recebe `after:{epoch}` com uma sobreposição conservadora de cinco minutos nas execuções seguintes. A identidade Gmail e a Inbox continuam sendo a defesa definitiva contra reprocessamento nas fronteiras do cursor. Se houver falha no lote, o cursor não avança, permitindo nova tentativa sem perder mensagens posteriores.

Anexos `application/pdf` ou com extensão `.pdf` case-insensitive, positivos e até 10 MB, são candidatos. Isso contempla o Itaú observado com MIME `application/octet-stream`. Depois do download, os primeiros bytes devem conter `%PDF-`; extensão e MIME sozinhos não autorizam a Inbox. Os anexos são processados sequencialmente. Imagens, HTML, ZIP, links e planilhas são ignorados.

Os perfis iniciais de exemplo são `ItauCards`, com `has:attachment subject:"fatura do seu cartão"`, e `NuEmpresas`, com `has:attachment subject:"fatura do seu cartão Nu Empresas"`. Ambos permanecem desabilitados até receberem família, mailbox e credenciais válidas. Nenhum final de cartão, nome, competência ou valor faz parte das queries.

## Execução manual e dry-run

```text
POST /api/financial-email-import/{profile}/run?dryRun=true
POST /api/financial-email-import/{profile}/run?dryRun=false
```

O endpoint exige autenticação, CSRF e participação na família configurada. Ele permanece disponível quando `FinancialEmailImport.Enabled=false`; essa chave controla exclusivamente o hosted service. O perfil solicitado ainda precisa estar habilitado. Assim, o bootstrap pode ser validado manualmente sem ativar o scheduler. `dryRun=true` pesquisa e lista apenas IDs técnicos dos candidatos; não baixa anexos, não grava Inbox e não avança checkpoint. A execução efetiva retorna contadores de mensagens, anexos, aceitos, duplicados, itens em revisão e falhas sanitizadas.

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
