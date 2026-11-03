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
2. Cadastrar a meta "Todos os cartões" (R$ 5.000,00) nos orçamentos dos meses futuros: sem ela o mês mostra "Sem meta definida".
3. Conferir, depois do vínculo e da primeira sincronização, o ciclo (fechamento) de cada cartão e as faturas de 11/2026.

## Meta consolidada "Todos os cartões" na visão integrada

Reutiliza a meta única `AllCards` do orçamento (R03, editável no Orçamento) e `PlanActualCalculator.CardTarget`/`Slice` (a mesma fonte do Dashboard). `IntegratedMonth.cards` traz, por competência da fatura:

| Campo | Regra |
|---|---|
| `target` | meta mensal única de todos os cartões; nula se o orçamento do mês não a define (`plannedState = NotDefined`) |
| `confirmed` | consumo realizado: faturas fechadas pelo banco (total bancário) + composição já lançada |
| `pendingKnown` | compras pendentes e parcelas já devolvidas pelo banco, ainda não faturadas |
| `estimated` | parcelas estimadas só com evidência (nunca gravadas) |
| `projected` | `confirmed + pendingKnown + estimated` |
| `remaining` / `excessProjected` | `max(0, meta − projetado)` / `max(0, projetado − meta)` |

- A meta é de consumo por competência: **não** entra no saldo bancário nem vira fatura prevista. O desembolso da fatura continua em "Cartões (faturas existentes)" na conta pagadora, pelo vencimento.
- Pagamento de fatura e transferências bancárias não consomem a meta (o consumo vem das compras/parcelas pela fatura, uma vez).
- Sem cartões vinculados: `applicable = false`, `state = Unavailable`, razão `NoCards`; a tela mostra "Sem cartões vinculados" (nunca R$ 0,00) e o mês fica parcial (`NoCardsRegistered`).
- `PlanActualService.BuildCardTarget` passou de `private` a `internal` e recebe só a meta (`decimal?`), sem mudar o resultado do Dashboard.

## Interface da prévia ("Cartões Pluggy")

Nova aba em Cartões: lista só cartões das conexões, com instituição, nome, final mascarado e status (**disponível**, **já vinculado**, **indisponível**), e carrega a prévia sob demanda. Mostra apenas o que o banco informa, com "Não informado pelo banco" no que faltar; as cobranças futuras aparecem como "o que o banco já conhece", nunca como fatura ou meta. A tela não tem ação de vínculo e só faz `GET`.

## Preparação do vínculo dos cartões reais (sem aplicar nada em produção)

**Conta pagadora obrigatória.** O vínculo de um cartão novo exige `paymentAccountId` no formulário e na API (`POST /pluggy-integration/links`, modo `CreateNew`). O formulário oferece só contas bancárias **ativas** do orçamento, não escolhe nenhuma sozinho e preserva fechamento, vencimento e limite sugeridos pela Pluggy. O backend valida que a conta pertence ao orçamento, está ativa e é corrente/poupança; sem ela devolve 400 e nada é criado.

**Mapeamento definido pelo usuário** (nunca inferido pelo nome do cartão):

| Cartão (Pluggy) | Conta pagadora |
|---|---|
| Itaú Empresas Visa | Itaú PJ |
| Personnalité Mult Black | Itaú PF |
| Credicard On Mastercard | Itaú PF |
| Iti Itaú Visa Platinum | Itaú PF |
| Company | **pendente de classificação** (PF ou PJ) |

**Data inicial de importação.** Proposta: `2026-10-01` para os cinco cartões, editável na aba "Cartões Pluggy"; nunca é ampliada para 10/2025 sem decisão do usuário. A prévia (`?importFrom=`) mostra: lançamentos que ficam de fora; compras parceladas feitas antes do corte e quantas parcelas delas caem dentro da janela (essas parcelas chegam do banco como movimentos futuros); e faturas a partir do corte que incluem lançamentos anteriores a ele (nesses casos só o total do banco vale e a composição local fica incompleta: sinal `BillsSpanCutoff`).

**Meta "Todos os cartões" (R$ 5.000,00 por mês), a aplicar depois, pelo usuário**, por competência, no Orçamento: criar o orçamento do mês (`POST /budgets` com `competence`) e adicionar a meta (`POST /budgets/{id}/targets` com `groupingType = AllCards` e `plannedAmount = 5000.00`). É uma única meta por orçamento (a API recusa a segunda e recusa meta por cartão). Ela é meta de consumo, não fatura.

**Roteiro de vínculo controlado, um cartão por vez:** (1) abrir a aba "Cartões Pluggy" e conferir a prévia com o corte; (2) vincular o cartão na tela de contas, escolhendo a conta pagadora; (3) aguardar a sincronização e conferir fatura aberta/fechada, parcelas e ciclo; (4) só então o próximo cartão.
