# PROJ-03 — PendingBank na projeção de caixa

Débitos bancários **pendentes** da Pluggy (ainda não efetivados) não existem como lançamento nem entravam na Agenda. Entravam só no consumo mensal (Plano × Realizado e planejamento
mensal), por isso o consumo projetado ficava acima das obrigações conhecidas e o saldo projetado, sem esses débitos. Em 11/2026 a diferença era R$ 1.026,91.

## Regra (`PendingBankCoverage`, `Saldo360.Application.Calendar`)

Cada débito pendente em BRL, de conta bancária integrada e posterior ao início da importação do vínculo, recebe um estado:

| Estado | Critério | Efeito |
|---|---|---|
| **Covered** | Par único e seguro, nos dois sentidos, com uma obrigação já na agenda (`FixedObligationMatcher`: mesma conta, tipo, descrição compatível, janela e valor esperado) | Adicional zero |
| **Uncovered** | Nenhuma candidata plausível (mesma conta, até 3 dias, valor até 10% de diferença) | Soma **uma vez** na agenda (saldo diário, mensal, consumo) |
| **Ambiguous** | Há candidata(s) sem par seguro | **Não** soma; fica visível (`coverage.pendingBankAmbiguous`, razão `PendingBankAmbiguous`, componente `PendingBankAmbiguous`) |
| **Ignored** | Valor não positivo | Nenhum |

- Candidatas: obrigações fixas e previsões avulsas pendentes (confirmam e detectam), parcelas de dívida, transferências previstas e faturas de cartão da conta pagadora (só detectam ambiguidade, nunca confirmam).
- Valor igual nunca confirma sozinho; uma obrigação confirma no máximo um débito e sai da lista de candidatas dos demais; dois débitos iguais para uma obrigação ficam ambíguos.
- Cancelado, vinculado, em revisão ou já pago deixa de ser obrigação pendente pela mesma regra única de pendência da agenda. Efetivado: o débito deixa de ser PENDING e passa a existir como lançamento realizado (nunca os dois).
- Nada é gravado: os registros importados não mudam e a classificação é recalculada a cada leitura.

## Onde a regra vale (uma só fonte)

`FinancialCalendarBalanceRepository` classifica e acrescenta os Uncovered às pendências do snapshot; a projeção diária, a mensal (`FinancialCalendarMonthlyProjectionMonth.PendingBankUncovered/Ambiguous`),
o planejamento mensal (`monthly-plan/six-months`), o Plano × Realizado e a projeção integrada leem esse mesmo resultado. O consumo separa os Uncovered da agenda só para exibi-los como componente próprio
(`PlanActualCalculator.Consumption`): o total não muda e nada é somado duas vezes.

Contratos aditivos: `coverage.pendingBankAmbiguous` (mês), `integrated.months[].pf.pendingBankUncovered/pendingBankAmbiguous`, razão `PendingBankAmbiguous` em `coverage.reasons`/`quality`.

## Novembro/2026 (dados de referência de produção, leitura)

| Débito pendente (Itaú PF) | Valor | Compromisso na agenda | Estado |
|---|---:|---|---|
| 11/11 Débito automático DA DMAE | 138,60 | Previsão "DMAE — novembro/2026", 11/11, 138,60 | **Covered** |
| 06/11 SAIDA CREDIARIO AUTOM | 748,31 | Recorrência "Crediário Solar", 06/11, 750,00 | **Ambiguous** |
| 10/11 Pagamento de boleto DDA | 140,00 | Recorrência "ASTTI", 10/11, 140,00 | **Ambiguous** |

- Adicional financeiro comprovado: **R$ 0,00** (saldo projetado preservado; não é projeção integralmente validada).
- Ambíguo: **R$ 888,31**, exposição adicional potencial se nenhuma das duas recorrências for o mesmo débito.
- Consumo projetado: de R$ 10.681,02 para R$ 9.654,11 (sai o R$ 138,60 do DMAE, que já estava na agenda e era contado duas vezes, e os R$ 888,31 ambíguos, que passam a ser sinalizados).
- Os R$ 888,31 só viram obrigação adicional se o usuário ajustar as recorrências para casarem com o débito bancário (valor e descrição) ou lançar o compromisso correspondente.

## Limitações

- A confirmação é conservadora de propósito: recorrência de valor fixo diferente do débito (R$ 750,00 × R$ 748,31) ou descrição bancária genérica ("boleto DDA") continuam ambíguas.
- Cartão (faturas) e dívidas só entram como candidatas; um pagamento pendente de fatura sem fatura equivalente na agenda seria Uncovered.
- Não houve recálculo em produção: os valores de novembro vêm das regras sobre os dados lidos (sem sync, sem escrita).
