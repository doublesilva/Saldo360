# Backlog (itens decididos, ainda não implementados)

Registro curto. Cada item traz o motivo, o que foi observado no código e a proposta mínima. Nada aqui altera saldos realizados.

## 1. Padronização semântica de receitas e estornos entre telas

**Divergência observada (OBSERVADO no código, 2026-10-31).**

| Tela / endpoint | Como calcula "entradas" e "receitas" | Devolução bancária (origem `PluggyRefund`) |
| --- | --- | --- |
| Contas — `GET /accounts/flows`, `GET /accounts/{id}/balance` (`SumMovementsAsync`) | `income` = todo lançamento do tipo receita não cancelado; `expense` = todo do tipo despesa | entra em `income` (conceito de caixa/saldo) |
| Visão Geral — "Receitas do mês" e "Despesas do mês" (`FinancialTransactionKpi`) | Receita econômica e consumo líquido | fora das receitas; abate a despesa (consumo) |
| Agenda — resumo (`realizedIncome`, `realizedExpense`, `realizedRefunds`) | Mesma regra da Visão Geral (`FinancialTransactionKpi`) | fora das receitas, abate a despesa, e é informada à parte em `realizedRefunds` |
| Orçamento / metas | Consumo líquido (fatos de orçamento): a devolução é um fato negativo na categoria e na natureza da despesa devolvida | abate o consumo no mês do crédito |
| Lançamentos | Lista bruta (a devolução é um lançamento `Income` com `origin=PluggyRefund`) | aparece como crédito identificado ("Devolução (não é receita)") |
| Visão Geral — bloco "Entradas e saídas do mês" | **Caixa** (corrigido em 2026-10-31): total de entradas = todos os créditos efetivos da conta + transferências recebidas; total de saídas = todos os débitos efetivos + transferências enviadas; devoluções e transferências sempre à parte | devolução em "Devoluções e estornos", fora de "Receitas de fontes externas" |

**Conclusão.** Os saldos e os totais de CAIXA não divergem (o saldo soma o crédito da devolução uma vez). A divergência é de CLASSIFICAÇÃO: a tela Contas chama de `income` o que a Visão Geral e a Agenda separam em receita × devolução. Havia uma divergência de total de caixa no bloco "Entradas e saídas do mês" (usava os indicadores líquidos); foi corrigida e coberta por teste (`RefundsAreCashCreditsInTheOverviewTotals…`).

**Proposta mínima (não implementada).** Em `GET /accounts/flows` expor `refunds` por conta (aditivo) e tornar `income` = receitas sem devolução, de modo que `totalInflows = income + refunds + transfersReceived` e `totalOutflows = expense (bruta) + transfersSent`, com a mesma consulta `FinancialTransactionKpi` das outras telas. Regra de nomenclatura: estorno e devolução nunca são "receita econômica"; entram em entradas de caixa.

## 2. Backlog atualizado: pendentes de decisão × ignoradas

O aviso "N movimentações aguardam revisão" conta `NeedsReview` e também `Ignored` (decisão "Não contabilizar" ou lançamento cancelado, exceto os anteriores ao início da importação). A fila "Movimentos para revisar" lista só o que depende de decisão. Proposta: expor em `coverage` (aditivo) `pendingDecisionCount` e `ignoredByUserCount`, sem mudar `reviewCount`, que é compartilhado com a conciliação de saldo; e ajustar o texto do aviso para citar as duas contagens.

## 3. Seletor de agrupamento PF/PJ

O seletor da Visão Geral só tem "principal", "todas" e uma conta. `TransferPerspective` já aceita qualquer conjunto de contas; falta expor perspectivas "só PF" e "só PJ" (conjuntos por `FinancialScope`) no plano e no overview, com a mesma regra: transferência entre contas do conjunto é neutralizada; a que cruza a fronteira é entrada ou saída.

## 4. Natureza de pró-labore e distribuição de lucros

Não existe no modelo (verificado). Quando for criada, deve ser uma natureza/finalidade do lançamento bancário já existente, sem segunda contabilização, sem classificação fiscal e sem confundir entrada de caixa com faturamento. Não implementar antes da decisão de produto.

## 5. Transferências previstas

Em andamento (ver `docs/T16-transferencias-previstas.md`).
