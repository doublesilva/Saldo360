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

## Materialização em previsões (T11.2)

`POST /api/families/{familyId}/recurrences/{id}/materialize` recebe `{ "from": "yyyy-MM-dd", "through": "yyyy-MM-dd" }` e exige intervalo explícito. Cada ocorrência é identificada por `FamilyId + RecurrenceId + OccurrenceNumber`; a constraint PostgreSQL impede duplicidades. Reenvios e intervalos sobrepostos retornam `Existing` para vínculos já registrados e criam somente as ocorrências novas.

A operação é atômica e usa transação com bloqueio da recorrência. A previsão criada usa o modelo existente, começa pendente e não cria lançamento, pagamento, alteração de saldo ou orçamento. Realizações e cancelamentos preservam o vínculo. A consulta `GET .../{id}/materializations?from=...&through=...` também lista ocorrências calculadas sem previsão persistida (`plannedTransactionId: null`) e informa os estados Pending, Realized ou Cancelled das materializadas.

Alterações estruturais de calendário (frequência, intervalo, data, encerramento ou âncora) são bloqueadas quando já existem materializações. Alterações de valor, descrição, conta ou categoria não reescrevem previsões antigas; novas materializações usam a definição vigente. Ajustes individuais das previsões continuam sob as regras existentes.

## Limitações

Não há jobs, geração automática de previsões, edição individual de ocorrências, reajustes, notificações ou integração com cartões. Alterações nesta definição não modificam lançamentos, previsões, faturas, pagamentos ou orçamentos existentes.
## Interface da Agenda de Recorrências (T11.4)

A navegação de cada família inclui a opção **Recorrências**. A tela lista as definições ativas e arquivadas, permite criar e editar recorrências usando contas e categorias da própria família e apresenta as ocorrências calculadas no intervalo selecionado.

As ações de pausar, retomar, encerrar e arquivar exigem confirmação quando alteram o ciclo de vida. A materialização é sempre explícita: o usuário informa `from` e `through`, revisa a quantidade de ocorrências e confirma; a interface não materializa recorrências automaticamente. A resposta da API é usada para exibir criadas e já existentes, preservando a idempotência do backend.

O detalhe distingue ocorrências virtuais das materializações persistidas e informa o estado da previsão (pendente, realizada ou cancelada). Ocorrências virtuais não alteram saldos, Agenda ou orçamento. Depois da materialização, a navegação para **Agenda** mantém a família e o mês corrente.

Em alterações de definição, campos estruturais do calendário permanecem bloqueados quando já há uma recorrência cadastrada, evitando que a interface sugira uma reinterpretação de ocorrências históricas. Valores monetários são enviados e exibidos como strings decimais; contas, categorias e autorização continuam sendo validados pelo backend.

As consultas são canceladas quando a família muda, evitando que respostas antigas contaminem a tela. A interface é responsiva e mantém ações acessíveis por teclado. O tratamento de ocorrências já materializadas, ajustes individuais e conciliação financeira continua pertencendo aos fluxos existentes de previsões e à T11.2/T11.3.
