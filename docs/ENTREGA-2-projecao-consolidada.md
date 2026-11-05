# Entrega 2 — Projeção financeira mensal consolidada (Atual e Tetos)

`GET /api/financial-workspaces/{id}/monthly-plan/projection?from=yyyy-MM&months=1..36` — somente leitura (a provisão de alimentação, abaixo, traz uma migration aditiva). Não há motor paralelo: a projeção compõe a visão integrada (Agenda → projeção mensal → receita PJ e repasse da Entrega 1 → cartões → cheque especial) e acrescenta o cenário Tetos.

## Consistência

- Uma data de referência por consulta; saldo inicial do primeiro mês vem da Agenda, que acumula desde a âncora bancária (consulta que começa em mês futuro não recomeça do zero).
- Repasse PJ → PF usa janela de proteção determinística (Entrega 1); o horizonte do repasse acompanha a consulta, mas o resultado de uma data não depende dele. Testes de API comparam 6, 13 e 24 meses.
- `saldoFinal = saldoInicial + entradas − saídas`; o fechamento de um mês é a abertura do seguinte.

## Cenários

- **Current:** o que a Agenda já conhece (previsto, recorrências, parcelas, faturas, receita PJ e repasse).
- **Ceilings:** parte do Atual e, por competência, `complemento = max(0, teto − consumo)`. Consumo acima do teto é preservado e sinalizado (`CardsCeilingExceeded`, `OutsideCardsCeilingExceeded`). Tetos: meta de "Todos os cartões" e meta total do orçamento; fora dos cartões = total − cartões (sem schema novo). Sem metas definidas, nada é complementado (`CeilingTargetsNotDefined`), nunca meta zero.
- **Caixa × orçamento:** o complemento de cartões entra no orçamento na competência da fatura, mas só reduz o saldo no mês do vencimento da fatura (ou no mês seguinte quando não há fatura conhecida); o de fora dos cartões reduz o saldo no próprio mês. O efeito é acumulado nos saldos posteriores.

## Cheque especial

`usado = max(0, −saldo)`; `disponível = max(0, limite − usado)`; recuperação mensal = uso na abertura − uso no fechamento (pode ser negativa); acumulada = uso na abertura do primeiro mês consultado − uso no fechamento. Saldo positivo aparece à parte (`positiveBalance`): de −3.000 para +2.000 a recuperação é 3.000.

## Provisão de alimentação (parâmetro do orçamento)

Tabela `financial_food_provisions` (migration `AddFoodProvision`, aditiva): linha padrão do orçamento (valor mensal, inicialmente R$ 2.000, e categorias alimentares **por identificador**) e linhas de ajuste por competência. API: `GET|PUT monthly-plan/food-provision`, `PUT|DELETE monthly-plan/food-provision/months/{yyyy-MM}`.

- Sem categorias configuradas, usam-se as categorias de despesa cujo nome contém "aliment" (sinal `FoodCategoriesInferred`); sem nenhuma, a provisão não é aplicada (`FoodProvisionCategoriesNotConfigured`).
- Estimativa = `max(0, provisão − alimentação já reconhecida nas faturas da competência)` (parcelas ativas de compras com categoria alimentar). Vale do mês corrente em diante, entra no consumo de cartões da competência e reduz o saldo só no vencimento da fatura.
- Está dentro do teto de cartões: no cenário Tetos o complemento é calculado depois da alimentação, então o caixa total até o teto nunca soma os dois.
- Limitação: compras pendentes do Pluggy ainda sem categoria não contam como reconhecidas (a estimativa pode ficar maior que o necessário; sinal `FoodKnownFromCategorizedPurchasesOnly`).

## Juros e IOF

Encargos já lançados pelo Pluggy seguem nos movimentos. Nenhuma taxa futura é inventada; mês com abertura ou fechamento negativo recebe o sinal `OverdraftChargesNotEstimated` (e a nota geral da projeção), logo não é "completo".

## Cartões além de 12 meses

Origem: cobranças recorrentes detectadas eram projetadas por 7 meses (`CardProjectionRepository`) e o endpoint de obrigações de cartão limitava 12 meses. Agora a recorrência vai até 37 meses (recortada pelo período pedido) e o endpoint aceita até 36. Parcelas conhecidas já eram projetadas até o fim da cadeia; compras futuras nunca são inventadas.

## Limitações conhecidas

- Sem interface nesta entrega.
