# R03 — Orçamento simplificado: categorias, despesas fixas e teto consolidado dos cartões

Escopo local, sem push, deploy nem alteração de produção. A migration foi validada só em PostgreSQL descartável (fixture dos testes), com a R02 aplicada; **não foi aplicada** ao banco de laboratório nem ao principal. Este documento contém apenas contagens, sem valores, identificadores ou dados reais.

Cada meta responde: **qual é o limite, quanto foi consumido e quanto está disponível?**

## 1. Modelo e contratos finais

### Tipos de meta (`financial_budget_targets`)

| Tipo (`groupingType`) | Escopo | Quantas por orçamento mensal |
|---|---|---|
| `Category` | a categoria escolhida; numa categoria principal, inclui as subcategorias (R02) | uma por categoria |
| `FixedExpenses` | toda despesa com natureza `Fixed`, de qualquer categoria e meio de pagamento | no máximo uma |
| `AllCards` | gastos de **todos** os cartões somados | no máximo uma |

- Não existe teto por cartão, meta de despesas variáveis nem teto global.
- Metas consolidadas não têm categoria nem nome editável (o servidor define "Despesas fixas" e "Todos os cartões"); enviar categoria ou cartão numa delas é 400.
- Banco: `category_id`/`category_type` passam a ser nulos só nas consolidadas; check `ck_financial_budget_targets_category_scope` liga o tipo à presença de categoria; índice único parcial `ux_financial_budget_targets_budget_aggregate (budget_id, grouping_type) WHERE category_id IS NULL`. Repetir a meta consolidada → 409; uma meta consolidada arquivada volta no mesmo registro, com o novo limite.
- Metas de categoria coexistem com as consolidadas. A restrição da R02 (categoria principal × subcategoria na mesma competência → 409) vale **só entre metas de categoria**.

### Fonte de verdade
O limite de "Todos os cartões" é a meta `AllCards` do orçamento do mês. A meta por conta pagadora da T10.1 (`financial_card_spending_targets`, `PUT financial-dashboard/card-target`, entidade `FinancialCardSpendingTarget`) foi **removida**, sem sincronização de compatibilidade. Consumidores atualizados para essa fonte única: painel Planejado × Realizado (`plan-actual`, bloco `cards`), plano mensal (T10.2) e Visão Geral. O limite agregado `CreditCards` que a importação de planilha gravava em `financial_monthly_aggregate_targets` deixou de ser lido (a tabela é uma referência histórica dormente, como as demais da R00); o total de despesas agregado segue sendo apenas referência do plano.

### API
- `POST budgets/{id}/targets`: `{groupingType, plannedAmount}` (+ `categoryId`/`name` em `Category`). `PUT .../targets/{id}` altera só o limite das consolidadas.
- `GET budgets/{id}/execution`: por meta, `planned`, `consumed`, `available`, `exceededAmount`, `committed` (previsto adicional), `projectedConsumption`, `availableProjected`, `exceededProjected`, percentuais, `status` (pelo consumido), `projectedStatus` (pela projeção) e `coverage`. O bloco `categories` resume **só as metas de categoria** (não se sobrepõem); `totalPlannedAmount` do orçamento também. Nenhum total soma fixas ou cartões.
- `GET budgets/{id}/targets/{targetId}/composition?page=&pageSize=`: os registros da meta, paginados no servidor (padrão 20, máximo 100), mais a participação de cada cartão (só informativa).
- `GET financial-dashboard/overview`: o bloco `budget` traz `fixed`, `cards` e `categories` (lista + resumo), sem "total do orçamento".

## 2. Competência e reconhecimento do consumo

Competência mensal explícita (a do orçamento). Uma única lista de registros (`BudgetFact`) alimenta resumo, composição, Visão Geral, Agenda e plano mensal; cada meta é um filtro dela (regra de cobertura única em `BudgetTargetMatcher`), então os totais sempre coincidem.

| Registro | Entra como | Data/competência | Observações |
|---|---|---|---|
| Despesa efetiva (lançamento) | consumido | data efetiva | exclui pagamento de fatura e pagamento de parcela de dívida (quitações, não consumo) |
| Previsão pendente | **previsto adicional** | data prevista | realizada, cancelada ou conciliada com fatura/parcela de dívida deixa de contar como pendente |
| Parcela de compra no cartão | consumido (cartão lançado) | mês de **vencimento da fatura** | cada parcela na sua competência; a compra não consome o total no primeiro mês |
| Parcela histórica/conhecida de cartão | consumido | vencimento da fatura (ou o informado) | |
| Encargo da fatura | consumido | competência da fatura | conta uma vez |
| Crédito/estorno da fatura | reduz o consumido | competência da fatura em que foi creditado | compra de R$ 100 e estorno de R$ 30 na mesma fatura = R$ 70 |
| Saldo anterior / saldo financiado | não é consumo | — | |
| Pagamento de fatura | **não conta** | — | não consome o orçamento de novo |
| Dívida: pagamento efetivo | consumido | data do pagamento | o lançamento bancário do pagamento não conta |
| Dívida: saldo ainda não pago da parcela | previsto adicional | vencimento | pagamento + saldo = a mesma parcela, nunca principal + parcela + pagamento |
| Transferência entre contas | não conta | — | |

Definições: **consumido** = efetivo + cartão lançado (mesmo com a fatura em aberto); **previsto adicional** = compromissos cadastrados ainda não consumidos; **projetado** = consumido + previsto; **disponível** = limite − consumido; **disponível projetado** = limite − projetado. Nada é estimado automaticamente: só entram compromissos cadastrados.

A realização de uma previsão substitui a contribuição pendente: prevista R$ 200 (previsto adicional) realizada por R$ 210 passa a consumir R$ 210 e deixa de contribuir com os R$ 200.

### Fixas e classificação pendente
Só `Fixed` entra na meta de fixas. `Undefined` e `Variable` ficam fora; as `Undefined` continuam entrando em categoria e cartões. Cada meta de fixas informa quantas despesas do mês estão sem tipo (e o valor), com o atalho "Revisar classificação" para a lista de lançamentos filtrada em despesas sem tipo no período (fluxo existente; nenhum módulo novo).

### Competência de cartão e cobertura incompleta
A competência vem da fatura da parcela. Uma compra importada do banco só tem competência **confiável** se veio com a fatura (bill) do banco ou se a fatura foi fechada pela instituição; sem isso a competência vem apenas do calendário configurado do cartão e é marcada **estimada**. Nunca se atribui a compra ao mês corrente por omissão: a meta mostra "Dados incompletos: N compra(s) com competência estimada", e a composição identifica cada registro afetado ("Competência estimada"). Sem cartões cadastrados, a meta de cartões mostra "Sem cartões cadastrados" e um aviso (não é um zero confirmado).

### Limite zero e ausência de dados
Limite zero não divide: com consumo, o percentual é nulo ("Sem referência") e o estado é excedido; sem consumo, 0%. Sem meta definida, a tela diz isso (nenhuma linha zerada). "Sem movimento nesta competência" só aparece quando há dados para afirmá-lo.

## 3. Interface
- **Orçamento:** cabeçalho com competência e navegação entre meses, "Nova meta" e "Ver na Agenda". Três grupos — Despesas fixas, Todos os cartões (com a competência das faturas) e Categorias. Cada meta: limite, consumido, "Disponível" ou "Excedido em", barra de progresso (`role=progressbar`) com os valores em texto, estado em texto (ícone e cor só reforçam), editar, arquivar e **abrir composição** (lista paginada com descrição, data/fatura, valor, categoria/subcategoria, tipo de despesa, cartão ou conta, situação realizada/lançada/prevista; participação por cartão informativa, sem campo de teto). Previsto adicional e projeção ficam numa linha complementar.
- **Nova meta:** tipo (fixas, todos os cartões ou categoria), categoria (só no tipo categoria, em "Categoria / Subcategoria", só disponíveis e sem meta), limite e a competência identificada. Se o mês não tem orçamento, ele é criado junto. Não existe escolha de cartão individual. Nenhum motor de repetição novo foi criado; a cópia do plano mensal (T10.2) segue copiando só limites por categoria.
- **Visão Geral:** o bloco de orçamento mostra fixas, todos os cartões e categorias (resumo + metas), cada uma com "Abrir composição" que leva ao Orçamento da competência já no grupo (e abre a composição de fixas/cartões). Não há "total do orçamento".
- **Agenda e plano mensal:** continuam projetados; somam apenas metas de categoria. A meta de cartões do plano mensal agora é a do Orçamento e só se aplica a "todas as contas" (numa conta específica não há teto individual).
- Revisão visual com dados sintéticos em 1440×900, 768×1024, 390×844 e 360×800 (base, valores negativos/enormes, nomes longos, limite zero, sem cartões e sem dados): sem rolagem horizontal nem texto cortado.

## 4. Migration
`20261030172644_AddSimplifiedBudgetTargets`: remove `financial_card_spending_targets`; torna `category_id`/`category_type` anuláveis; recria os checks de tipo; adiciona o check de escopo e o índice único parcial. Sem conversão de dados antigos, sem camada de compatibilidade e sem backups (conforme a tarefa). `Down` **recusa** (erro explícito) se existirem metas de fixas ou de cartões, em vez de descartá-las; sem elas, reverte e recria a tabela antiga vazia.

Validada em PostgreSQL descartável: instalação completa com a R02, `Down` até a migration da R02 e reaplicação (metas de categoria preservadas), recusa do `Down` com meta consolidada, e violações diretas no banco (segunda meta consolidada, categoria nula em meta de categoria).

## 5. Correções de domínio encontradas no caminho
- Parcela de dívida criada pela API descartava a categoria (`categoryId` nunca era gravado): agora é validada (despesa disponível do orçamento) e gravada; sem isso, dívidas fixas ficariam fora das metas de categoria.
- As consultas do orçamento passaram a ser em lote sobre uma lista única (11 consultas constantes por leitura, sem N+1).
- O painel Planejado × Realizado deixou de ter edição do teto de cartões por conta; o limite vive só no Orçamento.

## 6. Testes e resultados
Execução proporcional (sem repetir a suíte completa de ~26 min). Resultados na seção final do relatório de entrega; os cenários de aceite A–H têm testes de API sobre PostgreSQL real (`BudgetTargetsR03Tests`):

| Cenário | O que prova |
|---|---|
| A | três cartões somados contra um limite só (consumido e disponível); sem teto individual |
| B | fixas em categorias distintas (inclusive no cartão); variável e sem tipo ficam de fora; indicação de classificação pendente |
| C | uma assinatura fixa no cartão participa de Mídia, Fixas e Cartões sem virar três despesas; sem total global |
| D | R$ 100 direto + R$ 200 em subcategoria = R$ 300; coexistência com fixas e cartões; sobreposição categoria × subcategoria continua 409 |
| E | compra de R$ 900 em 3 parcelas consome R$ 300 por competência; pagamento da fatura não conta |
| E2 | parcela de dívida: pagamento + saldo = a mesma obrigação |
| F | previsto adicional vira consumido ao realizar, sem contar duas vezes |
| G | estorno reduz o consumo da competência (categoria e cartões); saldo anterior não é consumo |
| H | limite zero, ausência de metas, sem cartões, despesa sem tipo e competência estimada (Pluggy sem fatura do banco) |

Mais: composição com a mesma lista do resumo, paginação no servidor e isolamento por workspace; bloco da Visão Geral; integridade no banco; migration Down/Up; testes de frontend de cadastro, edição, navegação, estados e composição; build e lint.

## 7. Limitações
- Metas de cartões não têm teto individual por desenho; o plano mensal só mostra a meta em "todas as contas".
- Compra de cartão importada sem a fatura do banco tem competência estimada pelo calendário do cartão; a cobertura é dita, mas a competência só vira confiável quando o banco informa a fatura.
- Previsto adicional de cartões é sempre zero: compras e parcelas já lançadas são consumo; não há projeção automática de gastos futuros.
- Parcelas importadas e encargos/estornos de fatura não têm tipo de despesa (ficam `Undefined`), então não entram nas fixas.
- A UI de dívidas continua sem formulário de categoria/tipo (a API passou a gravá-los).
- Repetir limites para os meses seguintes continua restrito à cópia do plano mensal (categorias); não há repetição de fixas/cartões.
- A migration ainda não foi aplicada ao laboratório; exige as salvaguardas habituais (nunca o banco principal).
- Próxima tarefa: **R04 — Central de Automação Pluggy + Agente AI** (não iniciada).
