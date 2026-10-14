# Orçamentos e metas mensais

A T08.1 introduz o cadastro estrutural de orçamentos mensais. Ela não calcula consumo, disponível, percentual ou alertas: esses indicadores dependem da integração com movimentações prevista para uma etapa posterior.

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

## Migration e validação

A migration `AddFinancialBudgets` cria somente as duas tabelas, seus índices, checks e relacionamentos; nenhuma tabela financeira anterior é reescrita. Os testes usam PostgreSQL real para validar unicidade, precisão decimal, isolamento entre famílias, categoria de despesa, ausência de cascatas, arquivamento e atualização a partir da migration anterior sem perda de famílias ou categorias.

Para aplicar em desenvolvimento:

```bash
npm run db:migrate
```

