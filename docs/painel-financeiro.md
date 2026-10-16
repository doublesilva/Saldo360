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

## Indicadores e alertas (T15.2)

Além das seções-base, a resposta inclui `indicators` e `alerts`.

Os indicadores separam saldo realizado, fechamento projetado, variação em relação ao saldo realizado, fluxo líquido realizado/pendente, comprometimento conhecido e disponibilidade não alocada dos objetivos. A série mensal reutilizada informa a tendência como `Improving`, `Stable`, `Deteriorating` ou `Unknown`. A tendência é `Unknown` quando há menos de dois meses comparáveis, fechamento ausente ou cobertura incompleta; variações de até R$ 0,01 são estáveis.

Alertas são somente dados de consulta. Cada alerta possui código estável, severidade (`Info`, `Warning` ou `Critical`), texto, referência temporal, valor e identificador estruturado quando aplicável. A ordenação é severidade, data, código e identificador. São gerados para saldo realizado negativo, risco de fechamento projetado negativo, obrigações vencidas, orçamento acima da meta, orçamento a partir de 85% e cobertura incompleta. Um orçamento acima da meta não recebe também o alerta de aproximação.

O próximo compromisso é escolhido entre os itens pendentes da Agenda pela menor data e identificador. A capacidade antes do próximo recebimento permanece `Unknown` quando o saldo diário anterior não puder ser demonstrado com segurança; nenhuma data ou renda é presumida.

Alertas não são persistidos, enviados ou deduplicados por descrição/valor/data. Previsões conciliadas continuam seguindo a identidade estruturada da Agenda. Consultas complementares podem observar snapshot posterior ao da projeção mensal, limitação já indicada na seção anterior.
