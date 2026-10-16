# Painel financeiro consolidado (T15.1)

O endpoint `GET /api/families/{familyId}/financial-dashboard` reúne dados já existentes da família para uma leitura de painel. Ele não cria entidade, previsão, lançamento, orçamento ou cache.

## Contrato

Parâmetros opcionais:

- `competence=yyyy-MM` (padrão: mês atual em `America/Sao_Paulo`);
- `months` entre 1 e 12 (padrão: 6).

A resposta contém `position`, `cashFlow`, `obligations`, `budgets`, `creditCards`, `debts`, `goals`, `monthlyProjection` e `coverage`. Valores monetários são strings decimais (`"1234.56"`); valores ausentes permanecem `null`.

## Origem e semântica

- **position** usa contas e movimentos efetivos, incluindo transferências apenas no saldo de cada conta. O consolidado não trata transferência como receita. Saldo realizado e alocações de objetivos permanecem separados.
- **cashFlow** e **monthlyProjection** reutilizam `FinancialCalendarMonthlyProjectionService` (T07.5). Não incluem cenários hipotéticos nem recorrências virtuais.
- **obligations** expõe previsões e obrigações de cartão conhecidas, sem somar novamente previsões conciliadas. Dívidas usam o valor contratual declarado quando disponível.
- **budgets** reutiliza a execução orçamentária da competência; metas não são alteradas e faturas não são contadas duas vezes.
- **creditCards** informa quantidade, obrigações conhecidas, limite disponível cadastrado e cobertura de conta de pagamento.
- **debts** separa contratos ativos, parcelas estimadas no horizonte e saldo contratual declarado; a soma de parcelas não é apresentada como saldo oficial.
- **goals** reutiliza o resumo de alocações da T14.3. Valor-alvo não é tratado como dinheiro reservado.
- **coverage** indica quando cartões sem conta de pagamento, dívidas sem conta responsável ou contas sem saldo inicial tornam a leitura parcial.

## Consistência e limitações

A projeção mensal usa o snapshot `REPEATABLE READ` do motor da Agenda. As consultas cadastrais complementares são feitas pelo mesmo serviço de aplicação, mas podem observar um snapshot posterior ao da projeção; essa referência temporal é uma limitação documentada desta primeira versão. O endpoint é somente leitura, autenticado e isolado por família.

O dashboard não calcula projeções de cenários, metas de recuperação, pagamentos ou novas obrigações. A validação global da solução permanece planejada para a T15.5.
