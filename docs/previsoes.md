# Previsões e realização

Uma **previsão** (`financial_planned_transactions`) é uma receita ou despesa que se espera receber ou pagar em `due_date`. Uma previsão pendente não entra no saldo realizado. Ela só passa a contar quando é **realizada**: nesse momento é criado um lançamento efetivo (`financial_transactions`) vinculado a ela.

Previsões pendentes, vencidas ou não, entram no [saldo projetado](saldo-inicial.md#saldo-projetado), que nunca as conta junto com o lançamento efetivo.

## Situação: como Pending, Realized e Cancelled são determinados

A situação **não é gravada**. Ela é derivada a cada leitura, de fontes que não podem divergir:

| Situação | Regra |
| --- | --- |
| `Cancelled` | `financial_planned_transactions.is_cancelled = true` |
| `Realized` | não cancelada **e** existe lançamento com `planned_transaction_id = id` e `is_cancelled = false` |
| `Pending` | nenhum dos dois |

Consequências:

- **Reabertura automática.** Cancelar o lançamento efetivo devolve a previsão a `Pending` na mesma operação. Não existe outra coluna para atualizar.
- **Histórico preservado.** Lançamentos cancelados continuam vinculados e com auditoria completa. Uma previsão pode ter vários lançamentos cancelados e **no máximo um** ativo.
- **Vencida.** "Vencida" também é derivado, só na tela. Vale apenas para `Pending` com `due_date` anterior a hoje em America/Sao_Paulo.

A API devolve `status` e, quando `Realized`, `realization: { transactionId, amount, transactionDate }`. Nesse caso, `amount` e `dueDate` da previsão continuam sendo os **previstos**.

## Realizar

`POST /api/families/{familyId}/planned-transactions/{id}/realize` com corpo `{ amount, transactionDate }`. Nenhum outro campo é aceito.

- O lançamento efetivo usa a **conta, a categoria, o tipo e a descrição da previsão**, com o valor e a data informados.
  - O valor pode diferir do previsto e segue as regras Money.
  - A data não pode ser futura em America/Sao_Paulo.
- **Previsão inalterada.** O valor e a data previstos não mudam. O autor do lançamento é quem realizou a previsão.
- **Conta e categoria ativas.** Se uma delas estiver inativa, a resposta é `400`, orientando a editar a previsão antes.
- **Saldo.** Não é exigido saldo suficiente. Se a data efetiva for anterior à data de referência do saldo inicial, vale a regra existente: o lançamento fica no histórico, mas não entra no saldo (ver [saldo-inicial.md](saldo-inicial.md)).

Respostas:

| Situação | Resposta |
| --- | --- |
| Criou o lançamento | `201` com o lançamento |
| Já realizada com o **mesmo** valor e data | `200` com o lançamento existente; nada é criado nem alterado |
| Já realizada com **outro** valor ou data | `409` |
| Previsão cancelada | `409` |
| Valor ou data inválidos, conta ou categoria inativas | `400` |
| Fora do acesso | `404` |

## Regras de edição e cancelamento

- Uma previsão **realizada** não pode ser editada nem cancelada: a resposta é `409`. Para corrigir, cancela-se o lançamento efetivo.
- Um lançamento **vinculado** não pode ser editado: a resposta é `409`. Para corrigir, cancela-se o lançamento, o que reabre a previsão, e realiza-se de novo.
- Lançamentos sem vínculo continuam como antes.

## Concorrência e atomicidade

1. **Bloqueio da previsão.** Estas operações rodam numa transação de banco que começa com `SELECT … FROM financial_planned_transactions WHERE id = … FOR UPDATE`:
   - realizar;
   - editar a previsão;
   - cancelar a previsão;
   - cancelar um lançamento vinculado.

   Elas ficam em fila para a mesma previsão. Depois de obter o bloqueio, a previsão e o lançamento ativo são relidos e já refletem o que a operação anterior gravou. Os dois resultados possíveis são:
   - realizar antes de cancelar → o cancelamento da previsão recebe `409`;
   - cancelar antes de realizar → a realização recebe `409`;
   - realizar antes de editar → a edição recebe `409`;
   - editar antes de realizar → a realização usa os dados novos.
2. **Atomicidade.** Criar o lançamento e vinculá-lo à previsão é um único `INSERT`, com `planned_transaction_id`, dentro dessa transação. Cancelar o lançamento vinculado é um único `UPDATE`, e a reabertura é derivada dele.
3. **Garantia no PostgreSQL.** O índice único parcial `ux_financial_transactions_active_realization ON financial_transactions (planned_transaction_id) WHERE planned_transaction_id IS NOT NULL AND NOT is_cancelled` impede dois lançamentos ativos para a mesma previsão, mesmo fora do caminho da aplicação. Se a aplicação perder uma corrida contra ele, a violação é capturada e a realização é reexecutada uma vez. Na segunda tentativa, ela devolve o lançamento existente ou `409`.
4. **Repetições.** Com o mesmo valor e data, a realização devolve o lançamento existente sem tocar na auditoria. Cancelar de novo um lançamento antigo, já cancelado, não altera nada e não afeta uma realização nova.

## Persistência

- `financial_transactions.planned_transaction_id` aceita nulo, e lançamentos antigos ficam sem vínculo. A FK composta `(family_id, planned_transaction_id) → financial_planned_transactions (family_id, id)` garante que o vínculo é com uma previsão da **mesma família**. Ela é `RESTRICT`: a previsão com lançamentos não é apagada por cascata.
- Previsões, lançamentos e seus vínculos nunca são excluídos fisicamente.
