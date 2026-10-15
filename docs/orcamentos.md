# Orçamentos e metas mensais

O módulo mantém orçamentos mensais e acompanha sua execução financeira por categoria. Alertas continuam fora deste módulo.

A interface React está disponível na seção **Orçamentos** dentro de cada família. Ela seleciona inicialmente o mês de `America/Sao_Paulo`, permite navegar por competências e utiliza exclusivamente os contratos de orçamento e execução descritos abaixo. A tela não calcula valores financeiros: apenas formata strings decimais e usa o percentual retornado pela API para dimensionamento visual.

## Modelo

- `financial_budgets`: um orçamento por família e competência. A competência é persistida como o primeiro dia do mês e exposta como `yyyy-MM`.
- `financial_budget_targets`: metas de despesa pertencentes ao orçamento. Nesta primeira versão, o agrupamento suportado é `Category`, pois categorias são o agrupador financeiro existente no domínio.
- Uma meta referencia obrigatoriamente uma categoria `Expense` da mesma família. Categorias inativas continuam válidas para preservar e permitir o planejamento histórico.
- `planned_amount` usa `numeric(15,2)`, deve ser maior ou igual a zero e menor que `10000000000000`. A API devolve valores monetários como strings com duas casas.
- O total planejado é derivado das metas não arquivadas; não é persistido e não altera saldos, agenda ou projeções.

Os índices únicos impedem mais de um orçamento para `(family_id, competence)` e mais de uma meta para `(budget_id, grouping_type, category_id)`. Registros arquivados continuam reservando essas chaves para impedir a reinterpretação do histórico.

Orçamentos e metas são arquivados logicamente por `DELETE`. Datas e usuários de criação, alteração e arquivamento são mantidos. Chaves estrangeiras usam `RESTRICT`; a família, categoria, orçamento ou usuário referenciado não é removido em cascata.

## Contrato HTTP

Todas as rotas exigem sessão e associação à família. Escritas também exigem `X-CSRF-TOKEN`. Recursos de outra família são tratados como inexistentes (`404`). Corpos com campos desconhecidos retornam `400`.

### Orçamentos

| Método | Rota | Comportamento |
| --- | --- | --- |
| `POST` | `/api/families/{familyId}/budgets` | Cria com `{ "competence": "2026-10", "description": "Outubro" }`. |
| `GET` | `/api/families/{familyId}/budgets?competence=2026-10` | Lista ativos; `competence` é opcional e estritamente `yyyy-MM`. |
| `GET` | `/api/families/{familyId}/budgets/{budgetId}` | Consulta inclusive um orçamento arquivado. |
| `PUT` | `/api/families/{familyId}/budgets/{budgetId}` | Altera a descrição com `{ "description": "Novo texto" }`. A competência é imutável. |
| `DELETE` | `/api/families/{familyId}/budgets/{budgetId}` | Arquiva e responde `204`. |

`includeArchived=true` inclui históricos na listagem. Competência duplicada, mesmo que a anterior esteja arquivada, retorna `409`.

### Metas

| Método | Rota | Comportamento |
| --- | --- | --- |
| `POST` | `/api/families/{familyId}/budgets/{budgetId}/targets` | Cria com `{ "name": "Moradia", "plannedAmount": "1500.00", "groupingType": "Category", "categoryId": "..." }`. |
| `PUT` | `/api/families/{familyId}/budgets/{budgetId}/targets/{targetId}` | Altera nome e valor planejado. Agrupamento e categoria são imutáveis. |
| `DELETE` | `/api/families/{familyId}/budgets/{budgetId}/targets/{targetId}` | Arquiva e responde `204`. |

Uma categoria só pode ter uma meta por orçamento, incluindo metas arquivadas. Duplicidades e tentativas de alterar recurso arquivado retornam `409`. Dados inválidos retornam `400` com `errors.<campo>`.

Exemplo de resposta:

```json
{
  "id": "0199...",
  "competence": "2026-10",
  "description": "Outubro",
  "isArchived": false,
  "totalPlannedAmount": "1500.00",
  "targets": [
    {
      "id": "0199...",
      "name": "Moradia",
      "plannedAmount": "1500.00",
      "groupingType": "Category",
      "category": { "id": "0199...", "name": "Moradia", "isActive": true },
      "isArchived": false,
      "createdAt": "2026-10-14T20:00:00Z",
      "updatedAt": "2026-10-14T20:00:00Z",
      "archivedAt": null
    }
  ],
  "createdAt": "2026-10-14T20:00:00Z",
  "updatedAt": "2026-10-14T20:00:00Z",
  "archivedAt": null
}
```

Metas arquivadas permanecem em `targets`, marcadas por `isArchived`, para que a consulta detalhada preserve o histórico. Elas não entram em `totalPlannedAmount`.

## Acompanhamento da execução

`GET /api/families/{familyId}/budgets/{budgetId}/execution` retorna somente metas ativas de um orçamento ativo. Um orçamento arquivado, inexistente ou pertencente a outra família resulta em `404`. A consulta exige que o usuário seja participante da família.

Para cada meta e para o total das categorias orçamentadas:

- `planned`: valor imutável da meta;
- `realized`: despesas efetivas não canceladas cuja `transactionDate` pertence à competência;
- `committed`: previsões de despesa não canceladas e ainda sem realização ativa, cuja `dueDate` pertence à competência;
- `projectedConsumption = realized + committed`;
- `available = planned - projectedConsumption`, podendo ser negativo;
- `exceededAmount = max(0, projectedConsumption - planned)`;
- `consumptionPercentage = projectedConsumption / planned * 100`, arredondado para duas casas decimais com ponto médio afastado de zero.

Quando `planned` é zero, o percentual é `"0.00"` se o consumo também for zero e `null` quando há consumo positivo. Valores monetários e percentuais definidos são serializados como strings decimais; os cálculos usam exclusivamente `decimal`.

```json
{
  "budgetId": "0199...",
  "competence": "2026-10",
  "total": {
    "planned": "5000.00",
    "realized": "1900.00",
    "committed": "1300.00",
    "projectedConsumption": "3200.00",
    "available": "1800.00",
    "exceededAmount": "0.00",
    "consumptionPercentage": "64.00"
  },
  "targets": [
    {
      "targetId": "0199...",
      "categoryId": "0199...",
      "planned": "5000.00",
      "realized": "1900.00",
      "committed": "1300.00",
      "projectedConsumption": "3200.00",
      "available": "1800.00",
      "exceededAmount": "0.00",
      "consumptionPercentage": "64.00"
    }
  ]
}
```

O total representa exclusivamente categorias com metas ativas, e não todas as despesas da família. Categorias sem meta, metas arquivadas, receitas e transferências são ignoradas. Uma categoria inativa permanece no acompanhamento enquanto sua meta estiver ativa.

### Competência, realização e reabertura

A competência é determinada pela data financeira, nunca pela criação ou atualização do registro. Uma previsão vencida de setembro permanece comprometida em setembro; ela não é transferida para outubro. Isso difere da projeção de saldo da T06.3/T07, na qual pendências anteriores influenciam a disponibilidade financeira futura.

Enquanto uma previsão possui lançamento efetivo não cancelado, seu valor previsto sai de `committed` e o valor efetivamente pago entra em `realized` na competência de `transactionDate`. Se a data efetiva estiver em outro mês, ela não entra no mês originalmente previsto e a previsão continua fora do comprometido enquanto realizada. Ao cancelar a realização, o efetivo sai e o previsto volta ao comprometido de sua competência original. Assim, os dois valores nunca são contados simultaneamente.

A leitura carrega as metas e executa duas agregações por categoria no PostgreSQL, sem consulta por meta. Todo o processo ocorre num snapshot `REPEATABLE READ` somente leitura para impedir estados intermediários durante realização ou cancelamento concorrente. Nenhum total é persistido e nenhum valor planejado é modificado.

### Limitações

Transferências não são despesas orçamentárias. O módulo também não cria parcelas nem expande recorrências. Cartões e faturas exigirão modelagem própria; não se infere pagamento de fatura a partir de transferências ou lançamentos existentes.

Na interface, metas arquivadas deixam de aparecer entre as metas ativas; o histórico permanece no backend. Arquivar uma meta ou orçamento não cancela movimentações e não cria restauração automática. Categorias de receita não são oferecidas no formulário de metas, e categorias inativas são identificadas explicitamente quando ainda podem ser usadas para preservar planejamento.

## Migration e validação

A migration `AddFinancialBudgets` cria somente as duas tabelas, seus índices, checks e relacionamentos; nenhuma tabela financeira anterior é reescrita. O acompanhamento da execução é calculado a partir dessas tabelas e das movimentações existentes, portanto a T08.2 não adiciona migration. Os testes usam PostgreSQL real para validar unicidade, precisão decimal, isolamento entre famílias, categoria de despesa, ausência de cascatas, arquivamento, agregações e consistência concorrente.

Para aplicar em desenvolvimento:

```bash
npm run db:migrate
```

