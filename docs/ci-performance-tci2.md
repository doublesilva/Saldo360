# T-CI.2 — CI rápida com validação por dependências

26/10/2026 — **Implementação e validação local concluídas; meta no runner GitHub ainda não homologada.**
FAST passou em 52,147 s com PostgreSQL real; seu job completo final (restore/build/testes) passou em 61,795 s. FULL aprovou os 1.332 casos. Objetivos, leitura documental, imagem e smoke também passaram. Mudanças financeiras amplas continuam com seleção mais custosa, sem enfraquecer seus contratos.
Não houve publicação desta implementação, chamada OpenAI, importação financeira ou deploy.

## 1. Referências e método de medição

Base desta tarefa: commit `3c55159`, 1.330 casos mantidos na T-CI.1. A classificação CORE/REGRESSION/INFRA permanece sem ser confundida com frequência: importância não obriga execução em toda CI.

| Referência | Duração de parede | Proveniência |
|---|---:|---|
| Backend normal local T-CI.1, 1.241 casos | 674,927 s (11min15s) | `.artifacts/ci-test-audit/after-normal-parallel.trx` e cronômetro do processo |
| Pipeline GitHub indicada no pedido | 815 s (13min35s) | Referência de aceite; teto mantido em **244 s (4min04s)** |
| Pipeline anterior publicada | 815 s, backend 799 s | [run 40236083603](https://github.com/doublesilva/Saldo360/actions/runs/40236083603), primeira tentativa, sucesso |
| Pipeline mais recente antes da T-CI.2 | 957 s (15min57s), backend 937 s | [run 40314212385](https://github.com/doublesilva/Saldo360/actions/runs/40314212385), SHA 3c55159, primeira tentativa, sucesso |

As consultas GitHub foram **GET de metadados**, sem logs da aplicação, rerun, dispatch, push ou deploy. O segundo baseline não torna o alvo mais permissivo: continua 244 s, não 30% de 957 s.

No backend publicado mais recente: restore 11 s; build 49 s; instalação OCR 16 s; normal 702 s; específicos 58 s; OCR 7 s; imagem 71 s; smoke 14 s. O frontend inteiro levou 66 s e infra 11 s. Esses tempos orientaram os gargalos; não são somados para simular um novo workflow.

O script `scripts/ci/Measure-WorkflowRun.ps1` mede runs **completas e aprovadas**, da criação até o mais tardio entre a atualização final do workflow e o término do último job. Inclui preparação, cache/post-steps, fila e esperas entre jobs; reporta também tempo sem fila inicial, duração integral por job, SHA, evento, tentativa e perfis realmente executados. Não soma durações individuais de testes ou jobs paralelos para alegar latência do pipeline. Capturar os metadados logo após a conclusão, pois atualizações posteriores podem alongar conservadoramente a janela. Runs de Deploy são recusadas; tentativas repetidas não recebem aprovação automática da meta habitual.

## 2. Reavaliação individual e perfis

[Catálogo T-CI.2](ci-performance-tci2-cases.csv): 826 métodos, 1.332 casos expandidos. Inclui os 824 métodos/1.330 casos existentes e dois guards novos da seleção. Para cada grupo: contrato, risco/cadência, áreas, referências de produção, custo de corpo observado na T-CI.1, necessidade de PostgreSQL/host e perfil.

As referências de arquivos são sinais estáticos por símbolos/convenção conferida e módulos produtores; **não são cobertura dinâmica de linhas**. Os tempos de corpo não incluem todo setup/teardown e não substituem os cronômetros de job. Campos vazios dos dois guards novos significam ausência de baseline, não custo medido zero.

| Perfil | Casos | Execução |
|---|---:|---|
| FAST | **428** | Sempre que o backend é relevante; 388 independentes de PostgreSQL e 40 relacionais reais |
| AFFECTED disponível fora de FAST | **904** | Subconjunto por dependências; inclui regressões e INFRA pertinentes |
| FULL | **1.332** | Todos, inclusive FAST, PostgreSQL, migrations, ferramentas e OCR real |

FAST e AFFECTED são disjuntos. FULL os contém; não somar FULL aos demais como testes únicos.
Sem `Skip`, exclusões permanentes ou fallback para InMemory nos contratos relacionais.

Exemplos de seleções adicionais, sem repetir FAST:

| Mudança representativa | Áreas ampliadas | Casos AFFECTED |
|---|---|---:|
| Objetivos | Goals, Scenarios, Dashboard | 8 |
| Leitor, schema e provider documentais compartilhados | Documents, Services | 332 |
| Contratos financeiros amplos de cartões | Cards, Documents, Services, Planning, Scenarios, Accounts, Budgets, Recurrences, Dashboard | 621 |
| Autenticação | Security, Families, Workspaces, Documents, Services | 436 |
| DbContext, migration, raiz de workspace, Money/Common, DI/Program, projeto/testes/CI ou caminho desconhecido | FULL conservadora | 1.332, sem FAST separado |

A cadência não remove a proteção especializada. Mudanças de maior alcance legitimamente custam mais que FAST; não se estreita AFFECTED para forçar um número de tempo.

### Dependências, não apenas nomes

`classify-changes.sh` mapeia o código de domínio/aplicação/API/infraestrutura, configurações relacionais e dependências reversas explícitas. Exemplos:

- Conta/movimento afeta saldos, transferências, projeções, orçamento e outros consumidores financeiros.
- Cartão afeta parcelas, faturas, documentos, projeções, orçamento e serviços que compartilham leitura.
- Leitor/schema/provider documental afeta Documents/Services, incluindo Inbox, importação conhecida e consolidação oficial marcadas como consumidoras. Não modifica CRUD de contas nem o repositório de previsões. A versão intermediária selecionava 621 casos para esse leitor; a seleção final mantém 332 diretamente pertinentes e suas integrações, sem eliminar os outros da validação financeira/FULL.
- Objetivo afeta alocações, cenários protegidos e dashboard. A inspeção mostrou consumo em `FinancialPlanningScenarioProjection` e `FinancialDashboardService`, **não** no calendário de lançamentos. Por isso Scenarios é separado de Planning: não aciona 162 casos sem relação comprovada.
- Mudança transversal, nova pasta de produção não mapeada, alteração nos testes/suporte/projetos ou falha na base/diff amplia para FULL.
- Testes têm `Area` explícita e, quando necessário, múltiplas áreas. O filtro usa esses contratos, não apenas uma expressão sobre o nome do teste.

`test-classify-changes.sh` verifica mudanças simples/combinadas, configurações, fontes compartilhadas, paths desconhecidos e entrada literal que poderia ser confundida com comando. Entrada de path nunca é emitida como código shell; somente áreas fixas geram filtros.

## 3. Proteção crítica em FAST

| Proteção | Sentinelas executáveis |
|---|---|
| Sessão, identidade externa, CSRF e replay | AuthEndpointsTests, SessionIdentityTests, callbacks OIDC essenciais, perfil/segredo por arquivo e proxies confiáveis/não confiáveis |
| Personal autorizado e concorrência | FinancialWorkspaceEndpointsTests: bootstrap concorrente único, CSRF e membership social sem conceder acesso financeiro |
| Dinheiro, datas, parcelas e cancelamento no domínio | FinancialValueDomainTests e regras reais de cartão, calendário, recorrência, orçamento e objetivos |
| CRUD e isolamento HTTP de lançamentos | IncomeAndExpense_PersistWithExactCentsAndDate, mudança de tipo/categoria, cancelamento estável, outsider e CSRF |
| Constraints e FK composta reais | FinancialTransactionPersistenceTests: valores inválidos no PostgreSQL e conta de outra raiz recusada |
| Transferência neutra e reversão | Acceptance_PJ2000_PF100_Transfer500_Gives1500And600_WithoutTouchingIncomeOrExpense; cancelamento e outsider |
| Cartão sem dupla contagem | InstallmentsPaymentAndCancellationRemainConsistentAcrossFinancialModules: parcelas, fatura, pagamento concorrente/idempotente, conta, limite, orçamento e projeção |
| Realização e snapshot | Realização concorrente/reabertura e SQL RepeatableRead/read-only de projeção e orçamento com writer independente |
| Importação documental atômica | Nove parcelas/datas desconhecidas, confirmação concorrente e rollback no save final; provider sintético, nenhum parser tradicional nesse caminho |
| Dívidas/objetivos/reset | Pagamento único de dívida, alocação idempotente de objetivo e rollback de reset |
| Documentos inválidos bloqueados | Schema, scope PurchasesOnly, evidência financeira, X/Y, datas, cobertura visual/regional e contexto autorizado |
| Seleção da própria CI | CiTestProfileContractTests: áreas conhecidas, dependência PostgreSQL explícita, OCR fora de FAST e sentinelas críticas não removíveis por perda de trait |

Casos de autenticação e precisão não foram apagados por estarem fora da seleção habitual. Suas matrizes completas continuam em AFFECTED/FULL. Os guards de metadados verificam decisões de execução **em runtime**; não repetem garantias do compilador nem fixam uma quantidade arbitrária de testes.

## 4. Infraestrutura e caminho crítico

- Reutilizados os dois lanes PostgreSQL da T-CI.1, cada um com container/template próprio e banco por caso. API, sessão, logs e dados continuam isolados.
- FAST cria **40 bancos consumidores**, contra 593 no normal anterior: **93,25% menos**. Dois templates migrados continuam necessários; não se trata de 40 containers.
- Sem compartilhar host mutável ou substituir SQL/transação/concorrência por EF InMemory. O InMemory preexistente dos testes de autenticação continua restrito ao contrato não relacional.
- Ferramentas nativas não rodam em FAST. OCR é instalado/executado quando AFFECTED alcança o leitor compartilhado e sempre em FULL.
- Cache NuGet contém somente pacotes, não binários da aplicação, credenciais ou relatórios financeiros.
- `backend-image` executa build e smoke **em paralelo** ao job de testes. Ambos continuam obrigatórios no agregador; não se pulou imagem/smoke para alegar ganho.
- `backend-affected` também é independente de `backend`/FAST: restaura, compila e executa sua seleção em outro runner. Seus resultados são obrigatórios em ci-success; em FULL ele é ignorado legitimamente porque o job backend já executa todos os casos. Não há matriz de dezenas de shards nem compartilhamento de banco entre runners. Há custo adicional de um runner/build, explicitamente trocado por menor caminho crítico.
- Buildx usa cache GHA separado para API/web, `push=false` e `load=true`. Nada é publicado em registry. A configuração segue a [documentação de cache do Docker](https://docs.docker.com/build/ci/github-actions/cache/) e das [actions de cache](https://github.com/actions/cache).
- Dockerfile.api restaura os quatro projetos e ferramentas antes de copiar o código. Uma alteração de fonte não invalida desnecessariamente download/restauração; runtime, pacotes OCR, usuário, healthcheck e bundle continuam existentes.
- Smoke usa banco/rede novos, aplica bundle e testa readiness pela porta HTTP publicada e usuário não root. Recusa nomes não descartáveis/colisões **antes** de instalar cleanup; trap remove somente recursos criados pelo teste. Não monta .env nem usa conexões reais.

Cache frio, fila de runner e contenção podem alterar o resultado. Melhorias arquiteturais e uma execução quente local não demonstram, por si só, a meta de 244 s no GitHub.

## 5. FULL, release e independência de Deploy

FULL está disponível por `workflow_dispatch` com `benchmark_area=none` (padrão) e agenda semanal (segunda, 05:23 UTC), além do fallback conservador. Executa o projeto de testes sem filtro, inclusive os três OCR reais. Um teste não pode ficar eternamente fora de validação porque seu módulo mudou pouco.

O dispatch também oferece benchmarks explicitamente declarados (`goals`, `documents`, `cards`, `security`). Executam FAST, as dependências do cenário escolhido e **todos** os estágios frontend/infra/imagem; o resumo informa que não houve diff real. São úteis para medir o mesmo SHA sem fabricar alteração funcional. Nunca geram `ci-full-success`: `release_full=false`, mesmo se uma seleção futura ampliar para FULL. O padrão continua sendo FULL verdadeira, não o cenário mais rápido. `test-workflow-selection.sh` executa a seleção real extraída do YAML e verifica oito cenários, incluindo o fallback sem base e a recusa de uma opção desconhecida.

Foi acrescentado o check **ci-full-success**, além do ci-success habitual. O Deploy permanece `workflow_dispatch`, sem workflow_run, disparo encadeado ou publicação automática. Seu gate somente de leitura agora exige CI comum e **FULL aprovada no mesmo SHA**, antes de Tailscale/SSH/ações produtivas. Marcador skipped de uma execução habitual não substitui nem mascara a última FULL real; FULL mais recente falhada ou em andamento bloqueia.

`test-release-gate.sh` executa o **script real extraído do workflow**, com respostas GitHub sintéticas e nenhuma requisição remota. Cobre ausência de FULL, sucesso, marcador skipped posterior, FULL falhada/em andamento, app estrangeiro e CI inválida. Alteraram-se também dois backticks de shell do resumo para texto literal; nenhum comando de deploy foi executado.

Isso prepara a exigência antes de release/cutover, sem acoplar “CI passou” a “publicar em produção”. Nenhuma configuração do servidor foi modificada.

## 6. Medições desta implementação

Cronômetros de processo/job local, Windows/Docker. Não somar durações dos casos para declarar speedup.
Builds/restores locais podem estar quentes; o runner GitHub novo ainda não foi executado.

| Rodada | Casos / bancos consumidores | Tempo integral | Resultado |
|---|---|---:|---|
| Primeira FAST, Docker indisponível | 388 passaram, 40 falharam no setup / 0 bancos concluídos | 227,835 s | **Inválida para aceite**: Docker /_ping HTTP 500, timeout de fixture; relatório preservado em fast.trx |
| FAST independente, diagnóstico local | 388 / 0 | 19,560 s | Aprovada; não substitui FAST completa |
| FAST completa após restabelecimento pelo responsável | 428 / 40 | **52,147 s** | Zero falhas/ignorados; fast-verified.trx |
| FAST: job final restore + build + testes | **428 / 40** | **61,795 s** | Zero falhas/ignorados; fast-final-job.trx; testhost 48 s, build sem avisos/erros |
| FULL: restore + build + todos os testes | **1.332 / 631** | **690,755 s** | Zero falhas/ignorados; full.trx; testhost 11min02s |
| Ensaio local conjunto serial: restore + build + FAST + AFFECTED Goals | **428 + 8 / 48** | **80,339 s** | Zero falhas/ignorados; fast-goals-job.trx e affected-goals.trx; limite conservador, não simulação dos runners paralelos |
| AFFECTED Goals isoladamente | **8 / 8** | **27,310 s** | Subetapa contida no job acima; não somar novamente |
| AFFECTED financeiro amplo/intermediário: restore + build + testes | **621 / 383** | **407,098 s** | Zero falhas/ignorados; affected-cards.trx; processo de testes 403,066 s, três OCR. Inicialmente acionado de modo excessivo pelo leitor; continua disponível para contratos financeiros amplos |
| AFFECTED documental final: restore + build + testes | **332 / 109** | **140,600 s** | Zero falhas/ignorados; affected-documents.trx; processo de testes 135,675 s, três OCR |
| Primeira imagem API, sem cache das novas camadas-base | 0 bancos | **202,908 s** | Build aprovado; inclui download do SDK/runtime e ferramentas, não inclui smoke |
| Primeiro smoke da API | 1 banco/rede descartáveis | **23,471 s** | Bundle/migrations, healthcheck, HTTP publicado e nonroot aprovados |
| Job de imagem API + smoke, cache local disponível | 1 banco/rede novos | **29,305 s** | Build + smoke medidos pelo mesmo cronômetro; nenhuma publicação em registry |
| Lint/contratos finais | actionlint + ShellCheck + seleção + gates | **19,566 s** | Aprovados; ferramentas nativas locais, sem Docker como pré-requisito de lint |
| GitHub novo | Não publicado | **Não medido** | Aguarda autorização de publicação; não é aprovado retroativamente pelo baseline |

FAST local: **674,927 → 52,147 s**, redução de **92,27%**, abaixo do teto 202 s. Esse resultado comprova a etapa de testes FAST local; não comprova 70% do workflow GitHub inteiro. O ensaio conjunto local medido inclui restore/build, as duas partidas de testhost e os oito casos afetados; não é obtido pela soma dos tempos dos testes. A workflow final executa FAST e AFFECTED em runners paralelos, cujas durações e fila devem ser medidas remotamente. Caches locais estavam disponíveis. Não comparar os 80,339 s diretamente aos 815 s do runner para declarar homologação remota.

O cronômetro final de 61,795 s inclui restore/build/testes e também fica abaixo de 202 s. Foi executado enquanto o job local de imagem/smoke usava o mesmo Docker; no GitHub esses jobs têm runners separados. A imagem fria e a quente estão discriminadas: não se atribui ao cache uma melhoria comprovada de compilação nem se usa o caso quente para prometer o mesmo resultado com cache vazio. As duas execuções de smoke aplicaram migrations exclusivamente a bancos novos descartáveis; não houve migração produtiva.

O perfil financeiro amplo de 407,098 s **não cumpre** o teto local habitual de 202 s. Esse custo não foi ocultado nem convertido em skip; mudanças de maior alcance e FULL continuam necessariamente mais caras. O documental final cumpre esse teto local de job, mas é evidência intermediária, não duração do workflow completo. Em GitHub, FAST/AFFECTED/imagem/frontend podem executar em paralelo; não se somam esses jobs para inventar uma medição remota.

A indisponibilidade inicial foi registrada, não convertida em skip. O responsável restabeleceu o Docker; não houve reinício de Docker/WSL pelo agente ou alteração de outros projetos. Builds Release com warnings-as-errors passaram; resultados finais consolidados abaixo.

### Conferência final

- FULL: **1.332 aprovados, zero falhas/ignorados**, incluindo três casos Poppler/Tesseract reais. Dois containers/template PostgreSQL, 631 bancos consumidores. O catálogo contém exatamente os mesmos 826 métodos e suas quantidades expandidas: zero faltantes/extras; 1.332 IDs TRX distintos.
- FAST final: **428/428**, 40 bancos, zero OCR nativo. AFFECTED Goals **8/8**, documental **332/332** e financeiro amplo **621/621**; todos são subconjuntos da FULL e têm **zero IDs em comum com FAST**. Execuções sobrepostas não são somadas como casos únicos.
- Build Release de `Saldo360.slnx`: zero avisos/erros. SHA-256 do assembly de testes usado pela FULL e pela revalidação final: `2160e7929c7364127ea86453cae6cf05db306ff11786001b56c4a04a34b46d2f`.
- ShellCheck 0.10.0 dos cinco scripts bash modificados/novos e actionlint 1.7.7 de **CI e Deploy**: aprovados. PowerShell do medidor com sintaxe validada e execução GET conferida contra os dois baselines reais. Contratos de seleção, oito cenários do YAML e nove cenários do gate: aprovados, sem requisições remotas nesses testes.
- `git diff --check`: aprovado; scripts bash em LF. Nos testes preexistentes, o diff contém **somente traits de execução/dependência**: nenhuma asserção ou regra financeira foi alterada. Nenhuma modificação em `src`.
- Imagem local `saldo360-api:tci2-local` construída sem `.env`; smoke real aprovado. Containers/redes descartáveis removidos automaticamente; nenhum recurso de outros projetos foi alterado. A imagem e caches locais são artefatos de desenvolvimento reutilizáveis, não deployment.
- Frontend/infra mantêm seus checks obrigatórios no YAML. Não houve nova medição local de npm/build web/instalação Linux nesta tarefa; não alegar aprovação integral desses jobs no runner ou redução neles. Os smokes API e OCR locais não substituem a execução GitHub pendente.

TRX ficam em `.artifacts/ci-performance-tci2/`, ignorados pelo Git. Os tempos de processo foram capturados por `Stopwatch`; os tempos individuais de casos servem somente ao inventário de custo.

### Classes de maior custo na referência

Tempo **somado de corpos**, somente para localizar custo; não é duração de classe/job com lifecycle:

| Classe | Casos | Corpo total (s) |
|---|---:|---:|
| FinancialAccountEndpointsTests | 49 | 49.959 |
| FinancialTransactionEndpointsTests | 37 | 45.571 |
| KnownStatementImportTests | 30 | 40.790 |
| FinancialPlannedTransactionEndpointsTests | 29 | 40.710 |
| AccountBalanceEndpointsTests | 18 | 39.753 |
| InboxStatementAnalysisTests | 30 | 37.203 |
| FinancialCategoryEndpointsTests | 25 | 29.036 |
| OfficialStatementConsolidationTests | 23 | 28.674 |
| FinancialTransferEndpointsTests | 25 | 27.810 |
| PlannedRealizationEndpointsTests | 17 | 26.267 |

Os arquivos com factory/banco e cada grupo relevante têm custo/cadência no catálogo. A escolha de FAST não foi feita apenas por ordenar esses números: operações transacionais caras e riscos reais continuam presentes.

## 7. Limpeza e regressões

Nenhum novo caso foi removido como desnecessário nesta tarefa: a T-CI.1 já eliminou 42 casos líquidos com equivalências documentadas. Não houve nova evidência de obsolescência que autorizasse inventar remoções adicionais.

Dois casos foram acrescentados para proteger a configuração de seleção (1.330 → 1.332); não são ensaios temporários, cálculo local nem garantia de compilação. Asserções financeiras existentes permaneceram intactas. O problema conhecido de categoryId em parcelas de dívida **não foi corrigido**; o achado permanece no [relatório anterior](ci-test-audit.md).

Não houve modificação em código funcional de src, migrations, cálculo financeiro ou flags produtivas. Os modelos/providers de teste continuam sintéticos; não se acessou a chave OpenAI do .env.

## 8. Validação no runner após publicação autorizada

Sem autorização de push/publicação, estes passos ficam preparados, não executados:

1. A primeira publicação desta alteração de testes/CI exige FULL conservadora. Após autorização, o responsável publica pelo fluxo habitual. Para medir no mesmo SHA, pode disparar CI com `benchmark_area=goals` ou `documents`; isso é benchmark declarado, não uma execução sobre diff funcional real e não autoriza release. Uma mudança real representativa também pode ser medida; não escolher uma edição transversal que intencionalmente exige FULL como se fosse FAST.
2. Registrar cache hit/miss, SHA, evento, jobs e fila. Manter 815 s e teto de 244 s como referência fixa; não escolher uma execução falhada/cancelada ou repetir até obter uma amostra favorável.
3. Consultar, sem mutação remota:

```powershell
# ID numérico da execução habitual concluída. Não dispara workflow nem Deploy.
pwsh -File scripts/ci/Measure-WorkflowRun.ps1 -RunId $runIdHabitual -BaselineSeconds 815 -TargetSeconds 244
```

4. Exigir `measuredTargetMet=true` com FAST realmente executada, todos os jobs pertinentes aprovados e primeira tentativa. Conferir TRX, AFFECTED esperada e execução de imagem/smoke, não só o tempo.
5. Antes de release/cutover, disparar **somente CI FULL**, `benchmark_area=none`, no SHA desejado e aguardar ci-full-success. Um benchmark nunca substitui esse check. Deploy continua manual e independente.

O helper usa os [metadados oficiais de runs](https://docs.github.com/en/rest/actions/workflow-runs) e [jobs](https://docs.github.com/en/rest/actions/workflow-jobs), nunca soma tempos de testes individuais para simular pipeline. Não solicita tokens no chat nem imprime credenciais.

## 9. Estado final e riscos

Validação local concluída, com meta local FAST atendida. Permanecem riscos inerentes à seleção por dependências, mitigados por sentinelas FAST, fallback amplo, testes do classificador e FULL semanal/pré-release. Novo código compartilhado deve ampliar o mapa, não ser associado arbitrariamente à área mais barata. Dois runners de testes e um de imagem podem aumentar minutos faturáveis/concorrência em troca de menor latência; não foi prometida redução monetária. Contenção, filas e cache frio exigem medição no runner.

A meta de GitHub não pode ser declarada concluída antes de uma execução publicada autorizada. Não foram abertas microtarefas, executados push/deploy ou alteradas funcionalidades fora do escopo.

Entrega: perfis/traits em testes; dois guards novos `CiTestProfileContractTests`; workflows CI/Deploy independentes; `Dockerfile.api`; classificador e seus testes; `test-workflow-selection.sh`, `test-release-gate.sh`, `smoke-api-image.sh`, `Measure-WorkflowRun.ps1`; este relatório, catálogo por método e aviso histórico na T-CI.1. Nenhuma correção do achado de categoryId, remoção artificial de proteção, chamada paga ou alteração produtiva foi incluída.

**Encerramento da implementação local:** pronta para publicação e medição autorizadas, sem alegar que 70% da pipeline GitHub já foi demonstrado. A condição externa restante é autorização de publicação/execução no runner, não um refinamento funcional pendente ou licença para disparar Deploy.
