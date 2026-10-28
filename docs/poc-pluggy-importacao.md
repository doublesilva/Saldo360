# T09.2 — Importação controlada de transações Pluggy

Evolui o Laboratório Pluggy ([poc-pluggy.md](poc-pluggy.md)) para **sincronizar transações e guardá-las à parte do núcleo financeiro**. A importação é informativa: **não cria lançamentos, previsões, transferências nem pagamentos, não atualiza saldos e não faz conciliação.** Nenhuma tabela financeira operacional é lida ou escrita pela importação (há teste que compara contagens e saldo antes e depois).

## Configuração

Duas chaves independentes, ambas desligadas por padrão:

| Chave | Efeito |
|---|---|
| `Pluggy:Enabled` | Habilita o laboratório (T09.1). |
| `Pluggy:ImportEnabled` | Habilita a importação (T09.2). Desligada, todas as rotas abaixo respondem 404 e a seção não aparece na tela. |

Local: `PLUGGY_IMPORT_ENABLED=true` no `.env` e `pwsh scripts/pluggy-poc-load-env.ps1` (grava em user-secrets). Ajustes opcionais em `Pluggy:*`:

| Opção | Padrão | Uso |
|---|---|---|
| `SyncMaxPagesPerAccount` | 50 | Páginas de 500 por conta; ao atingir, a coleta fica **incompleta** (nada é marcado como ausente). |
| `SyncPageDelayMilliseconds` | 250 | Pausa entre páginas. |
| `SyncRateLimitRetries` | 2 | Novas tentativas após 429. |
| `SyncRateLimitDelaySeconds` / `SyncMaxRateLimitWaitSeconds` | 10 / 60 | Espera quando não há `Retry-After`; teto de qualquer espera. |
| `SyncStaleMinutes` | 30 | Coleta "em andamento" mais antiga que isto é tratada como abandonada. |

## Migration

`20261028135409_AddPluggyTransactionImport` — apenas três tabelas novas, nenhuma alteração em tabelas existentes. Aplicar depois de `AddPluggyItemLinks`.

| Tabela | Conteúdo |
|---|---|
| `pluggy_sync_runs` | Uma linha por sincronização: início/fim, status, contagens agregadas (`seen`, `created`, `updated`, `unchanged`, `missing`, `errors`), resumo de erros. Índice único parcial `ux_pluggy_sync_runs_running` (`connection_id` onde `status='Running'`): no máximo uma coleta em andamento por conexão, mesmo entre instâncias. |
| `pluggy_imported_transactions` | A transação importada. Índice único `ux_pluggy_imported_transactions_external` em **(conexão, conta Pluggy, ID da transação Pluggy)**. |
| `pluggy_transaction_changes` | Histórico mínimo: por alteração, os campos mudados e, para status e valor, o antes e o depois. |

As três têm `workspace_id` e entram no reset do orçamento (apagadas antes dos vínculos). Foram incluídas no inventário do importador de planilha (`ReadOnlyBudgetDestinationSnapshot`).

### O que é guardado (e o que não é)

Guardado: conexão, conta (ID, tipo, rótulo "nome ••••1234"), ID da transação, data, valor (`numeric(19,4)`), moeda, descrição e descrição original (até 500 caracteres), tipo (`DEBIT`/`CREDIT`), status, tipo de operação, categoria, número da parcela, total de parcelas, ID da fatura, últimos 4 dígitos do cartão, meio de pagamento, classificação sugerida e o motivo, chave de possível duplicidade, datas de primeira importação/última observação/última alteração e contagem de ausências.

**Não guardado:** o payload bruto, pagador, recebedor, CPF/CNPJ, números completos de conta ou cartão, `apiKey`, `clientSecret` ou qualquer token. A decisão foi não persistir payload adicional: os campos acima bastam para a finalidade, e o resto aumentaria a exposição sem uso.

## Sincronização

`POST /api/financial-workspaces/{workspaceId}/pluggy-poc/connections/{id}/sync` (sessão + CSRF, dono do orçamento). Resposta: o registro da coleta.

1. Cria a coleta; se já existe uma em andamento para a conexão, responde **409**.
2. Lê o Item. Se a autorização expirou, registra `ConsentExpired` e termina sem consultar transações.
3. Lista as contas e, para cada uma, percorre **todas as páginas** do cursor de `GET /v2/transactions`, sem filtro de período.
4. 429: espera (`Retry-After`, ou o padrão, com teto) e tenta de novo até `SyncRateLimitRetries`; persistindo, interrompe a conta, registra o erro e **pula as demais contas**. Timeout/indisponibilidade: uma nova tentativa.
5. Aplica cada conta de forma idempotente, em uma transação do banco: insere o que é novo, atualiza o que mudou, conta o que não mudou. IDs repetidos no mesmo payload valem uma vez e são reportados (`DuplicateIdsInPayload`).
6. Só quando a coleta da conta foi **completa**, transações já conhecidas que não vieram acumulam `missing_sync_count` (e `first_missing_at`). **Nada é excluído.** Reaparecendo, a contagem volta a zero.
7. Encerra a coleta: `Completed`, `CompletedWithErrors` (alguma conta sincronizada e houve erro) ou `Failed` (nenhuma conta sincronizada e houve erro). Coletas interrompidas ficam registradas mesmo com a requisição cancelada.

Uma coleta incompleta (erro no meio das páginas, limite de páginas, cursor repetido) ainda grava o que chegou, mas nunca marca ausências.

## Classificação inicial (informativa)

Usa só campos estruturados do banco; o texto da descrição só entra na dica de "possível pagamento de fatura" no lado da conta bancária e **nunca** para ligar parcelas. O motivo fica em `classificationReason`.

| Classe | Quando |
|---|---|
| Compra de cartão | Conta de cartão, `DEBIT`, operação `PAGAMENTO`, sem parcelamento. |
| Parcela de cartão | Conta de cartão, `DEBIT`, `totalInstallments > 1` nos metadados. Não liga parcelas entre si. |
| Transferência potencial | **Pagamento da própria fatura** (`PAGAMENTO_FATURA` no cartão; débito na conta cuja descrição cita pagamento de fatura, como `PossibleCardBillPayment`), transferência na mesma instituição, resgate de aplicação, saque. Nunca é despesa de consumo. |
| Receita potencial | Conta bancária, `CREDIT`, exceto os casos acima; rendimento de aplicação. |
| Despesa potencial | Conta bancária, `DEBIT`, exceto os casos acima; tarifa de cartão. |
| Informação insuficiente | Sem tipo ou valor (ou valor zero); operação de crédito/empréstimo; créditos de cartão (estorno, cashback); operações de cartão não reconhecidas. |

Limite conhecido: pelo lado da conta bancária, um pagamento de fatura só é reconhecido se a descrição disser isso; os demais viram "Despesa potencial". Distingui-los exige conciliação, fora desta tarefa.

## Outras rotas

| Rota (após `/pluggy-poc`) | Descrição |
|---|---|
| `GET /imported-transactions` | Lista paginada (`page`, `pageSize` até 200) com filtros `connectionId`, `accountId`, `from`/`to` (aaaa-mm-dd), `status` (`Pending`/`Posted`/`Unknown`) e `classification`. Devolve também contadores de **novas** e **atualizadas** na última sincronização da conexão, **pendentes**, **ausentes** e total (os contadores ignoram os filtros de status e classificação). Cada item traz `possibleDuplicateKey` (12 primeiros caracteres do hash) e `possibleDuplicateCount`. |
| `GET /connections/{id}/sync-runs` | Últimas 20 coletas, mais recentes primeiro. |
| `GET /connections/{id}/stability` | Evidências de estabilidade (abaixo). |

Todas exigem sessão e dono do orçamento; conexão de outro orçamento responde 404.

## Diagnóstico de estabilidade

`GET /connections/{id}/stability` compara as duas últimas coletas concluídas (e acumula o histórico):

- **IDs preservados**: das transações já importadas antes da última coleta, quantas foram vistas de novo; as demais são "não vistas".
- **Novas na última** e quantas delas têm data anterior à coleta anterior (chegada tardia).
- **Mudanças**: contagem por campo alterado na última coleta; transições `PENDING → POSTED` (última e total); outras mudanças de status; alterações de valor e de descrição.
- **Datas futuras** (e quantas pendentes), **ausentes agora**, **pendentes agora** e grupos de **possível duplicidade** (mesma conta, dia, valor e descrição normalizada com IDs diferentes — pode ser repetição legítima, por isso nada é removido).

Limitações da medição: coletas incompletas inflam "não vistas"; "ID preservado" só significa que o ID reapareceu, não que o conteúdo é o mesmo (para isso há as contagens de campos alterados).

## Limitações

- A sincronização é manual e sequencial; não há agendamento.
- Sem payload bruto: se um dia for preciso reprocessar com dados que hoje não são guardados, será preciso uma nova coleta.
- Os índices garantem identidade por conexão+conta+ID; se a Pluggy reemitir a mesma movimentação com **outro ID**, ela entra como nova (aparece como possível duplicidade quando conta, dia, valor e descrição coincidem).
- Nada é conciliado com lançamentos, faturas ou previsões do Saldo360.
