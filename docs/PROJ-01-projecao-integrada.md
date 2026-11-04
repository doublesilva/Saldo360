# PROJ-01 — Projeção financeira integrada de seis meses

Receita PJ por horas, disponibilidade para repasse, transferências PJ → PF, saldo da conta PF e recuperação do limite. Reutiliza a projeção mensal da Agenda
(`FinancialCalendarMonthlyProjectionService`), as transferências previstas (`FinancialPlannedTransfer`), as despesas fixas consolidadas e `OverdraftRecoveryRule`.
Nada é gravado em leitura; nenhum lançamento, previsão avulsa ou snapshot é criado.

## Receita PJ (estimativa parametrizada)

- Configuração por orçamento (`financial_income_forecasts`) e ajustes por competência trabalhada (`financial_income_forecast_months`). **Só entra na projeção quando aprovada** (`isActive`).
- `ReceitaBruta = ValorHora × HorasDia × DiasFaturáveis`. Dias úteis: segunda a sexta, menos feriados de São Paulo/SP (nacionais, Sexta-feira Santa, 9/7 e 25/1) e os
  dias cadastrados em "não trabalhados". Carnaval e Corpus Christi são ponto facultativo: ajustar os dias faturáveis da competência.
- Mês trabalhado ≠ mês de recebimento: recebimento = competência + deslocamento (padrão 1 mês) no dia configurado (31 vira o último dia do mês). Valor, dias e data podem ser corrigidos por competência; a previsão pode ser descartada.
- A receita **realizada** na conta de recebimento no mês do recebimento, igual ou acima de 50% do esperado, substitui a estimativa (nunca as duas).
- A estimativa entra como entrada pendente na data do recebimento, na conta de recebimento (Agenda, indicadores e seis meses leem do mesmo carregador: `IncomeForecastLoader`).

> **PROJ-06:** sem configuração a receita é inferida do histórico e o repasse PJ → PF é projetado automaticamente (só planejamento); veja [PROJ-06](PROJ-06-previsao-receita-pj-repasse.md). O "repasse sugerido" abaixo passa a ser o residual depois do repasse projetado.

## Disponibilidade PJ

- `ReservaTributos = Receita do mês × 11%` (percentual configurável). **Não é débito** do saldo bancário e não se aplica quando existe despesa tributária cadastrada na conta (DAS, Simples, imposto, DARF…).
- `Disponível = SaldoPJProjetado − ReservaTributos − ReservaOperacional`. O saldo projetado já é líquido das despesas PJ pendentes e dos repasses previstos, então `Saldo antes das despesas − Despesas pendentes − Tributos − Reserva` é a mesma conta: as despesas não são subtraídas duas vezes.
- Repasse sugerido = `max(0, Disponível)` truncado em centavos. É só sugestão: não cria transferência.

## Transferências PJ → PF e conta PF

- A transferência prevista reduz a PJ e aumenta a PF; é neutra no consolidado. Associada a uma transferência realizada (inclusive a inferida do Pluggy), deixa de projetar: o realizado conta uma vez.
- `SaldoFinalPF = SaldoInicial + EntradasPendentes − SaídasPendentes` (inclui repasses previstos). Despesas PF: fixas, cartões existentes e demais compromissos.
- Limite: `Utilizado = max(0, −Saldo)`, `Recuperação = UtilizadoInicial − UtilizadoFinal`, `Disponível = max(0, Limite − Utilizado)`; o excesso é mostrado à parte. Meta já gravada (`overdraft_recovery_snapshots`) é preservada; sem ela, a meta provisória é calculada sem gravar.

## Cobertura

Sem cartões cadastrados, sem previsão de receita aprovada ou com fontes incompletas, o mês é **parcial** (`NoCardsRegistered`, `IncomeForecastNotConfigured`, `CardCoverageIncomplete`…). Metas de orçamento seguem separadas do saldo.

## API

`GET|PUT /monthly-plan/income-forecast`, `PUT|DELETE /monthly-plan/income-forecast/months/{yyyy-MM}`; `GET /monthly-plan/six-months` devolve o bloco `integrated`.
