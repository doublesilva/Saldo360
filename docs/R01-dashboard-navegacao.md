# R01 — Novo Dashboard e Navegação

A Visão Geral foi reconstruída para responder, de relance: *qual é a minha situação, quanto entrou e saiu no mês, como o mês deve terminar e o que pede atenção*.
A referência de interface é o Folio (hierarquia de indicadores, gráfico e orçamento no corpo, contas/compromissos/metas ao lado, estados vazios que orientam);
nenhuma de suas limitações (armazenamento local, automações simuladas, cálculos não verificados) foi copiada. Todos os valores vêm do servidor; o navegador só formata.

## 1. Composição final

```
Cabeçalho   Competência · Conta/Consolidado · [Novo lançamento]
Indicadores Saldo atual | Receitas do mês | Despesas do mês | Fechamento projetado
Avisos      (só o que muda a leitura dos valores; detalhes em "Por que alguns valores são parciais")
Corpo       Evolução financeira        │  Próximos compromissos
            Orçamento do mês           │  Contas
            Últimos lançamentos        │  Metas
```

Em telas estreitas as duas colunas viram uma e a ordem prioriza o que pede atenção: indicadores → compromissos → contas → evolução → orçamento → últimos lançamentos → metas.
Não existem espaços reservados para módulos sem dados: cada bloco mostra o seu estado vazio com a ação correspondente.

## 2. Indicadores (um único conjunto)

| Indicador | Significado | Fonte | Referência de tempo |
|---|---|---|---|
| **Saldo atual** | Posição das contas do escopo. Se faltar saldo em alguma conta, mostra o saldo parcial com a tag "Parcial"; sem nenhum, "Indisponível" (nunca zero). | `plan-actual.situation.realizedBalance` | Hoje (não depende da competência); informa também a última atualização do banco |
| **Receitas do mês** | Receitas realizadas na competência; "Previsto a receber" como complemento. | `plan-actual.income` | Competência |
| **Despesas do mês** | Despesas **incorridas** na competência (consumo): saídas bancárias + compras de cartão pelo mês da fatura. O pagamento da fatura não é somado de novo. "Previsto a pagar" como complemento. | `plan-actual.expenses.consumption` | Competência |
| **Fechamento projetado** | Saldo estimado no último dia da competência. Mês encerrado vira **"Saldo no fechamento"** (valor realizado, sem a palavra projeção); mês futuro avisa que só considera compromissos conhecidos. | `plan-actual.situation.projectedClosing` | Último dia da competência |

O resultado do mês nunca é apresentado como saldo bancário. Saldo ou fechamento negativos recebem a tag textual "Saldo negativo" (não dependem só da cor).

## 3. Filtros e alcance por bloco

* **Competência** e **Conta** (conta principal · consolidado · cada conta, inclusive inativa com atividade) ficam guardados na página-mãe e sobrevivem à ida a um detalhe e à volta.
  O padrão é a conta principal definida pelo usuário (nenhuma conta ou identificador fixo no código); sem conta principal, mostra todas as contas e avisa.
* Indicadores, evolução, orçamento (meta de cartões), compromissos e últimos lançamentos acompanham o **escopo** escolhido.
* **Contas** lista sempre todas as contas ativas (é a porta para o detalhe) e marca a selecionada; o saldo é o de hoje.
* **Compromissos** têm horizonte próprio e fixo: vencidos (qualquer data anterior a hoje, até 12 meses) e os próximos 30 dias; o texto informa a data final.
* **Orçamento** e **Últimos lançamentos** seguem a competência; os lançamentos vão do início do mês até hoje (mês futuro: vazio) e o cabeçalho diz o período e o escopo.
* **Metas** não dependem de filtro (objetivos ativos do orçamento).
* **Evolução** mostra 3 meses antes da competência, ela e os 2 seguintes, no mesmo escopo; cada mês traz a sua situação (realizado, mês atual, projeção).

## 4. Contratos

* **Novo** `GET /api/financial-workspaces/{workspaceId}/financial-dashboard/overview?competence=yyyy-MM&scope=primary|all|account&accountId=`
  (`DashboardOverviewService`, `Application/Dashboard/Overview`): contas com saldo por conta, compromissos da Agenda, metas, orçamento do mês, últimos lançamentos e fila de
  classificação. Cada bloco tem `state` (`Complete`/`Partial`/`Unavailable`): a falha de um bloco não derruba os outros e "indisponível" nunca vira zero ou vazio.
  Valores monetários são texto decimal exato; datas, em America/Sao_Paulo; `Cache-Control: no-store`; mesma autorização por workspace (404 para quem não participa).
* **Ampliado** `GET .../transactions`: parâmetro `excludeInvoicePayments=true` omite os lançamentos ligados a um pagamento de fatura ativo (manual ou do banco). É o que impede
  "compras do cartão + pagamento da fatura" de aparecerem como duas despesas nos últimos lançamentos.
* **Removido** `GET .../financial-dashboard` (consolidado das T15.x) com o seu serviço, DTOs e a tela correspondente; os painéis `PlanActualDashboard`, `FinancialDashboard`,
  `DashboardDataPanels`, `MonthlyFlowSummary` e `DashboardFilters` saíram do frontend, junto do CSS e das fixtures que só eles usavam. `plan-actual` e `monthly-plan/six-months`
  permanecem e alimentam indicadores, avisos e evolução.
* **Frontend**: `components/overview/*` (`Overview`, `OverviewIndicators`, `OverviewEvolution`, `OverviewBudget`, `OverviewAside`, `useLoad`, `overviewText`), `api/overview.ts`, `Overview.css`.
  `PersonalFinance` passou a levar o contexto ao detalhe (mês da Agenda, competência do orçamento, período e conta dos lançamentos, e "novo lançamento" com o formulário aberto).

## 5. Avisos e simplificação operacional

Ficam visíveis só os avisos que mudam a interpretação: conta principal ausente, retrato inconsistente, sincronização com problema (falhou, expirou ou desatualizada),
movimentações do banco em revisão que ainda não entram nos totais, **fila de classificação** ("Há N lançamento(s) pendentes de classificação", aviso próprio, nunca tratado como falha de
sincronização) e "Projeção incompleta". As explicações técnicas (cartões sem ciclo confirmado, histórico parcial, parcelas sem mês etc.) ficam em um detalhe expansível, em linguagem direta.
Mantém-se uma linha com a última sincronização do banco e o botão **Sincronizar agora** (Pluggy continua acessível pela Visão Geral e por Contas). Nenhuma tela de conferência ou
conciliação foi recriada.

## 6. Orçamento (preparação para a R03)

O bloco lista as metas por categoria existentes, cada uma com valores junto da barra, status em texto (Dentro / Perto do limite / Limite atingido / Excedido) e o excedente; o excesso
também gera um aviso. A meta existente dos cartões aparece **à parte**, como "Meta própria", e há a nota "as metas olham para os mesmos gastos: seus valores não são somados" — nenhum total
global é calculado. A meta consolidada de despesas fixas e a meta única de cartões da R03 não foram implementadas.

## 7. Navegação

| Grupo | Destinos |
|---|---|
| Visão geral | Visão Geral |
| Finanças | Lançamentos, Contas, Cartões |
| Planejamento | Orçamento, Agenda, Metas, Planejamento |
| Configurações e mais | Categorias, Recorrências, Previsões, Transferências, Dívidas, Configurações |

A seção ativa tem `aria-current`; o menu móvel é um diálogo com foco confinado, fecha com Esc e devolve o foco ao botão. O item "Relatórios — em breve" (página vazia) foi retirado. O acesso
operacional ao Pluggy continua em Contas (e no Laboratório, quando habilitado). A central de Automação fica para a R04.

## 8. Correção encontrada no caminho

As rotas de **pausar, retomar, concluir e arquivar metas** respondiam sempre 400: o parâmetro do workspace se chamava `f` e não casava com `{workspaceId}` da rota (existia desde a T14). Corrigido
(`FinancialGoalEndpoints`) e coberto por teste. Também foi tornado independente do dia o teste `FinancialCalendarMonthlyProjectionEndpointsTests` (falhava nos últimos dias do mês).

## 9. Testes e evidências

* **Backend** (`DashboardOverviewTests`, 15, PostgreSQL real): saldo por conta (disponível × indisponível, principal primeiro), escopos, compromissos (vencidos × próximos, horizonte, só despesas pendentes,
  filtro por conta, contagem e total exatos com lista limitada, item realizado sai), metas (ativas, progresso), orçamento (ordem por severidade, excedente, mês sem orçamento), últimos lançamentos (mês, escopo,
  mês futuro vazio), fila de classificação, autorização/validação/`no-store` e o filtro `excludeInvoicePayments` com um pagamento de fatura real. Regressão direcionada (Plan×Actual, Agenda, plano mensal, reset, metas,
  workspace, contas principais, contratos de CI): 249 testes; Pluggy: 307 testes — todos passando. Os testes que liam o endpoint removido foram migrados para `plan-actual`/`overview`.
* **Frontend** (vitest, 425 testes no total; `tsc`, `oxlint` e build sem erros): `Overview.test` (25: indicadores, mês encerrado/futuro, indisponível/parcial/negativo, avisos, filtros, cada bloco, estados
  vazio/carregando/erro e isolamento de falha por bloco, sem valores financeiros sem resposta da API), `OverviewNavigation.test` (4: novo lançamento, Agenda no mês do vencimento, lançamentos do período e da conta,
  orçamento na competência e ausência de destinos retirados), `AppShell`/`PersonalFinance` atualizados e `layout-tests/OverviewLayout.test` (contrato de responsividade e de que nenhum componente soma ou arredonda dinheiro).
* **Revisão visual** em Chromium real (interface do Vite com a API interceptada por **dados sintéticos**; não foi possível usar a sessão autenticada do laboratório): 1440 × 900, 768 × 1024, 390 × 844 e 360 × 800, em três
  cenários — base, **estresse** (nomes longos, valores de nove dígitos, saldo negativo, muitos compromissos) e **vazio**. Medido por script: nenhuma rolagem horizontal da página, nenhum elemento fora da viewport, nenhuma sobreposição
  entre cartões e nenhum valor cortado (o texto quebra de linha); foco por teclado visível em todos os controles (a exceção é o seletor interno do navegador no campo de mês); menu móvel abre como diálogo, lista os 14 destinos e fecha com Esc.
  Capturas: `scratchpad/shots-r01/` (não versionadas).

## 10. Limitações e backlog (nada bloqueia a R02)

* **Edição da meta de cartões**: a antiga tela de Planejado × Projetado × Realizado, que permitia definir a meta por conta pagadora, saiu. O valor já definido continua valendo e aparece no orçamento; o backend
  (`PUT financial-dashboard/card-target`) permanece. A edição volta na **R03** (meta única para todos os cartões somados).
* **Recuperação do cheque especial** e o comparativo detalhado planejado × realizado por componente deixaram de ter tela própria na Visão Geral; os dados seguem em `plan-actual` e o plano mensal em **Planejamento**.
* **Compromissos** vêm da Agenda (previsões e parcelas de dívida). Faturas de cartão não são itens da Agenda e por isso não entram na lista (a própria tela informa); acompanhe-as em Cartões.
* A classificação pendente é uma contagem do orçamento todo (não do mês); a fila é resolvida na integração do Pluggy (R02/R04 vão tratá-la).
* Revisão visual feita com dados sintéticos; recomenda-se uma conferência com a sessão real após o próximo login.
* Resíduos da R00 ainda presentes e sem efeito: consultas a tabelas de conciliação dormentes na Agenda, no orçamento e na projeção (`FinancialPlannedInvoiceReconciliations` etc.) e `PlanActual.css` parcialmente usado pelo plano mensal.
* Possível refinamento futuro (não bloqueante): carregar os blocos em requisições separadas para a tela aparecer progressivamente; hoje o endpoint `overview` agrega contas, compromissos, metas, orçamento e lançamentos numa única resposta.

## 11. Próxima entrega

**R02 — Categorias, Subcategorias e Classificação de Despesas** (categoria e subcategoria, despesa fixa como classificação independente).
