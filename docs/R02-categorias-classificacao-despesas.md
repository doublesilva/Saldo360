# R02 — Categorias, subcategorias e classificação de despesas

Escopo local, sem push, deploy nem alteração de produção. A migration foi validada só em PostgreSQL descartável (fixture dos testes); **não foi aplicada** ao banco de laboratório nem ao principal. Este documento contém apenas contagens, sem valores, identificadores ou dados reais.

## 1. Modelo e contratos

**Hierarquia.** A subcategoria é uma linha de `financial_categories` com `parent_category_id` (auto-referência). Cada registro (lançamento, previsão, recorrência, compra de cartão, parcela de dívida) mantém **um único** `category_id`, que aponta para a principal ou para a subcategoria; por isso a classificação não pode divergir.

| Regra | Onde é garantida |
|---|---|
| Subcategoria no mesmo orçamento e tipo da principal | FK composta `(workspace_id, parent_category_id, type)` → `(workspace_id, id, type)` |
| No máximo dois níveis (sem 3º nível, sem principal com filhas virando filha) | trigger `trg_financial_categories_two_levels` (erro `CATEGORY_MAX_TWO_LEVELS`, 23514) + serviço |
| Sem ciclo / auto-pai | check constraint + trigger |
| Nome único: principal por orçamento+tipo; subcategoria dentro da principal (sem diferenciar caixa/acento, como o resto do projeto) | índices únicos parciais (`WHERE parent IS NULL` / `NOT NULL`) com collation ICU insensível a caixa |
| Sem exclusão em cascata, sem reorganização em lote, sem mover subcategorias | não existe endpoint de exclusão/movimentação |
| Ausência de categoria ≠ "Sem categoria" real | `category_id` nulo continua significando "sem categoria"; nenhuma categoria fictícia é criada |

**API de categorias** (`/api/financial-workspaces/{id}/categories`): `POST` aceita `{name, type}` (principal) ou `{name, parentCategoryId}` (o tipo é herdado; enviar tipo diferente → 400 em `type`). A resposta traz `parentCategoryId`, `parentName`, `isAvailable` e `path` ("Categoria / Subcategoria"). Erros: 400 (`parentCategoryId` inválido, de outro orçamento, inativo ou sub de sub), 409 (nome repetido no nível).

**Disponibilidade.** `isAvailable = isActive && (principal ativa)`. Uma subcategoria de principal inativa fica indisponível para **nova** classificação, mas registros já classificados continuam legíveis e, se a categoria não for trocada, continuam editáveis.

**Tipo de despesa** (`ExpenseNature`: `Fixed`, `Variable`, `Undefined`), coluna `expense_nature` (texto, padrão `Undefined`) em lançamentos, previsões, recorrências, compras de cartão e parcelas de dívida. Check constraints: valores conhecidos; receita ⇒ `Undefined`.
- Só existe em despesa; receita com `Fixed`/`Variable` → 400 em `expenseNature`.
- "Não definida" é explícito e é o padrão: nada é assumido como variável, nem inferido de cartão, parcelas, recorrência ou categoria.
- Independe da categoria: a mesma categoria pode ter despesas fixas e variáveis; fixas podem estar em categorias diferentes.
- Correção sem tocar em valor, data, saldo ou categoria: `PUT .../transactions/{id}/expense-nature` e `PUT .../credit-cards/{cardId}/purchases/{id}/expense-nature` (corpo `{expenseNature}`; qualquer outro campo é recusado).
- Transferências não têm categoria nem tipo. Pagamento de fatura e de parcela de dívida **não recebem** tipo (o endpoint recusa com 400): o compromisso já existe como compra/parcela, então não nasce uma segunda despesa fixa/variável.

**Filtros de listagem** (`transactions`, `planned-transactions`): `expenseNature=Fixed|Variable|Undefined` (valor inválido → 400). `categoryId` de uma **principal inclui as subcategorias**; de uma **subcategoria** traz só os registros dela. Os demais filtros e a navegação contextual da R01 foram preservados.

## 2. Propagação

| Origem → destino | Comportamento |
|---|---|
| Lançamento manual | `categoryId` (principal ou sub) + `expenseNature` na criação/edição |
| Previsão → realização | o lançamento realizado herda categoria e tipo da previsão; depois disso o tipo se corrige no lançamento |
| Recorrência → previsão / compra de cartão | cada materialização copia a definição vigente; **editar a recorrência vale só para as próximas materializações** (as já geradas não mudam) |
| Compra de cartão → parcelas | as parcelas herdam categoria e tipo da compra; o tipo é corrigível mesmo com fatura fechada (a categoria segue as regras de imutabilidade da fatura oficial) |
| Parcela de dívida | aceita categoria e tipo na criação via API; a UI de dívidas não tem formulário de categoria/tipo (ver limitações) |
| Lançamento Pluggy | nasce `Undefined`; a categoria vem da regra/catálogo/IA; o usuário corrige categoria e tipo pelas telas |
| Edição de registro derivado | editar a previsão não altera o lançamento já realizado; editar a categoria não muda o tipo e vice-versa |

Não há motor genérico de propagação nem reprocessamento em massa.

## 3. Pluggy e agente de IA

Reaproveitados o classificador e o provider existentes; nenhum segundo provider, agente ou agendador.
- Ordem preservada: escolha manual → regras → catálogo da Pluggy → IA (só com confirmação explícita do usuário na tela).
- **A escolha manual de categoria prevalece** e sincronizações repetidas (inclusive execução forçada e uso da IA) **não apagam** categoria nem tipo escolhidos. O tipo de despesa vive no lançamento/compra e a importação nunca o altera.
- A categoria externa da Pluggy só vale se mapear para uma categoria **local válida** (mesmo orçamento, tipo certo, disponível). A busca por nome prefere a principal; entre subcategorias homônimas de principais diferentes não há correspondência segura (não chuta).
- O menu enviado à IA lista só categorias disponíveis, subcategorias como "Principal / Sub". Códigos fora do menu, indisponíveis ou de outro tipo são rejeitados.
- **Sugestão de categoria nova da IA não é cadastro autorizado**: nada é criado. O lançamento fica em revisão e o nome sugerido (se for seguro) aparece só no motivo ("Sugestão de categoria nova, não cadastrada: X"). `MaxNewCategoriesPerRun` foi removido; `AllowCategorySuggestions` só controla se o nome entra no motivo. Prompt versionado (`2026-10-30.1`), que também diz à IA que ela não classifica fixa/variável nem cria categorias.
- A IA **não infere** tipo de despesa.
- Validação sem chamadas pagas: todos os testes usam o modelo falso; a validação real de IA continua dependendo de execução explícita do usuário.

## 4. Orçamento e painéis

- A meta de uma categoria principal cobre os registros diretos e os das subcategorias, cada registro contado **uma vez** (ex.: R$ 100 direto + R$ 200 em subcategoria = R$ 300, não R$ 500).
- Metas sobrepostas no mesmo orçamento (principal + subcategoria dela) são recusadas com 409 para não somar alvos que se sobrepõem.
- A Visão Geral, a Agenda e o orçamento exibem "Categoria / Subcategoria" quando aplicável.
- **Não implementado (R03):** meta consolidada de despesas fixas e teto único de cartões.

## 5. Interface

- **Categorias:** receitas e despesas separadas; principal com subcategorias agrupadas e recuadas; criar/renomear/desativar/reativar; formulário de subcategoria com a principal identificada; inativas sob demanda; estados vazio/carregando/erro/duplicado; uma categoria por vez (vírgula e ponto e vírgula recusados); sem módulo de tags ou estabelecimentos.
- **Formulários** (lançamento, previsão, recorrência, compra de cartão): select "Categoria / Subcategoria" (só as disponíveis, mais a que o registro já usa) e campo "Tipo de despesa" com a dica "Fixa é um compromisso habitual, mesmo que o valor mude." (somente em despesa).
- **Listas:** mostram categoria/subcategoria e o tipo **em texto** ("Tipo de despesa: Fixa") — nunca só por cor; filtro por tipo de despesa; filtro de categoria rotula "(com subcategorias)"; lançamentos importados, realizações de previsão e compras de cartão têm correção rápida do tipo.
- Revisão visual com dados sintéticos em 1440×900, 390×844 e 360×800: sem rolagem horizontal, sem texto fora da tela. Achado corrigido: no celular, o seletor de tipo de despesa apertava o botão "Cancelar lançamento" numa linha importada (agora ocupa a linha toda).

## 6. Migrations

`20261030153959_AddCategoryHierarchyAndExpenseNature`: remove/recria o índice único de nome (agora filtrado às principais), adiciona `parent_category_id`, `expense_nature` (5 tabelas), checks, índices, FK composta e a trigger de dois níveis. Sem conversão de dados antigos, sem camada de compatibilidade e sem backups (conforme a tarefa). Estruturas da Pluggy preservadas; tabelas dormentes da R00 não foram tocadas. `Down` reverte colunas, índices e a trigger.

Validada em PostgreSQL descartável: instalação completa a partir do zero (todas as migrations), `Down` até a migration anterior e reaplicação (principais preservadas, trigger recriada) e rejeição, direto no banco, de 3º nível, principal com filhas virando filha, tipo diferente, outro orçamento, auto-pai, irmã homônima, tipo em receita e valor desconhecido.

## 7. Testes e resultados

Execução proporcional (sem repetir a suíte completa de ~26 min):

| Conjunto | Resultado |
|---|---|
| Backend: classificação, persistência de categorias e categorização Pluggy | 75 aprovados após os ajustes (1 teste reescrito, ver abaixo) |
| Backend: novos casos de API/DB da R02 (hierarquia inválida, 3º nível, duplicidades, desativação e leitura, fixa em categorias diferentes, fixa+variável na mesma, receita sem tipo, realização/materialização, filtros, orçamento sem dupla contagem, integridade no banco, pagamento sem tipo) | 19 aprovados |
| Backend: Pluggy (sync repetida sem apagar categoria/tipo manuais, categoria externa não mapeada, sugestão de categoria nova, mapeamento para subcategoria, principal inativa), compra de cartão com tipo, migration down/up | aprovados |
| Backend: regressão dirigida de lançamentos, previsões, orçamento, recorrências, dívidas, Visão Geral, agenda, cartões | 178 aprovados (3 expectativas de contrato atualizadas, ver abaixo) |
| Backend: regressão Pluggy, persistência, classificação, cartões, plano mensal, bootstrap | 618 aprovados, 0 falhas |
| Frontend: `vitest` completo | 442 aprovados (17 novos); `tsc -b`, `oxlint` (0 erros; avisos já existentes) e `npm run build` ok |

Expectativas ajustadas por mudança legítima de contrato (nenhum teste removido): resposta de categoria ganhou `parentCategoryId/parentName/isAvailable/path`; execução do orçamento faz uma consulta constante a mais (subcategorias das metas, sem N+1); teste de "downgrade recusado" volta ao esquema atual antes de ler o dado; testes de payload do frontend agora esperam `expenseNature: "Undefined"` em despesa; o teste "IA sugere categoria nova e o backend cria" foi **reescrito** porque contradiz a regra da R02 (agora afirma que nada é criado).

Não executado: suíte completa do backend (apenas subconjuntos acima), testes com IA real/paga, validação em banco de laboratório.

## 8. Limitações e backlog

- A etapa do catálogo da Pluggy continua criando categorias **principais** canônicas quando o catálogo é inequívoco (comportamento anterior, determinístico, disparado pelo usuário). Só a sugestão da IA deixou de criar.
- A UI de dívidas não tem formulário de categoria/tipo: "Adicionar parcela" mantém categoria vazia e tipo "Não definida"; a API aceita ambos.
- Não há tela para escolher a subcategoria de um registro importado em massa, nem reorganização em lote (fora do escopo).
- Parcelas importadas do cartão já fechadas em fatura oficial seguem imutáveis para categoria (regra de banco existente); o tipo continua corrigível na compra.
- A migration ainda não foi aplicada ao banco de laboratório; fazê-lo exige as salvaguardas habituais (banco do laboratório, nunca o principal).
- Próxima tarefa: **R03 — Orçamento Simplificado: Categorias, Despesas Fixas e Teto Consolidado dos Cartões** (não iniciada).
