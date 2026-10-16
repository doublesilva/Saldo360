# Recorrências financeiras (T11.1)

Uma recorrência é uma definição de compromisso repetido. Ela não é um lançamento realizado nem uma previsão materializada. Nesta etapa, o Saldo360 apenas armazena a definição e calcula ocorrências virtuais sob demanda; nenhuma previsão, fatura, saldo ou orçamento é alterado automaticamente.

## Frequências e calendário

São aceitas `Weekly`, `Monthly` e `Yearly`, com `interval` entre 1 e 120. A data inicial é inclusiva e civil (`yyyy-MM-dd`). Recorrências mensais preservam o dia âncora original: dias 29, 30 e 31 usam o último dia válido do mês, sem alterar a âncora para os meses seguintes. Recorrências anuais de 29/02 usam 28/02 em anos comuns e retornam a 29/02 em anos bissextos.

O endpoint de ocorrências limita o período a 3.660 dias e não depende do relógio atual.

## Encerramento e estado

Uma definição pode ser sem encerramento, ter `endDate` ou `occurrenceCount` (as duas modalidades não podem ser combinadas). Estados: `Active`, `Paused`, `Ended` e `Archived`. Pausada, encerrada ou arquivada não retorna ocorrências. A pausa é um estado mínimo: não registra início/fim de cada intervalo; ao retomar, o cálculo volta a considerar a definição original. Histórico materializado e edição retroativa serão definidos na T11.2.

## Valores e referências

`amount` é decimal positivo, retornado no REST como string. `isEstimated` identifica valores aproximados sem criar ajuste automático. Conta e categoria devem pertencer à mesma família, estar ativas na criação/edição e a categoria deve ter o mesmo tipo (`Income` ou `Expense`).

## REST

- `GET /api/families/{familyId}/recurrences?includeArchived=false`
- `POST /api/families/{familyId}/recurrences`
- `GET /api/families/{familyId}/recurrences/{id}`
- `PUT /api/families/{familyId}/recurrences/{id}`
- `POST /api/families/{familyId}/recurrences/{id}/pause`
- `POST /api/families/{familyId}/recurrences/{id}/resume`
- `POST /api/families/{familyId}/recurrences/{id}/end`
- `DELETE /api/families/{familyId}/recurrences/{id}` (arquivamento lógico)
- `GET /api/families/{familyId}/recurrences/{id}/occurrences?from=yyyy-MM-dd&through=yyyy-MM-dd`

Escritas exigem autenticação, participação na família e CSRF. O retorno de ocorrência contém sequência, data, descrição, tipo, valor, conta e categoria.

## Limitações

Não há jobs, geração automática de previsões, edição individual de ocorrências, reajustes, notificações ou integração com cartões. Alterações nesta definição não modificam lançamentos, previsões, faturas, pagamentos ou orçamentos existentes.
