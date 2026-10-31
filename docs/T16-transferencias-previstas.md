# Transferências previstas

Planejar transferências futuras entre contas próprias e levar o efeito aos saldos projetados e à Agenda. Nada é publicado ou implantado; a migration é aditiva e não foi aplicada a nenhum banco real.

## Decisão de modelagem

| Alternativa | Avaliação |
| --- | --- |
| **A. Nova entidade `FinancialPlannedTransfer` (escolhida)** | Origem, destino, valor, data, descrição, cancelamento e associação à transferência realizada em uma tabela própria e aditiva (`financial_planned_transfers`). Não mexe nos contratos existentes de previsão e de transferência. |
| B. Estender `FinancialPlannedTransaction` com tipo Transferência | A previsão exige categoria (FK do mesmo tipo Income/Expense) e se realiza em um lançamento. Transferência não tem categoria nem é receita/despesa; exigiria afrouxar constraints e quebrar premissas de todos os consumidores. Rejeitada. |
| C. Reutilizar `PlanLineKind.Transfer` (`FinancialPlanLine`) | A linha de planejamento é uma META mensal (competência) que "nunca altera saldo" e não tem ciclo de vida cancelada/realizada nem associação. Rejeitada. |

Risco: médio-baixo. Tabela nova, endpoints novos, campos novos e opt-in nas respostas existentes; sem dados anteriores, a projeção atual não muda.

## Comportamento

- **Situação:** Prevista (`Planned`), Vencida (`Overdue`: prevista com data anterior a hoje), Realizada (`Realized`: associada a uma `FinancialTransfer`) e Cancelada (`Cancelled`).
- **Projeção (backend):** `financial-calendar/balances` soma a saída na origem e a entrada no destino das previsões pendentes (as vencidas contam como as de hoje). Numa conta entra só a ponta dela; no consolidado entra o líquido por dia, então as duas pontas se anulam. O saldo realizado não muda.
- **Agenda:** com `includeTransfers=true` as duas pontas aparecem como itens `Source=Transfer` com situação Pendente/Vencido e a ação `CancelPlannedTransfer`; o resumo traz `plannedTransfersReceived/Sent` à parte de receitas/despesas previstas. A transferência realizada continua aparecendo como realizada.
- **Cancelamento:** `POST .../planned-transfers/{id}/cancel` (idempotente; previsão já realizada responde 409).
- **Associação com a transferência realizada:** automática só quando inequívoca: mesma origem, mesmo destino, mesmo valor, até 3 dias de distância, UMA transferência candidata para a previsão e UMA previsão candidata para a transferência (`PlannedTransferReconciler`, executado ao criar/cancelar transferência manual, ao criar a previsão e no fim de cada passada do motor Pluggy). Em qualquer ambiguidade nada é associado: a listagem traz as candidatas (até 7 dias) e o usuário confirma (`link`) ou desfaz (`unlink`). Transferência cancelada libera a previsão, que volta a projetar.
- **Sem duplicidade:** previsão associada deixa de projetar (o realizado conta a transferência uma vez); índice único impede duas previsões para a mesma transferência.
- **Rastreabilidade:** `RealizedTransferId`, origem da associação (`Inferred`/`UserConfirmed`), data da associação, criador e cancelamento ficam gravados; nada é apagado.

## Contratos (aditivos)

`POST|GET /planned-transfers`, `POST /planned-transfers/{id}/cancel|link|unlink`; `financial-calendar?includeTransfers=true` (itens `Source=Transfer`, `plannedTransferId`) e `summary.plannedTransfers*`. Migration `20261031213935_AddPlannedTransfers` (CreateTable; o `Down` só remove a tabela nova). O reset do orçamento passou a incluir a tabela (inventário por modelo; contagem 51 → 52).

## Pendências

- O fechamento projetado por MÊS no plano mensal usa os mesmos saldos da Agenda, mas as linhas de planejamento (`PlanLineKind.Transfer`) continuam sendo metas e não geram previsão.
- Previsão recorrente de transferência não existe (só avulsa).
- A associação automática não considera descrição; só contas, valor e data.
