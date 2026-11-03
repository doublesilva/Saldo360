# PROJ-02 — Cartões de crédito via Pluggy

Levantamento e primeira lacuna: **prévia somente leitura do cartão antes do vínculo**. Nada foi importado, vinculado nem criado em produção; as consultas à Pluggy foram só `GET` (mais a autenticação).

## Situação em produção (leitura)

- 3 vínculos Pluggy, todos de conta bancária. **Nenhum vínculo de cartão**; 0 cartões, compras, faturas e pagamentos de fatura; 0 linhas em `pluggy_card_bills`.
- As sincronizações já gravaram 1.152 transações de 5 cartões em `pluggy_imported_transactions` (cru, sem efeito financeiro). Os cartões só passam a existir no Saldo360 quando o usuário faz o vínculo (`POST /pluggy/links`).

## O que a Pluggy devolve (consulta ao vivo, 5 cartões)

| Dado | Disponível? |
|---|---|
| Limite, limite disponível, bandeira, dia de vencimento do saldo | Sim, nos 5 cartões |
| Data de fechamento (`balanceCloseDate` da conta) | **Não** (nula nos 5); só aparece na fatura (`closingDate`) quando o banco a informa |
| Faturas (`/bills`) | Sim: 12 por cartão, até a de 10/2026; a fatura aberta de 11/2026 ainda não existe |
| Parcelas e compras futuras | Sim, como transações `PENDING` com data futura (4 cartões; Itaú Empresas e Iti não têm parcelas) |

Particularidades: faturas com frações de centavo (cartão "company", até 4 casas) e com total negativo (crédito). O total da fatura nunca é arredondado para somar: a prévia sinaliza `FractionalBillCents` e `CreditBill`.

## Prévia (`GET …/pluggy-integration/connections/{id}/accounts/{pluggyAccountId}/card-preview`)

`PluggyCardPreview.Build` (função pura) + `PluggyAccountLinkService.PreviewCardAsync`. Resume limite, usado, vencimento, últimas 12 faturas, contagem por status, parcelas e **cobranças já conhecidas por mês** (pendentes ou com data futura, só valores positivos). Sinais: `NoBillsReturned`, `AwaitingBankBill` (última fatura já venceu), `NoClosingDate`, `FractionalBillCents`, `CreditBill`, `TransactionsTruncated`.

Garantias: não grava nada (teste compara contagens de vínculos, cartões, compras, faturas, lançamentos, postings e faturas Pluggy antes e depois); não expõe descrição nem número do cartão; só vale para conta de cartão do Item do orçamento (400 para conta bancária, 404 para conta desconhecida ou outro orçamento). A conta externa nunca é confiada ao navegador.

As cobranças por mês são **o que o banco já informou**, não a fatura nem a meta: ausência de cobrança futura não significa gasto zero.

## Reaproveitado (nada reconstruído)

`PluggyAccountLinkService` (vínculo e conta pagadora), `PluggyPostingEngine`, `PluggyFinancialMapper`, `CardProjectionEngine`/`CardInstallmentProjector` (T09.4), `PluggyCardSchedule`, meta "Todos os cartões" (R03) e a projeção integrada da PROJ-01.

## Pendências

1. Vínculo dos 5 cartões em produção (ação do usuário, com conta pagadora e data de início por cartão).
2. Meta consolidada de R$ 5.000 lado a lado com a obrigação dos cartões na visão integrada.
3. Interface da prévia (hoje só a API).
