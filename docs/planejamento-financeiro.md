# Planejamento financeiro por cenários (T12.1)

Um cenário é uma coleção de hipóteses de planejamento pertencente a uma família. Ele não é uma transação, previsão, orçamento, recorrência ou obrigação de cartão. Nesta etapa o Saldo360 cadastra e valida os parâmetros; nenhum saldo, Agenda, orçamento ou limite é calculado ou alterado.

## Cenário e horizonte

Cada cenário possui nome, descrição opcional, competência inicial (`yyyy-MM`) e horizonte de 1 a 12 meses. O nome é único dentro da família. Cenários podem começar em qualquer competência futura ou passada. O arquivamento é lógico e preserva o histórico; cenários arquivados não recebem novos parâmetros.

## Parâmetros mensais

Os parâmetros são explícitos por competência, sem cópia automática de defaults ou herança nesta etapa:

- `expectedAdditionalIncome`: receita adicional hipotética;
- `expectedAdditionalExpense`: despesa adicional hipotética;
- `contingencyReserve`: margem de segurança, que não é despesa efetiva;
- `recoveryTargetPercentage`: percentual entre 0 e 100, reservado para o futuro cálculo de recuperação;
- `notes`: observação da hipótese.

Os três valores monetários são não negativos e aceitam até duas casas decimais. Percentuais também são decimais exatos. O cliente deve enviar todos os campos explicitamente; zero deve ser representado por `"0.00"`, distinguindo ausência de um valor zero. Uma competência possui no máximo um registro por cenário; o `PUT` é uma atualização idempotente.

Os parâmetros são ajustes hipotéticos em relação à projeção-base. Não existe deduplicação por descrição, data ou valor e nenhum valor do sistema real é somado automaticamente. A semântica de aplicação aos saldos será definida na T12.2/T12.3.

## Escopo e transferências

O cenário é isolado por família e pode coexistir com outros cenários na mesma competência. Contas PF/PJ e transferências não são materializadas nem reinterpretadas nesta tarefa. A seleção de contas e a consolidação entre escopos serão definidas no motor futuro; por enquanto a entidade não cria referências financeiras adicionais.

## REST

`GET /api/families/{familyId}/planning-scenarios?includeArchived=false`

`POST /api/families/{familyId}/planning-scenarios`

Corpo: `{ "name": "Realista", "description": "...", "startMonth": "2026-11", "months": 6, "recoveryAccountId": "uuid-opcional" }`. A conta-alvo deve pertencer à família e estar ativa; o servidor nunca escolhe uma conta automaticamente.

`GET /api/families/{familyId}/planning-scenarios/{id}`

`PUT /api/families/{familyId}/planning-scenarios/{id}` usa o mesmo corpo da criação.

`DELETE /api/families/{familyId}/planning-scenarios/{id}` arquiva o cenário.

`GET /api/families/{familyId}/planning-scenarios/{id}/parameters`

`PUT /api/families/{familyId}/planning-scenarios/{id}/parameters/{competence}`

Corpo: `{ "expectedAdditionalIncome": "500.00", "expectedAdditionalExpense": "237.45", "contingencyReserve": "1000.00", "recoveryTargetPercentage": "85.00", "notes": "..." }`.

As escritas exigem autenticação, participação na família e CSRF. Respostas monetárias e percentuais são strings decimais exatas.

## Projeção por cenário (T12.2)

`GET /api/families/{familyId}/planning-scenarios/{id}/projection` consulta o horizonte cadastrado no cenário. A projeção-base é obtida pelo mesmo serviço mensal da Agenda, incluindo transações, previsões pendentes, recorrências materializadas, cartões e conciliações conforme as regras existentes. Definições e ocorrências virtuais de recorrência ficam fora da base.

Para cada competência, a resposta retorna `openingBalance`, realizados, pendências, `creditCardOutstanding`, `baseClosingBalance`, ajustes, `cumulativeAdjustment`, `scenarioClosingBalance`, `contingencyReserve`, `plannedAvailable` e `projectionComplete`. O ajuste líquido é `additionalIncome - additionalExpense`; o acumulado é aplicado uma única vez ao fechamento-base. A reserva reduz somente `plannedAvailable` (`scenarioClosingBalance - contingencyReserve`) e nunca o saldo da conta.

Quando não há parâmetro explícito, os ajustes e a reserva são zero e `usesBaseProjectionOnly` informa essa condição. Obrigações de cartão sem conta de pagamento mantêm a projeção parcial, com `projectionComplete=false` e `hasUnallocatedObligations=true`; nenhuma conta é escolhida arbitrariamente. A consulta é somente leitura e preserva a captura temporal do motor mensal.

## Recuperação de saldo (T12.3)

Quando `recoveryAccountId` é informado, a mesma projeção mensal é consultada para essa conta, sem criar transferências hipotéticas. A resposta de cada competência inclui `openingNegativeBalance`, `closingNegativeBalance`, `projectedRecovery`, `cumulativeRecovery`, `recoveryCapacity`, `recoveryTarget`, `uncommittedMargin`, `remainingNegativeBalance`, `recoveryStatus` e `recoveryProjectionComplete`.

As fórmulas são determinísticas: `negativeBalance = max(0, -balance)`; `projectedRecovery = max(0, openingNegativeBalance - closingNegativeBalance)`; `recoveryCapacity = max(0, plannedAvailable)` quando a projeção está completa; `recoveryTarget = min(openingNegativeBalance, recoveryCapacity × recoveryTargetPercentage / 100)`; e `uncommittedMargin = recoveryCapacity - recoveryTarget`. A capacidade é um indicador de planejamento, não autoriza transferência automática nem altera o saldo real. A reserva continua fora do saldo de fechamento.

Os estados são `NotConfigured`, `NoNegativeBalance`, `Recovering`, `Stable`, `Worsening`, `Recovered` e `IncompleteProjection`. Sem conta-alvo, as métricas de recuperação ficam nulas e o consolidado continua válido (`NotConfigured`). Se a conta não tiver saldo inicial ou houver obrigação sem cobertura, a recuperação fica incompleta; nenhum valor é atribuído arbitrariamente a outra conta.

## Persistência e limitações

A migration `AddPlanningScenarios` cria tabelas com FKs compostas por família, índices de consulta, unicidade de cenário/competência e constraints de horizonte, competência, não negatividade e percentual. A migration incremental `AddRecoveryAccountToPlanningScenario` adiciona a conta-alvo opcional com FK composta por família. Não há criação de transações, previsões, transferências, alteração de orçamento, jobs ou interface React nesta tarefa.

## Evolução

A T12.3 poderá calcular metas de recuperação usando o percentual informativo, sem transformar a reserva em despesa bancária. A projeção atual não materializa hipóteses nem altera dados financeiros.

## Interface React (T12.4)

A seção **Planejamento** da família consome os cenários, parâmetros mensais e a projeção consolidada. É possível criar, editar e arquivar cenários, selecionar explicitamente uma conta-alvo de recuperação e salvar parâmetros por competência. A tela separa visualmente projeção-base, cenário e disponibilidade planejada, mantendo os valores monetários como strings até a formatação.

O gráfico mensal é apenas uma visualização dos valores retornados pela API e possui tabela acessível equivalente. A comparação entre cenários é lado a lado por competência; meses ausentes permanecem como indisponíveis e nenhum cálculo de diferença é inventado no navegador. Projeções incompletas são sinalizadas, sem atribuição arbitrária de obrigações.

A troca de família cancela as requisições pendentes e limpa o contexto do planejamento. A tela não cria transações, previsões, transferências ou metas de recuperação.

A auditoria integrada da T12 está registrada em [docs/T12.5-auditoria-planejamento.md](T12.5-auditoria-planejamento.md).
Obrigações de parcelas ainda devidas são incorporadas pela projeção-base quando a conta de pagamento é conhecida. Parcelas sem conta produzem cobertura incompleta e não são alocadas arbitrariamente.

O planejamento reutiliza essa projeção-base; parcelas não geram ajustes hipotéticos adicionais.

## Integração com objetivos (T14.3)

A projeção de cenário mantém `baseClosingBalance`, `scenarioClosingBalance` e `plannedAvailable` inalterados. Ela acrescenta `allocatedToGoals`, `goalProtectedAvailability` e `goalAllocationCoverageComplete` como informações de proteção, sem transformar aportes em despesas. A disponibilidade protegida é `scenarioClosingBalance - allocatedToGoals - contingencyReserve`; a contingência não é automaticamente considerada coberta por objetivos. A vinculação explícita de contingência e a evolução de aportes futuros permanecem fora desta etapa.
