# PROJ-04 — Valor real por competência nas despesas fixas

Uma despesa fixa recorrente usa o **valor-base** da recorrência até que o valor real de uma competência seja conhecido. O valor real substitui o projetado **somente naquele mês**; nunca soma, nunca altera a recorrência e não é pagamento.

## Prioridade do valor (por despesa e competência)

| Ordem | Origem | Onde fica | `amountSource` |
|---|---|---|---|
| 1 | **Real confirmado** | `financial_planned_transactions.confirmed_amount` (+ `amount_confirmed_at`, `amount_confirmed_by_user_id`) | `Confirmed` |
| 2 | **Ajuste da competência** | `amount` da previsão-exceção (`exception_recurrence_id`/`exception_competence`) ou da ocorrência materializada com valor diferente do base | `Adjustment` |
| 3 | **Valor-base** | `financial_recurrences.amount` (ocorrência virtual) | `Base` |

Reaproveita o mecanismo de exceção de competência que já existia; não há outro motor de recorrências. `FixedObligationReader` continua sendo a fonte única: `FixedObligation.PlannedAmount` já é o valor vigente, e ganhou `BaseAmount`, `AdjustedAmount` e `AmountSource` para exibir projetado × real.

## Confirmado ≠ pago

Confirmar o valor não vincula pagamento: a obrigação segue `Planned` (pendente, entra no fluxo de caixa pelo valor confirmado). Só o pagamento vinculado (conciliação) a torna `Realized`, e então vale o valor efetivamente pago (`ActualAmount`), nunca somado ao previsto.

## Escrita

`PUT /api/financial-workspaces/{workspaceId}/recurrences/{id}/competences/{yyyy-MM}/confirmed-amount` com `{ "amount": "748.31" }`.

- Sem previsão na competência: cria a exceção (valor-base como ajuste) e grava o confirmado. Com previsão (materializada ou exceção): só grava o confirmado. Corrigir repete a chamada: atualiza a mesma linha, sem criar outra.
- Recusado (409/validação): recorrência inativa, de cartão ou detectada pelo Pluggy; competência já paga, cancelada (mês pulado) ou em revisão; competência fechada sem previsão (o histórico não é alterado).
- Não há "desconfirmar": o valor corrige-se confirmando outro; uma previsão cancelada significaria mês pulado.

## Migration `AddCompetenceConfirmedAmount`

Aditiva: três colunas nulas, `CHECK` (os três campos juntos ou nenhum; valor positivo, até 2 casas), FK do responsável e índice dessa FK. **Nenhuma linha existente é alterada**: exceções antigas continuam sendo apenas ajustes (`Adjustment`), nunca confirmadas automaticamente. Reversível (`Down` remove as colunas). Criada localmente; não aplicada em produção nesta etapa.

## Onde vale

Todas as leituras que usavam o valor da previsão passam a usar `ConfirmedAmount ?? Amount`: Agenda (itens e totais), saldo projetado diário e mensal (e, por consequência, planejamento mensal, Plano × Realizado e recuperação do limite), soma pendente por conta e fatos de orçamento. As ocorrências virtuais (meses sem exceção) continuam no valor-base.
O "planejado original" do Plano × Realizado segue lendo o valor previsto original, de propósito.

## Pluggy

O valor confirmado entra na regra de cobertura do `PendingBank` (PROJ-03) como valor da obrigação, mas **não relaxa** a correspondência: valor igual não associa sozinho e descrição genérica continua ambígua. Exemplo de novembro/2026:

- **Crediário Solar**: recorrência-base R$ 750,00; novembro confirmado R$ 748,31; dezembro em diante R$ 750,00. O débito bancário "SAIDA CREDIARIO AUTOM" só é `Covered` se a descrição da obrigação for compatível com a do banco (hoje "Crediário Solar" × "SAIDA CREDIARIO AUTOM" continua `Ambiguous`: aparece sinalizado e fora do saldo, sem duplicar o R$ 748,31 da agenda).
- **ASTTI**: identificação original preservada; o "Pagamento de boleto DDA" genérico não é associado automaticamente.

## Interface (detalhe do compromisso na Agenda)

Mostra Projetado (valor-base), Ajuste da competência, Real confirmado, Diferença para o projetado e Pagamento (Pendente de pagamento / Pago). "Confirmar valor real" / "Corrigir valor real" abre um formulário que informa que o valor vale só para aquela competência. Ficam fora desta etapa: tela de recorrências com histórico por mês e exclusão do valor confirmado.
