# T-CI.1 — Auditoria dos testes backend

Evolução posterior: [T-CI.2 — FAST/AFFECTED/FULL](ci-performance-tci2.md). Os perfis e tempos abaixo preservam a evidência histórica da T-CI.1, não a configuração mais recente da CI.

26/10/2026 — **Auditoria concluída localmente**: 1.372 → 1.330 casos; todos os casos restantes aprovados. Etapa normal de testes 41,61% mais rápida na medição local, com build/lint aprovados. Uma lacuna funcional preexistente foi identificada e registrada, sem alteração das regras da aplicação.

## 1. Base, método e contagem

A suíte encontrada tem **1.372 casos expandidos**, não 1.274: 835 métodos Fact/Theory em 137 arquivos de testes. O número do pedido ficou 98 casos atrás do código atual. A execução de referência confirmou 1.372 aprovados, sem falhas ou ignorados. Contagem inclui cada InlineData executado; não confunde métodos, asserts e casos.

Base de código anterior à limpeza: commit `bd70e87`. Parte da limpeza foi incorporada ao commit local `a9ff2e9` durante o trabalho; as alterações posteriores foram preservadas sobre ele. Não houve push/deploy realizado por esta execução.

[Catálogo individual](ci-test-audit-cases.csv): 835 registros, um por método inicial, incluindo contagens e ordinais de variações antes/depois, finalidade, classificação, decisão, destino e risco. Os ordinais se referem à ordem do InlineData na fonte correspondente; nomes dos métodos e linha inicial identificam o cenário exato na base. Os 12 grupos realocados mantêm suas entradas; não são testes novos contados duas vezes. A tabela ao final cobre todos os arquivos originais e os dois destinos novos.

A decisão considerou o código chamado, seus consumidores atuais, isolamento/fakes e observações efetivas. Testes com nomes semelhantes não foram tratados automaticamente como equivalentes. O compilador não garante autorização, transações, precisão decimal, regras de calendário, hash serializado ou fechamento financeiro. Testes chamados “Diagnostic” podem proteger regressão permanente e foram mantidos quando era o caso.

### Classificação final

| Classe | Casos mantidos | Critério |
|---|---:|---|
| CORE | 1.153 | Contratos funcionais, financeiros, autorização, isolamento, integridade e smoke essenciais |
| REGRESSION | 88 | Falhas concretas reproduzíveis, ainda relevantes |
| INFRA | 89 | Migrations, ferramentas administrativas, laboratório, Docker/OCR e contratos de deployment |
| DIAGNOSTIC | 0 | Sete casos sem execução do código da aplicação removidos |
| REDUNDANT | 0 | Redundâncias identificadas foram eliminadas/consolidadas: redução líquida de 35 casos |
| OBSOLETE | 0 | Nenhuma funcionalidade comprovadamente removida justificou essa classificação |
| **Total** | **1.330** | **824 métodos, 138 arquivos** |

Redução líquida: **42 casos (3,06%)**. Foram retiradas 46 instâncias antigas e quatro entradas distintas transferidas para uma teoria mantida. Não apresentar 46 como redução líquida. Uma classe/arquivo inteiro foi eliminado; dois arquivos recebem testes reais já existentes. A redução modesta de quantidade foi deliberada: não se apagou isolamento ou precisão apenas para atingir uma meta numérica.

## 2. Remoções e equivalência comprovada

| Local/cenário anterior | Antes → depois | Decisão e proteção equivalente |
|---|---:|---|
| `Application/FinancialGoalIntegrationTests.cs`, dois Facts | 2 → 0 | DIAGNOSTIC: aritmética de variáveis locais, sem chamar domínio, serviço ou repositório. Arquivo removido. Não eram testes de integração reais de objetivos. |
| `CreditCardInvoicePaymentTests.Remaining_amount_never_goes_negative` | 3 → 0 | DIAGNOSTIC: copiava fórmula de saldo/status sem chamar aplicação. Pagamento, saldo e cancelamento reais continuam em `FinancialCreditCardIntegrationAuditTests.InstallmentsPaymentAndCancellationRemainConsistentAcrossFinancialModules`. Não afirmar que isso comprova todos os limites de sobrepagamento. |
| `CreditCardProjectionTests.Limit_formula_preserves_negative_available_and_reports_over_limit` | 2 → 0 | DIAGNOSTIC: fórmula local, não testava serviço de limite. Cenários reais de limite/projeção permanecem na auditoria integrada; o teste eliminado não oferecia prova de limite negativo. |
| `AuthEndpointsTests.Health_RemainsAnonymous` | 1 → 0 | `HealthEndpointTests.Health_ReturnsHealthy_WhenDatabaseIsReachable` já chama health/live/ready anonimamente e valida conteúdo; guardas de sessão de rotas protegidas permanecem. |
| `FinancialAccountEndpointsTests.AccountWithoutOpeningBalance_ReturnsNulls_NotZero` | 1 → 0 | `OpeningBalanceNull_IsReturnedAsJsonNull_InEveryResponse_AndRemovalMakesBalanceUnavailable` verifica as mesmas nulidades e sua remoção/indisponibilidade em mais respostas. |
| `OidcCallbackTests.SensitiveCpfResultCannotLeakThroughDefaultSerializationOrToString` | 1 → 0 | Mesmos ToString/JSON do mesmo tipo em `AccessGatewayProfileClientTests.FullCpfIsAvailableOnlyInternallyAndNeverSerialized`, junto ao cliente real com transporte simulado. As validações do callback não foram retiradas. |
| `FinancialBudgetExecutionAlertTests.ClassificationDoesNotUseRoundedPercentage` | 1 → 0 | Mesmo limite 799,99/1.000 já consta na teoria de fronteiras que chama o classificador real. |
| `FinancialRecurrenceListTests.RepositoryListsEmptyActiveAndArchivedRows` | 8 → 2 | Mantidas linhas ativas+arquivadas com ambos os filtros; lista vazia com ambos os filtros coberta no HTTP `EmptyFamilyOwnerReceivesEmptyArray`. Eliminadas combinações unitárias repetindo os mesmos predicados, sem alterar ordenação/filtro testados. |
| `CreditCardStatementAgentPreviewTests.KnownStatementProfilesReturnSchemaValidReconciledDocuments` | 3 → 1 | Orquestrador falso variava somente emissor/valor; não exercitava layouts de três bancos. Representante preserva contrato do wrapper; parsers reais/fixtures continuam testados separadamente. |
| Mesma classe, `InvalidAgentResponsesFailClosed` | 3 → 1 | JSON inválido preserva a rota de falha do wrapper. Versão/schema/evidências têm testes próprios do validador/orquestrador. |
| `StatementInstallmentDiagnosticTests.AllReferenceMarkersGluedToMerchantHaveNoDiagnosticWhenCorrect` | 9 → 0 | Os nove X/Y continuam na fixture `ReferenceShapePreservesSixCurrentAndThreeFutureInstallmentsDatesAndIndividualValues`, em execução com/sem lotes. Acrescentada exigência explícita de ausência de diagnósticos. |
| `StatementInstallmentRecognitionTests.ExplicitMarkerAttachedToMerchantCannotRemainUnnumberedPurchase` | 8 → 3 | Mantidos 06/06, 2/3, 07/10: larguras distintas do marcador. Os outros números repetiam o mesmo ramo; todos os nove marcadores documentais continuam na fixture. |
| `DatesCommercialCodesAndMoneyAreNotInventedInstallments` e teoria diagnóstica de datas/códigos | 9 + 10 → 14 | Quatro entradas distintas transferidas: data completa inicial, ISO inicial, CODIGO colado e “em dd/MM”. Outras cinco já tinham forma equivalente. A teoria destino agora verifica documento/item inalterados, além de ausência de erro. |
| `WrongNumberingOrTypeIsRejectedWithoutChangingFacts` | 2 → 1 | Tipo Purchase incompatível mantido. Divergências de número/quantidade preservadas nos diagnósticos específicos, inclusive regressão real dos itens 2/4. |
| `ContradictingDescriptionAndQuoteNumberingIsNotSilentlyResolved` | 1 → 0 | `ConflictingMarkersAreNotReportedAsAnUnequivocalDocumentMarker` mantém rejeição, campo e marcador null; não escolhe uma fonte arbitrariamente. |

Não houve remoção de testes por terem poucos asserts ou por terem surgido durante uma investigação. A enumeração financeira indefinida, centavos fracionários, escala “1.500”, zero, negativos e limites de armazenamento não foram considerados todos iguais: são fronteiras distintas e permanecem onde há contrato próprio.

### Helpers, fixtures e dependências

Removido `coverlet.collector`: nenhum comando de CI/scripts/testes utiliza coleta XPlat/CollectCoverage. Não havia cobertura publicada que dependesse dele. Isso não remove ferramentas globais eventualmente instaladas pelo responsável. Não foram criados fixtures/helpers mortos para guardar testes removidos.

`FinancialValueDomainTests.cs` recebe 23 casos de calendário, Money, entidades e realização; `InvoiceAdjustmentValidationTests.cs` recebe quatro enums/tipos de ajuste recusados antes do repositório. Os 27 deixam de clonar banco/iniciar API para validar código puro. O repositório proibido do segundo arquivo falha se a validação tocar persistência. Constraints, transações e SQL reais continuam sobre PostgreSQL, não InMemory.

## 3. Regressões e segurança preservadas

- OIDC: issuer/subject, nonce/PKCE, assinatura inválida, token audience/expiração/permissões, cookies, CSRF, logout, TLS e segredo por arquivo. CPF/tokens/senhas ausentes de respostas e logs.
- Personal/workspace: proprietário persistido, acesso cruzado, arquivamento, reset/cutover explícito e preservação de histórico.
- Financeiro: centavos exatos, cancelamento/idempotência, pagamentos atômicos, concorrência e reconciliação, parcelas futuras fora da fatura atual, ausência de dupla contagem entre parcelas/faturas/caixa.
- Snapshot RepeatableRead e conexões concorrentes reais continuam exercitados; testes HTTP e SQL não são redundantes quando protegem fronteiras distintas.
- Extração: schema legado e hashes históricos, evidência literal/monetária, parcelas contraditórias, falsas parcelas em datas/códigos, baixa confiança antes da normalização, cobertura visual/raster, OCR e datas não inventadas.
- Inbox: hash revalidado antes do agente, reanálise concorrente/documento rastreado obsoleto e perfil desabilitado sem fallback silencioso. Laboratório continua sem parser, retry ou escrita financeira.
- Parsers legados não são “obsoletos” apenas porque não fazem parte do caminho AgentPrimary: há consumidores compatíveis ainda existentes. Ferramentas administrativas permanecem mantidas, mas em execução específica.

## 4. Performance do harness

Referência: um container PostgreSQL, template migrado uma vez, **665 clones**. Startup 11,242 s, migração do template 8,200 s; soma dos tempos registrados de clone/lock 118,427 s. Soma dos corpos de teste 698,748 s não é tempo de parede: inclui paralelismo e exclui parte do setup/dispose. A API era recriada por teste e a coleção PostgreSQL inteira era serial.

Otimizações implementadas:

1. 27 casos puros retirados do ciclo de API/banco, preservando os mesmos contratos.
2. Dois lanes xUnit fixos (`postgres` e `postgres-secondary`), cada qual com **um container/template próprio**, não um container por classe/caso. Classes distribuídas usando a referência medida; testes de cada lane continuam serializados. Host, sessão, logs, contexto e banco por teste não são compartilhados.
3. Limpeza de pools restrita à fixture/conexão; não despejar todos os pools globais ao clonar/migrar o banco da outra coleção. Não há compartilhamento de banco, transação ou substituição por InMemory.
4. Logger capturador preserva níveis, mensagens e campos estruturados usados nas asserções de sigilo; removida apenas a duplicação nos providers Console/Debug dos hosts de teste. Logging de produção intocado.

Não se compartilhou uma API mutável entre cenários com configurações/cookies distintos. Não se reduziu artificialmente o número de operações dos testes de corrida. A medição exploratória de geração RSA mostrou custo pequeno frente ao total; nenhuma troca de assinatura/autenticação foi necessária.

Custo: dois PostgreSQL locais simultâneos no perfil relacional, em vez de um. Aumenta consumo instantâneo, limitado a dois; requer Docker com recursos suficientes. O runner continua com timeout de 30 min e Testcontainers continua responsável pelo descarte. Eventual esgotamento de recursos deve falhar, não ignorar testes.

A medição normal final registrou 593 clones, divididos em 296/297 nos dois containers. Startup 9,955/10,085 s e migração do template 6,014/5,934 s. A soma de clone/lock foi 168,391 s: **não** se afirma que cada clone ficou mais rápido (há contenção de I/O no paralelismo). O ganho está no caminho crítico total, na retirada de setups inúteis e na execução seletiva; a diferença de 72 clones frente à referência completa também inclui os INFRA deslocados, não apenas testes eliminados.

## 5. Execução da CI

Três filtros disjuntos, sobre o mesmo assembly Release:

```text
normal:    Category!=INFRA
specific:  Category=INFRA&Dependency!=NativeOCR
ocr:       Dependency=NativeOCR
```

Normal executa **1.241** casos: CORE + todas as 88 regressões essenciais. Specific executa 86; OCR, três. Soma 1.330; nenhuma exclusão permanente. Não há matriz de dezenas de jobs nem teste desabilitado com Skip. TRX é preservado como artefato e seleção vazia falha (`TreatNoTestsAsError=true`).

- Alteração backend habitual: restore/build normal + normal; build da imagem e smoke com PostgreSQL migrado/readiness/nonroot permanecem.
- Banco/domínio/bootstrap/cutover/ferramentas/configuração da API/deployment: acrescenta specific conservadoramente.
- Cartões/extração/configuração compartilhada/testes/projetos/CI: acrescenta OCR; instalação Poppler/Tesseract somente quando necessária. Não basta nome de extensão do PDF para selecionar.
- Base Git indisponível, diff com erro ou `workflow_dispatch`: todos os componentes/perfis. Antes de release/cutover relevante executar CI completa por dispatch; não confundir com executar Deploy.
- Frontend: build/test/lint e imagem/smoke preservados.
- Infra: Compose sintético, install/coletores, secrets/preflight e wrapper privilegiado continuam. Nenhum desses checks executa deploy.

O classificador também cobre `Workspaces`, onde está o cutover documental. As variações e combinações são verificadas em `scripts/ci/test-classify-changes.sh`. CI e Deploy continuam workflows independentes; não foi introduzida ativação/deploy após CI.

## 6. Evidências e tempos

Execuções locais em Windows/Docker, mesma máquina; não são medições de runner GitHub Ubuntu. Não houve push/dispatch remoto. Não atribuir efeito de cache de compilação à limpeza de testes.

| Etapa | Antes | Depois / evidência |
|---|---:|---|
| Restore | Não cronometrado isoladamente na referência | 1,503 s, cache disponível |
| Build Release da solução | 216,809 s (recompilação) | Recompilação validada em 115,61 s; conferência incremental final em 3,84 s, zero avisos/erros. Tempos **não comparáveis causalmente** |
| Suíte backend completa anterior | 1.155,798 s / 1.372 aprovados | `.artifacts/ci-test-audit/baseline-full.trx` |
| Primeiro normal, ainda serial | 1.134,165 s / 1.240 aprovados + 1 falha | Só separar perfis não bastou; não é resultado final aprovado |
| Normal final, dois lanes | **674,927 s / 1.241 aprovados, zero falhas/ignorados** | `after-normal-parallel.trx`; testhost 10 min 59 s |
| Specific final | **64,344 s / 86 aprovados, zero falhas/ignorados** | `after-specific.trx`; testhost 55 s |
| OCR real final | **16,854 s / 3 aprovados, zero falhas/ignorados** | `after-ocr.trx`; testhost 15 s, imagem local com ferramentas reais, sem OpenAI |
| Classificador / ShellCheck / actionlint CI | Não medidos antes | 12,528 s / 11,118 s / 1,148 s, aprovados; incluem partida dos processos/containers |
| Frontend, imagens e smokes inalterados | Não medidos nesta auditoria | Não alegar speedup nessas etapas; comandos e requisitos preservados |

Baseline e primeira tentativa ocorreram em 25/10; retomada final em 26/10. Carga do sistema, caches e containers de outros projetos podem variar: usar a comparação de parede como evidência local, não garantia universal de redução percentual. A primeira tentativa com falha está preservada para não esconder resultados intermediários.

Comparação da etapa de testes da CI anterior (todos os casos) com o novo perfil normal: **19 min 15,798 s → 11 min 14,927 s**, economia de **480,872 s (41,61%)**, aproximadamente 1,71× mais rápido. Não é uma medição do workflow inteiro nem do runner hospedado. Frente à primeira tentativa normal ainda serial, a redução foi 40,49%; essa tentativa tinha uma falha funcional exposta, corrigida no preparo do teste conforme o achado abaixo.

A suíte restante foi executada integralmente pela união dos três perfis, sem recompilação entre eles: **1.241 + 86 + 3 = 1.330 aprovados**, zero falhas/ignorados. Soma das três durações de processo: **756,124 s (12 min 36,124 s)**, 34,58% abaixo da referência completa anterior; inclui as três partidas independentes do runner. Os perfis se comparam por testes efetivamente executados, não pela soma de tempos individuais de cada teste.

Conferência dos TRX: **1.330 IDs distintos**, zero métodos em mais de um perfil; os 824 métodos e suas quantidades de casos coincidem integralmente com a fonte final (zero faltantes/extras). Há nomes de exibição equivalentes em algumas teorias; não foram usados como identidade nem removidos por isso, pois protegem variações reais de caixa/acentuação/valores. Assembly de testes executado SHA-256: `e98cf8c322f10f19143662e320e342365dde9d907a7a0fcba032b2cfac46aea2`.

Foram criados 631 bancos consumidores no conjunto final (593 normal + 38 específicos), contra 665 na referência; são bancos descartáveis de teste. A mudança de perfis não deve ser confundida com a remoção de todos esses 72 clones do perfil normal. Nenhum container temporário destas execuções PostgreSQL/OCR permaneceu; containers preexistentes de outros projetos não foram alterados.

O lint do workflow CI alterado passou integralmente. A inspeção adicional do Deploy inalterado encontrou dois avisos preexistentes SC2006 (backticks de shell no resumo); não foram corrigidos fora do escopo nem declarados como lint integral do Deploy aprovado. Os três contadores sem uso do smoke da CI foram renomeados para `_`, sem alterar tentativas ou verificações de saúde.

## 7. Riscos e achado funcional não corrigido

**Categoria de parcela de dívida ignorada na criação HTTP.** `FinancialDebtInstallmentService.CreateAsync` recebe `InstallmentInput.CategoryId`, mas constrói `InstallmentDetails` sem esse argumento. O teste antigo `CategorizedInstallment_IsCommittedOnceInBudget` apenas procurava a palavra `targets` na resposta: passava com comprometimento zero. Ao exigir 1.000,00 no alvo/total e consumo projetado, falhou com 0,00. Confirmado pela leitura do serviço e do repositório orçamentário.

Sem autorização para alterar regras funcionais, não foi corrigido o serviço nem mudada a expectativa para zero. O teste foi renomeado `StoredCategorizedInstallment_IsCommittedOnceInBudget`: prepara pelo construtor real uma parcela categorizada no PostgreSQL e verifica os valores pela API autenticada de orçamento. Isso protege o cálculo com dados efetivamente categorizados, **não comprova que o endpoint de criação preserve a categoria**. A lacuna exige correção funcional autorizada; não foi ocultada como homologação ponta a ponta.

Os sete testes aritméticos removidos nunca provavam integração de objetivos, sobrepagamento ou limite negativo. A suíte mantida tem cenários reais de objetivos/cartões, mas não se declara cobertura integral desses contratos pelas fórmulas eliminadas. Quantidade de testes verdes não é prova de ausência de defeitos, como demonstra o achado acima.

Risco da execução seletiva: erro na seleção por caminhos. Mitigado com testes de classificador, fallback completo e dispatch obrigatório antes de releases relevantes. Risco de paralelismo: recursos de Docker e estado global. Não há configuração/DB/cookies compartilhados entre os dois lanes; operações de pool foram delimitadas. Scripts shell/deploy não foram colocados em paralelo com recursos de produção.

## 8. Recomendações justificadas e encerramento

- Corrigir preservação/validação autorizada de CategoryId da parcela de dívida em escopo funcional autorizado; então transformar o preparo do teste novamente em criação HTTP e exigir os mesmos totais. Não fazer isso silenciosamente nesta auditoria.
- Medir os TRX do runner após um push autorizado para confirmar duração/memória nos recursos Ubuntu; os números desta entrega são locais.
- Manter os três perfis completos antes de release/cutover. Não adicionar regressões históricas sem chamar o código relevante; preferir incorporar fronteira nova ao teste adequado.

Não foram abertas novas tarefas de refinamento. Nenhum código funcional, migration, credencial, dados financeiros reais, OpenAI, Gmail ou serviço de produção foi utilizado/alterado. Sem push ou deploy.

Encerramento: limpeza efetiva, inventário individual, execução seletiva, otimização medida e validação integral concluídos. Artefatos TRX estão em `.artifacts/ci-test-audit/` (ignorados pelo Git); relatório e catálogo são versionáveis. Testes removidos podem ser recuperados do histórico anterior `bd70e87`, sem manter código morto no projeto. Nenhum editor de lançamentos ou módulo funcional foi iniciado nesta auditoria.

## Apêndice — finalidade e contagem de cada arquivo

A categoria é por comportamento/método; arquivos mistos mostram mais de uma classe. Os caminhos CSV trazem as decisões específicas de cada grupo e as variações de teoria. Contagens abaixo são expandidas, não número de métodos.

| Arquivo | Casos antes → depois | Classificação mantida | Contrato auditado |
|---|---:|---|---|
| [AccessGatewayProfileClientTests.cs](../tests/Saldo360.Tests/Api/AccessGatewayProfileClientTests.cs) | 1 → 1 | CORE | Cliente de perfil real com transporte sintético: token de acesso validado, CPF somente interno e ausência de segredos na serialização. |
| [AccountBalanceEndpointsTests.cs](../tests/Saldo360.Tests/Api/AccountBalanceEndpointsTests.cs) | 18 → 18 | CORE | Saldo HTTP exato, corte civil/data inicial, cancelamentos, referências, valores indisponíveis e isolamento. |
| [AccountProjectionEndpointsTests.cs](../tests/Saldo360.Tests/Api/AccountProjectionEndpointsTests.cs) | 15 → 15 | CORE | Projeção HTTP: vencidos e pendentes uma vez, realização/cancelamento, filtros, precisão e snapshot concorrente. |
| [ApiExceptionHandlerTests.cs](../tests/Saldo360.Tests/Api/ApiExceptionHandlerTests.cs) | 2 → 2 | CORE | ProblemDetails e logs sanitizados/correlacionados, inclusive timeout de dependência. |
| [AuthEndpointsTests.cs](../tests/Saldo360.Tests/Api/AuthEndpointsTests.cs) | 11 → 10 | CORE | Sessão, cookie seguro, logout/CSRF, PKCE e redirecionamento local no pipeline HTTP. |
| [AuthenticatedDocumentPasswordProviderTests.cs](../tests/Saldo360.Tests/Api/AuthenticatedDocumentPasswordProviderTests.cs) | 18 → 18 | CORE | Desbloqueio autenticado limitado a dois prefixos, prioridade manual, estados de CPF e autorização antes da leitura. |
| [BackchannelCertificateValidationTests.cs](../tests/Saldo360.Tests/Api/BackchannelCertificateValidationTests.cs) | 6 → 6 | CORE | TLS com CA/intermediário configurados; rejeição de hostname, cadeia ou uso de certificado incompatíveis. |
| [BudgetBootstrapExtensionsTests.cs](../tests/Saldo360.Tests/Api/BudgetBootstrapExtensionsTests.cs) | 13 → 13 | CORE, INFRA | Contratos atuais de recorrência em cartão, parcelas conhecidas, proveniência, isolamento/reset e vínculo idempotente a fatura. |
| [CardConsolidationTests.cs](../tests/Saldo360.Tests/Api/CardConsolidationTests.cs) | 5 → 5 | CORE | Componentes financeiros disjuntos, importadas vinculadas e pagamentos sem contagem dupla; dívida arquivada e histórico somente leitura. |
| [CardLatestEmailEndpointsTests.cs](../tests/Saldo360.Tests/Api/CardLatestEmailEndpointsTests.cs) | 3 → 3 | CORE | Query persistida, sessão/CSRF/Personal autorizado e recepção Gmail sem mutar checkpoint ou fatos financeiros. |
| [ClientSecretFileTests.cs](../tests/Saldo360.Tests/Api/ClientSecretFileTests.cs) | 3 → 3 | CORE | Arquivo de segredo prioritário, falha fechada sem arquivo e segredo ausente de logs. |
| [CreditCardCsvPreviewEndpointsTests.cs](../tests/Saldo360.Tests/Api/CreditCardCsvPreviewEndpointsTests.cs) | 2 → 2 | CORE | Upload HTTP/CSV somente leitura, precisão, arquivamento e autenticação. |
| [FamilyEndpointsTests.cs](../tests/Saldo360.Tests/Api/FamilyEndpointsTests.cs) | 16 → 16 | CORE | Família social continua existente: criação/Owner, validação, identidade externa opaca, isolamento e respostas limitadas. |
| [FamilyInvitationEndpointsTests.cs](../tests/Saldo360.Tests/Api/FamilyInvitationEndpointsTests.cs) | 11 → 11 | CORE | Convite social: papel/CSRF/autenticação, token retornado uma vez, consumo/revogação e não divulgação. |
| [FinancialAccountEndpointsTests.cs](../tests/Saldo360.Tests/Api/FinancialAccountEndpointsTests.cs) | 50 → 49 | CORE | CRUD HTTP de contas Personal, defaults do servidor, responsabilidade autorizada, saldo inicial exato/null e histórico. |
| [FinancialBudgetEndpointsTests.cs](../tests/Saldo360.Tests/Api/FinancialBudgetEndpointsTests.cs) | 19 → 19 | CORE | Orçamento/metas por competência; execução exata, despesas elegíveis, cancelamentos e ausência de efeitos em caixa. |
| [FinancialCalendarBalanceEndpointsTests.cs](../tests/Saldo360.Tests/Api/FinancialCalendarBalanceEndpointsTests.cs) | 7 → 7 | CORE | Saldos diários consolidados/individuais, neutralidade de transferências e ausência de previsão/realização duplicadas. |
| [FinancialCalendarEndpointsTests.cs](../tests/Saldo360.Tests/Api/FinancialCalendarEndpointsTests.cs) | 6 → 6 | CORE | Calendário une realizados e pendentes ordenados, ações/filtros e totais exatos, com autorização. |
| [FinancialCalendarMonthlyProjectionEndpointsTests.cs](../tests/Saldo360.Tests/Api/FinancialCalendarMonthlyProjectionEndpointsTests.cs) | 9 → 9 | CORE | Projeção mensal/seis meses, encadeamento, orçamento sem virar fluxo, transferência neutra e snapshot. |
| [FinancialCategoryEndpointsTests.cs](../tests/Saldo360.Tests/Api/FinancialCategoryEndpointsTests.cs) | 25 → 25 | CORE | CRUD HTTP de categorias, normalização, unicidade, imutabilidade do tipo, arquivo e proteção de identidade. |
| [FinancialCreditCardImportEndpointsTests.cs](../tests/Saldo360.Tests/Api/FinancialCreditCardImportEndpointsTests.cs) | 2 → 2 | CORE | Confirmação CSV legada ainda utilizada: criação/replay e recusa atômica de parcela intermediária. |
| [FinancialCreditCardImportedInstallmentEndpointsTests.cs](../tests/Saldo360.Tests/Api/FinancialCreditCardImportedInstallmentEndpointsTests.cs) | 3 → 3 | CORE | Importação legada ainda publicada: vinculação/histórica explícita, idempotência, cancelamento e competência obrigatória. |
| [FinancialCreditCardIntegrationAuditTests.cs](../tests/Saldo360.Tests/Api/FinancialCreditCardIntegrationAuditTests.cs) | 2 → 2 | CORE | E2E real de compra/parcelas/fatura/pagamento/estorno de pagamento/limite/saldo/orçamento/projeção. |
| [FinancialDashboardEndpointsTests.cs](../tests/Saldo360.Tests/Api/FinancialDashboardEndpointsTests.cs) | 3 → 3 | CORE | Dashboard agregado: seções, horizonte, isolamento e alerta estruturado sem mutação. |
| [FinancialDebtBudgetIntegrationTests.cs](../tests/Saldo360.Tests/Api/FinancialDebtBudgetIntegrationTests.cs) | 1 → 1 | CORE | Parcela de dívida categorizada entra uma vez no orçamento; assert financeiro fortalecido nesta auditoria. |
| [FinancialDebtEndpointsTests.cs](../tests/Saldo360.Tests/Api/FinancialDebtEndpointsTests.cs) | 4 → 4 | CORE | Contrato e estados de dívida, pagamento idempotente, transação bancária única, validação e recursos ocultos. |
| [FinancialDebtInstallmentReconciliationEndpointsTests.cs](../tests/Saldo360.Tests/Api/FinancialDebtInstallmentReconciliationEndpointsTests.cs) | 1 → 1 | CORE | Vínculo explícito dívida/previsão concorrente e idempotente, sem criar transação bancária. |
| [FinancialEmailHistoricalEndpointsTests.cs](../tests/Saldo360.Tests/Api/FinancialEmailHistoricalEndpointsTests.cs) | 1 → 1 | CORE | Consulta histórica exige sessão/CSRF/janela explícita e permanece sem escrita de estado. |
| [FinancialGoalErrorEndpointsTests.cs](../tests/Saldo360.Tests/Api/FinancialGoalErrorEndpointsTests.cs) | 2 → 2 | CORE | Aporte/retirada: conflito, idempotência concorrente, cancelamento, logs seguros e ocultação de recursos. |
| [FinancialPlannedInvoiceReconciliationEndpointsTests.cs](../tests/Saldo360.Tests/Api/FinancialPlannedInvoiceReconciliationEndpointsTests.cs) | 2 → 2 | CORE | Conciliação previsão/fatura explícita: compromisso contado uma vez, desfazer, conta compatível e isolamento. |
| [FinancialPlannedTransactionEndpointsTests.cs](../tests/Saldo360.Tests/Api/FinancialPlannedTransactionEndpointsTests.cs) | 29 → 29 | CORE | Previsões HTTP: valores/data, referências ativas, cancelamento, filtros, paginação e isolamento sem efeito realizado. |
| [FinancialPlanningScenarioEndpointsTests.cs](../tests/Saldo360.Tests/Api/FinancialPlanningScenarioEndpointsTests.cs) | 4 → 4 | CORE | Cenários e parâmetros atuais: projeção cumulativa, reserva e recuperação explícita, sem fabricar transações. |
| [FinancialRecurrenceListTests.cs](../tests/Saldo360.Tests/Api/FinancialRecurrenceListTests.cs) | 10 → 4 | CORE | Lista real de recorrências, vazia/mista, ordenação, filtro de arquivadas e workspace estranho. |
| [FinancialRecurrenceMaterializationEndpointsTests.cs](../tests/Saldo360.Tests/Api/FinancialRecurrenceMaterializationEndpointsTests.cs) | 2 → 2 | CORE | Materialização por intervalo idempotente, sem caixa fictício; realização preserva a origem. |
| [FinancialResetConcurrencyTests.cs](../tests/Saldo360.Tests/Api/FinancialResetConcurrencyTests.cs) | 8 → 8 | CORE | Reset e escritores serializados no PostgreSQL, rollback, epoch, limpeza restrita e retry durável. |
| [FinancialResetEndpointsTests.cs](../tests/Saldo360.Tests/Api/FinancialResetEndpointsTests.cs) | 3 → 3 | CORE | Reset integral autorizado/atômico/idempotente: inventário completo e preservação de dados sociais/pessoais. |
| [FinancialTransactionEndpointsTests.cs](../tests/Saldo360.Tests/Api/FinancialTransactionEndpointsTests.cs) | 37 → 37 | CORE | Lançamentos HTTP: dinheiro/data exatos, histórico, transições, referências/tipo, CSRF e isolamento. |
| [FinancialTransferEndpointsTests.cs](../tests/Saldo360.Tests/Api/FinancialTransferEndpointsTests.cs) | 25 → 25 | CORE | Transferências HTTP: débito/crédito neutros, saldos, cancelamento idempotente, datas, precisão e referências. |
| [FinancialWorkspaceEndpointsTests.cs](../tests/Saldo360.Tests/Api/FinancialWorkspaceEndpointsTests.cs) | 5 → 5 | CORE | Bootstrap Personal persistido, unicidade concorrente, inativo não substituído e fronteira social/financeira. |
| [FinancialWriteMiddlewareTests.cs](../tests/Saldo360.Tests/Api/FinancialWriteMiddlewareTests.cs) | 3 → 3 | CORE | Conflitos PostgreSQL encapsulados viram resposta sanitizada 409/no-store, não exceções internas. |
| [ForwardedHeadersTests.cs](../tests/Saldo360.Tests/Api/ForwardedHeadersTests.cs) | 9 → 9 | CORE | Somente proxy/rede confiáveis alteram a origem OIDC; allowlist de host e configuração inválida. |
| [HealthEndpointTests.cs](../tests/Saldo360.Tests/Api/HealthEndpointTests.cs) | 3 → 3 | CORE | Liveness/readiness públicas, dependência indisponível sem segredos e configuração obrigatória. |
| [HealthReadinessPostgresTests.cs](../tests/Saldo360.Tests/Api/HealthReadinessPostgresTests.cs) | 1 → 1 | CORE | Smoke readiness com migrations e PostgreSQL reais, sem fatos financeiros criados. |
| [InboxStatementAnalysisTests.cs](../tests/Saldo360.Tests/Api/InboxStatementAnalysisTests.cs) | 30 → 30 | CORE, REGRESSION | Análise/diagnóstico armazenado, modos existentes, senha/reprocessamento e não consolidação de contexto inválido. |
| [KnownStatementImportTests.cs](../tests/Saldo360.Tests/Api/KnownStatementImportTests.cs) | 30 → 30 | CORE, REGRESSION | Fluxo T16.5: confirmação documental única, nove parcelas conhecidas sem data fictícia, atomicidade, projeção e idempotência. |
| [LoginFailureLoggingTests.cs](../tests/Saldo360.Tests/Api/LoginFailureLoggingTests.cs) | 3 → 3 | CORE | Falhas OIDC/token recebem motivos allowlisted, sem ecoar códigos/provider payload ou credenciais. |
| [OfficialStatementConsolidationTests.cs](../tests/Saldo360.Tests/Api/OfficialStatementConsolidationTests.cs) | 23 → 23 | CORE | Composição oficial transacional/versionada: sinais, ajustes, correções, pagamentos, histórico e DB guards. |
| [OidcCallbackTests.cs](../tests/Saldo360.Tests/Api/OidcCallbackTests.cs) | 23 → 22 | CORE | Callback OIDC real simulado: claims, token elegível, permissão de CPF e indisponibilidade sem quebrar login. |
| [OngoingCreditCardPurchaseTests.cs](../tests/Saldo360.Tests/Api/OngoingCreditCardPurchaseTests.cs) | 11 → 11 | CORE, INFRA, REGRESSION | Compras em andamento, centavos originais/histórico pago, preview, calendário, edição e rollback. |
| [PlannedRealizationEndpointsTests.cs](../tests/Saldo360.Tests/Api/PlannedRealizationEndpointsTests.cs) | 17 → 17 | CORE | Realização HTTP com idempotência concorrente, corridas editar/cancelar, reabertura e integridade do histórico. |
| [RequestObservabilityMiddlewareTests.cs](../tests/Saldo360.Tests/Api/RequestObservabilityMiddlewareTests.cs) | 4 → 4 | REGRESSION, CORE | Observabilidade da requisição preserva rota normalizada, correlação de servidor e sigilo mesmo após exceção. |
| [ServiceAccountApiTests.cs](../tests/Saldo360.Tests/Api/ServiceAccountApiTests.cs) | 1 → 1 | CORE | Cadastro pessoal de contas de serviço, identificadores protegidos/mascarados e escopo por usuário. |
| [SessionIdentityTests.cs](../tests/Saldo360.Tests/Api/SessionIdentityTests.cs) | 9 → 9 | CORE | Sessão persistida e ticket devem concordar ordinalmente em issuer/subject; legado inválido removido e logout isolado. |
| [BudgetBootstrapDomainTests.cs](../tests/Saldo360.Tests/Application/BudgetBootstrapDomainTests.cs) | 15 → 15 | CORE, INFRA | Invariantes de recorrências, parcelas conhecidas e snapshots; contratos auxiliares do importador administrativo separados. |
| [BudgetSpreadsheetMapperTests.cs](../tests/Saldo360.Tests/Application/BudgetSpreadsheetMapperTests.cs) | 28 → 28 | INFRA | Ferramenta administrativa existente: mapeamento determinístico/proveniência, decisões aprovadas e nenhuma informação financeira inventada. |
| [CardEmailSearchAuthorizationTests.cs](../tests/Saldo360.Tests/Application/CardEmailSearchAuthorizationTests.cs) | 3 → 3 | CORE | Acesso à caixa global limitado ao owner persistido do Personal aprovado, sem provisionamento na autorização. |
| [CardLatestEmailServiceTests.cs](../tests/Saldo360.Tests/Application/CardLatestEmailServiceTests.cs) | 24 → 24 | CORE, REGRESSION | Busca determinística mais recente, limites/ambiguidade/falhas seguras, recepção e continuação do documento existente. |
| [CreditCardBalanceProjectionTests.cs](../tests/Saldo360.Tests/Application/CreditCardBalanceProjectionTests.cs) | 2 → 2 | CORE | Compromisso de cartão reduz projeção uma vez; ausência de conta não autoriza atribuição arbitrária. |
| [CreditCardInvoiceCalendarTests.cs](../tests/Saldo360.Tests/Application/CreditCardInvoiceCalendarTests.cs) | 4 → 4 | CORE | Calendário de cartão: fechamento inclusivo, clamp de dias e competência no vencimento inclusive virada do ano. |
| [CreditCardInvoicePaymentTests.cs](../tests/Saldo360.Tests/Application/CreditCardInvoicePaymentTests.cs) | 4 → 1 | CORE | Identidade de operação e cancelamento lógico/idempotente de pagamento no domínio. |
| [CreditCardProjectionTests.cs](../tests/Saldo360.Tests/Application/CreditCardProjectionTests.cs) | 3 → 1 | CORE | Consumo projetado usa o serviço real e mantém compromissos bancários/cartão separados. |
| [FinancialBudgetExecutionAlertTests.cs](../tests/Saldo360.Tests/Application/FinancialBudgetExecutionAlertTests.cs) | 9 → 8 | CORE | Limiares usam valor cru, não percentual arredondado; mensagens de alerta do contrato atual. |
| [FinancialCalendarActionsTests.cs](../tests/Saldo360.Tests/Application/FinancialCalendarActionsTests.cs) | 8 → 8 | CORE | Ações permitidas dependem da origem e do estado, bloqueando edição de pagamentos/realizações indevidas. |
| [FinancialCalendarDailyBalanceTests.cs](../tests/Saldo360.Tests/Application/FinancialCalendarDailyBalanceTests.cs) | 4 → 4 | REGRESSION | Regressões de passado/pendências/abertura ausente e uma única leitura civil/snapshot. |
| [FinancialCreditCardPurchaseTests.cs](../tests/Saldo360.Tests/Application/FinancialCreditCardPurchaseTests.cs) | 19 → 19 | CORE | Parcelamento exato em centavos, validação, cancelamento e preservação do histórico pago original. |
| [FinancialCreditCardTests.cs](../tests/Saldo360.Tests/Application/FinancialCreditCardTests.cs) | 15 → 15 | CORE | Cadastro de cartão, query editável/limpável, limite/dias/dígitos e arquivo sem destruir histórico. |
| [FinancialDebtReconciliationRaceTests.cs](../tests/Saldo360.Tests/Application/FinancialDebtReconciliationRaceTests.cs) | 2 → 2 | REGRESSION | Interleaving determinístico lookup/eligibilidade: replay somente do par exato, sem segunda escrita. |
| [FinancialGoalAllocationTests.cs](../tests/Saldo360.Tests/Application/FinancialGoalAllocationTests.cs) | 1 → 1 | CORE | Identidade de operação, valor exato e cancelamento estável da alocação no domínio. |
| `FinancialGoalIntegrationTests.cs` (removido; recuperável na base Git) | 2 → 0 | DIAGNOSTIC removido | Verificações aritméticas locais sem chamada ao código do produto; removidas, não eram testes de integração. |
| [FinancialGoalTests.cs](../tests/Saldo360.Tests/Application/FinancialGoalTests.cs) | 3 → 3 | CORE | Valor/meta e transições do domínio atuais: pausar, retomar, concluir e arquivar sem apagar história. |
| [FinancialRecurrenceCalendarTests.cs](../tests/Saldo360.Tests/Application/FinancialRecurrenceCalendarTests.cs) | 5 → 5 | CORE | Calendário real de recorrências: âncora 31 após fevereiro, bissexto, intervalo/quantidade, fim inclusivo e pausa. |
| [FinancialSixMonthPlanningTests.cs](../tests/Saldo360.Tests/Application/FinancialSixMonthPlanningTests.cs) | 1 → 1 | REGRESSION | Regressão de data civil/snapshot únicos, saldos encadeados e cartão sem contagem dupla. |
| [FinancialValueDomainTests.cs](../tests/Saldo360.Tests/Application/FinancialValueDomainTests.cs) | 0 → 23 | CORE | 23 casos puros realocados sem mudar asserts de dinheiro, calendário, quantias, datas, cancelamentos e realização. |
| [InvoiceAdjustmentValidationTests.cs](../tests/Saldo360.Tests/Application/InvoiceAdjustmentValidationTests.cs) | 0 → 4 | CORE | Quatro entradas inválidas do serviço real recusadas antes de qualquer chamada ao repository, sem precisar de PostgreSQL. |
| [CreditCardCsvPreviewServiceTests.cs](../tests/Saldo360.Tests/CreditCards/CreditCardCsvPreviewServiceTests.cs) | 3 → 3 | CORE | CSV real com BOM/aspas/multilinha/localidade, colunas obrigatórias e valores ambíguos sem escrita. |
| [CreditCardPdfConfirmationRepositoryTests.cs](../tests/Saldo360.Tests/CreditCards/CreditCardPdfConfirmationRepositoryTests.cs) | 4 → 4 | CORE | Writer PDF legado real: composição mista, replay, conflito de decisões/referências e rollback integral. |
| [CreditCardPdfConfirmationServiceTests.cs](../tests/Saldo360.Tests/CreditCards/CreditCardPdfConfirmationServiceTests.cs) | 2 → 2 | CORE | Guards legados ainda usados: documento/parser/linhas elegíveis e categoria somente para compras. |
| [CreditCardPdfPasswordProviderTests.cs](../tests/Saldo360.Tests/CreditCards/CreditCardPdfPasswordProviderTests.cs) | 2 → 2 | CORE | Provider local de desenvolvimento existente tem tentativas limitadas e é indisponível em produção. |
| [CreditCardPdfPreReconciliationRepositoryTests.cs](../tests/Saldo360.Tests/CreditCards/CreditCardPdfPreReconciliationRepositoryTests.cs) | 1 → 1 | CORE | Leitura consistente da identidade de importação existente, sem mutação financeira. |
| [CreditCardPdfPreReconciliationServiceTests.cs](../tests/Saldo360.Tests/CreditCards/CreditCardPdfPreReconciliationServiceTests.cs) | 2 → 2 | CORE | Pré-conciliação legada classifica operações, separa informativos e detecta identidade/conflito sem escrita. |
| [CreditCardPdfPreviewServiceTests.cs](../tests/Saldo360.Tests/CreditCards/CreditCardPdfPreviewServiceTests.cs) | 7 → 7 | CORE | Prévia: senha manual/automática, estados de leitura distintos e divergência do cartão bloqueada. |
| [CreditCardPdfPreviewTests.cs](../tests/Saldo360.Tests/CreditCards/CreditCardPdfPreviewTests.cs) | 5 → 5 | CORE | PdfPig real/sintético protegido/corrompido e parser genérico legado com valores/páginas preservados. |
| [CreditCardStatementAgentPreviewTests.cs](../tests/Saldo360.Tests/CreditCards/CreditCardStatementAgentPreviewTests.cs) | 9 → 5 | CORE | Adaptador de prévia do agente: avaliação/evidência/confiança, falha de schema e ausência de escrita. |
| [CreditCardStatementAgentSchemaTests.cs](../tests/Saldo360.Tests/CreditCards/CreditCardStatementAgentSchemaTests.cs) | 30 → 30 | CORE, REGRESSION | Schema 1/2, campos presentes/null, dinheiro/datas/enums/evidência e códigos sanitizados; não apenas compilação. |
| [CreditCardStatementAgentShadowTests.cs](../tests/Saldo360.Tests/CreditCards/CreditCardStatementAgentShadowTests.cs) | 4 → 4 | CORE | Modo Shadow ainda existe: comparação exata, sinais/seções e não promoção de documento vazio. |
| [CreditCardStatementExtractionOrchestratorTests.cs](../tests/Saldo360.Tests/CreditCards/CreditCardStatementExtractionOrchestratorTests.cs) | 16 → 16 | CORE | Modos, parser proibido em AgentPrimary, falha fechada, retries limitados e grounding independente de identidade. |
| [CreditCardStatementResolverTests.cs](../tests/Saldo360.Tests/CreditCards/CreditCardStatementResolverTests.cs) | 7 → 7 | REGRESSION, CORE | Resolução por evidência de emissor/dígitos/titular, ambiguidade e shape histórico que participa de hashes oficiais. |
| [FinancialCreditCardInvoiceAdjustmentIntegrationTests.cs](../tests/Saldo360.Tests/CreditCards/FinancialCreditCardInvoiceAdjustmentIntegrationTests.cs) | 7 → 3 | CORE | Efeito relacional real de ajustes, sinais, limite/projeção, cancelamento, fatura oficial e isolamento. |
| [FinancialDocumentClassificationPreviewTests.cs](../tests/Saldo360.Tests/CreditCards/FinancialDocumentClassificationPreviewTests.cs) | 2 → 2 | CORE | Classificação somente leitura preserva status da Inbox, inclusive PDF protegido. |
| [FinancialDocumentClassificationTests.cs](../tests/Saldo360.Tests/CreditCards/FinancialDocumentClassificationTests.cs) | 11 → 11 | CORE | Roteamento por sinais/tipos; agente sintético estrito somente quando necessário, falhas/baixa confiança seguras. |
| [FinancialDocumentInboxServiceTests.cs](../tests/Saldo360.Tests/CreditCards/FinancialDocumentInboxServiceTests.cs) | 13 → 13 | CORE | Recepção/storage/hash e identidade externa, classificação/roteamento, associação e reprocessamento sem análise indevida. |
| [FinancialDocumentStorageTests.cs](../tests/Saldo360.Tests/CreditCards/FinancialDocumentStorageTests.cs) | 1 → 1 | CORE | Storage real temporário: namespace de workspace, bytes preservados e traversal recusado. |
| [FinancialEmailCollectorRunnerTests.cs](../tests/Saldo360.Tests/CreditCards/FinancialEmailCollectorRunnerTests.cs) | 5 → 5 | CORE | Locks, agendamento desabilitado, execução manual e separação do checkpoint histórico/incremental. |
| [FinancialEmailCollectorTests.cs](../tests/Saldo360.Tests/CreditCards/FinancialEmailCollectorTests.cs) | 14 → 14 | CORE | Coletor simulado: janela/checkpoint/dry-run, anexos/links, assinatura, erros parciais e dedupe por hash/identidade. |
| [FinancialEmailHistoricalWindowTests.cs](../tests/Saldo360.Tests/CreditCards/FinancialEmailHistoricalWindowTests.cs) | 10 → 10 | CORE | Janela estrita/limitada inclusiva no fuso São Paulo, sem depender do timezone do container. |
| [FinancialEmailLinkExtractorTests.cs](../tests/Saldo360.Tests/CreditCards/FinancialEmailLinkExtractorTests.cs) | 9 → 9 | CORE | Somente HTTPS/host/path autorizados, identidades estáveis e recusa de esquemas/credenciais/endereço inseguros. |
| [FinancialEmailScheduleTests.cs](../tests/Saldo360.Tests/CreditCards/FinancialEmailScheduleTests.cs) | 5 → 5 | CORE | Próxima janela civil de São Paulo e startup sem replay de execuções perdidas. |
| [FinancialLinkedDocumentDownloaderTests.cs](../tests/Saldo360.Tests/CreditCards/FinancialLinkedDocumentDownloaderTests.cs) | 2 → 2 | CORE | Download sem credenciais, nome seguro, DNS privado e redirecionamento fora de allowlist recusados. |
| [GmailFinancialEmailSourceTests.cs](../tests/Saldo360.Tests/CreditCards/GmailFinancialEmailSourceTests.cs) | 9 → 9 | CORE | Gmail HTTP simulado: paginação/internalDate, query/janelas, MIME e segredo de arquivo sem fallback. |
| [ItauCreditCardPdfParserTests.cs](../tests/Saldo360.Tests/CreditCards/ItauCreditCardPdfParserTests.cs) | 3 → 3 | REGRESSION | Parser legado ativo: regressões de duas colunas, valores/sinais e linhas desconhecidas não conciliadas. |
| [NubankCreditCardPdfParserTests.cs](../tests/Saldo360.Tests/CreditCards/NubankCreditCardPdfParserTests.cs) | 3 → 3 | REGRESSION | Parser legado ativo: layout observado, evidência/desconhecidos e seleção de perfil determinística. |
| [OpenAiCreditCardStatementAgentProviderTests.cs](../tests/Saldo360.Tests/CreditCards/OpenAiCreditCardStatementAgentProviderTests.cs) | 23 → 23 | CORE | Transporte HTTP sintético verifica schema/store=false, imagens, orçamento, modelo, flags, falhas e segredo sem fallback. |
| [RealOcrToolTests.cs](../tests/Saldo360.Tests/CreditCards/RealOcrToolTests.cs) | 3 → 3 | INFRA | Poppler/Tesseract reais sobre fixtures sintéticas: PDF protegido, texto/imagem mistos e OCR regional com coordenadas. |
| [ServiceAccountResolverTests.cs](../tests/Saldo360.Tests/CreditCards/ServiceAccountResolverTests.cs) | 17 → 17 | CORE | Identificadores normalizados/protegidos, HMAC escopado, ambiguidade/conflito, máscara e proibição de inferir por CPF. |
| [ServiceBillConfirmationTests.cs](../tests/Saldo360.Tests/CreditCards/ServiceBillConfirmationTests.cs) | 8 → 8 | CORE | Serviços e PostgreSQL reais de confirmação mensal/binding, idempotência concorrente e ausência de caixa fictício. |
| [ServiceBillExtractionTests.cs](../tests/Saldo360.Tests/CreditCards/ServiceBillExtractionTests.cs) | 10 → 10 | CORE | Extração de contas de serviço por tipo, campos exatos/opcionais, schema inválido e provider indisponível. |
| [ServiceBillLayoutTests.cs](../tests/Saldo360.Tests/CreditCards/ServiceBillLayoutTests.cs) | 13 → 13 | REGRESSION | Regressões de colunas/labels e valores repetidos/conflitantes, sem inventar emissor raster ou usar consumo como total. |
| [ServiceBillPreviewServiceTests.cs](../tests/Saldo360.Tests/CreditCards/ServiceBillPreviewServiceTests.cs) | 3 → 3 | CORE | Prévia de conta de serviço somente leitura, senha e identidade do usuário encaminhada ao resolver. |
| [ServiceBillRecurringMatchTests.cs](../tests/Saldo360.Tests/CreditCards/ServiceBillRecurringMatchTests.cs) | 13 → 13 | CORE | Correspondência única por identidade/competência, não valor; ambiguidade e referências mascaradas. |
| [StatementBatchExtractionTests.cs](../tests/Saldo360.Tests/CreditCards/StatementBatchExtractionTests.cs) | 11 → 11 | CORE | Lotes limitados/disjuntos, multiplicidade, conflitos de cabeçalho, páginas/cobertura, cancelamento e stop sem retry pago. |
| [StatementCategorizationTests.cs](../tests/Saldo360.Tests/CreditCards/StatementCategorizationTests.cs) | 10 → 10 | CORE | Sugestões e proveniência por identidade exata, histórico/correção explícita prioritários, sem inventar categoria/ID. |
| [StatementDocumentaryConfigurationTests.cs](../tests/Saldo360.Tests/CreditCards/StatementDocumentaryConfigurationTests.cs) | 12 → 12 | CORE | Perfil T16.5 desabilitado por padrão, calendário corroborado, zeros inelegíveis e autorização anterior a PDF/provider. |
| [StatementExtractionLabPipelineTests.cs](../tests/Saldo360.Tests/CreditCards/StatementExtractionLabPipelineTests.cs) | 4 → 4 | INFRA | Laboratório pago isolado: orçamento global, limites e documento protegido falham antes de chamar provider/CPF. |
| [StatementInstallmentDiagnosticTests.cs](../tests/Saldo360.Tests/CreditCards/StatementInstallmentDiagnosticTests.cs) | 29 → 24 | REGRESSION | Regressão real X/Y divergente: diagnóstico por campo/índice/página/lote sem conteúdo financeiro e sem corrigir fatos. |
| [StatementInstallmentRecognitionTests.cs](../tests/Saldo360.Tests/CreditCards/StatementInstallmentRecognitionTests.cs) | 26 → 10 | REGRESSION | Seis parcelas correntes/três futuras, valor/data próprios, totalizadores recusados e evidência monetária literal. |
| [StatementLabReferenceValidationTests.cs](../tests/Saldo360.Tests/CreditCards/StatementLabReferenceValidationTests.cs) | 11 → 11 | INFRA, CORE | Comparador offline independente não pode sobrepor rejeição/cobertura; guards geométricos de produção mantidos na suíte normal. |
| [StatementPurchaseDocumentValidationTests.cs](../tests/Saldo360.Tests/CreditCards/StatementPurchaseDocumentValidationTests.cs) | 35 → 35 | CORE | Datas somente comprováveis, cobertura específica de compras/multiplicidade/colunas e identidade separada/contraditória. |
| [StatementPurchaseExtractionTests.cs](../tests/Saldo360.Tests/CreditCards/StatementPurchaseExtractionTests.cs) | 31 → 31 | CORE, REGRESSION | PurchasesOnly rejeita tipos/resumos/natureza/seção/inclusão indevidos e nunca reconcilia inventando valores. |
| [StatementVisualRegionCoverageTests.cs](../tests/Saldo360.Tests/CreditCards/StatementVisualRegionCoverageTests.cs) | 17 → 17 | REGRESSION | OCR regional: legal/compra/totalizador, sobreposição física, falhas, coordenadas e fidelidade separada de persistência. |
| [StatementVisualSafetyTests.cs](../tests/Saldo360.Tests/CreditCards/StatementVisualSafetyTests.cs) | 6 → 6 | CORE | Qualidade nativa e revisão por evidência visual/OCR/preparo incompleto; imagem nunca serializada. |
| [InvoicePaymentOperationIdentityMigrationTests.cs](../tests/Saldo360.Tests/Infrastructure/InvoicePaymentOperationIdentityMigrationTests.cs) | 1 → 1 | INFRA | Migration histórica preserva pagamentos/identidades de operação, índices e efeitos sem duplicação. |
| [ProductionDeploymentContractTests.cs](../tests/Saldo360.Tests/Infrastructure/ProductionDeploymentContractTests.cs) | 5 → 5 | INFRA | Contratos de Compose/deploy: tag imutável, destino explícito, defaults desabilitados e candidate sem alias ativo. |
| [AccountBalancePersistenceTests.cs](../tests/Saldo360.Tests/Persistence/AccountBalancePersistenceTests.cs) | 7 → 3 | CORE | Agregações SQL numeric exatas, filtros/transferências/cancelados, consultas limitadas e tracker vazio. |
| [AccountProjectionPersistenceTests.cs](../tests/Saldo360.Tests/Persistence/AccountProjectionPersistenceTests.cs) | 4 → 4 | CORE | SQL de pendências e leitura RepeatableRead/read-only: interleaving real independente do timing do scheduler. |
| [BudgetSpreadsheetImporterTests.cs](../tests/Saldo360.Tests/Persistence/BudgetSpreadsheetImporterTests.cs) | 7 → 7 | INFRA | Importador administrativo PostgreSQL: dry-run read-only, execução/proveniência atômica, conflitos e replay. |
| [FamilyInvitationPersistenceTests.cs](../tests/Saldo360.Tests/Persistence/FamilyInvitationPersistenceTests.cs) | 15 → 15 | CORE | Unicidade/constraints, consumo/revogação concorrentes, expiração e rollback de convite social no PostgreSQL. |
| [FamilyPersistenceTests.cs](../tests/Saldo360.Tests/Persistence/FamilyPersistenceTests.cs) | 14 → 14 | CORE, INFRA | Identidade externa ordinal única, concorrência de login, membership social e criação atômica com FKs reais. |
| [FinancialAccountPersistenceTests.cs](../tests/Saldo360.Tests/Persistence/FinancialAccountPersistenceTests.cs) | 23 → 19 | CORE, INFRA | Constraints reais de conta/responsável/workspace/saldo inicial, precisão e preservação histórica na migration. |
| [FinancialBudgetExecutionPersistenceTests.cs](../tests/Saldo360.Tests/Persistence/FinancialBudgetExecutionPersistenceTests.cs) | 2 → 2 | CORE | Agregações eficientes e snapshot real de execução: realização concorrente não desaparece nem duplica. |
| [FinancialBudgetPersistenceTests.cs](../tests/Saldo360.Tests/Persistence/FinancialBudgetPersistenceTests.cs) | 7 → 7 | CORE, INFRA | Unicidade por competência/grupo, categoria da mesma raiz/tipo, dinheiro, restrição de histórico e migration. |
| [FinancialCategoryPersistenceTests.cs](../tests/Saldo360.Tests/Persistence/FinancialCategoryPersistenceTests.cs) | 17 → 17 | CORE, INFRA | Unicidade/collation/acentos inclusive inativas, corridas de insert/rename, constraints e FKs reais. |
| [FinancialDocumentWorkspaceCutoverTests.cs](../tests/Saldo360.Tests/Persistence/FinancialDocumentWorkspaceCutoverTests.cs) | 14 → 14 | INFRA | Ferramenta administrativa de cutover: bytes/hash, caminhos/symlinks, CAS e rollback de referências sem afetar outro workspace. |
| [FinancialPlannedTransactionPersistenceTests.cs](../tests/Saldo360.Tests/Persistence/FinancialPlannedTransactionPersistenceTests.cs) | 21 → 20 | CORE, INFRA | Persistência numeric/date, FKs compostas e constraints/auditoria/anticascade, incluindo dados históricos. |
| [FinancialTransactionPersistenceTests.cs](../tests/Saldo360.Tests/Persistence/FinancialTransactionPersistenceTests.cs) | 30 → 21 | CORE, INFRA | Dinheiro/auditoria e referências de lançamentos garantidos no PostgreSQL, atualização e anticascade. |
| [FinancialTransferPersistenceTests.cs](../tests/Saldo360.Tests/Persistence/FinancialTransferPersistenceTests.cs) | 21 → 17 | CORE, INFRA | Persistência/constraints de transferência (duas contas, dinheiro, auditoria e anticascade), com migration histórica. |
| [FinancialWorkspaceMigrationTests.cs](../tests/Saldo360.Tests/Persistence/FinancialWorkspaceMigrationTests.cs) | 2 → 2 | INFRA | Cutover povoado exige plano autorizado e preserva fatos financeiros, proprietário e identificadores de serviço. |
| [PlannedRealizationPersistenceTests.cs](../tests/Saldo360.Tests/Persistence/PlannedRealizationPersistenceTests.cs) | 7 → 6 | CORE, INFRA | Índice parcial, reativação, FKs e anticascade da realização, preservando vínculos históricos. |
| [PostgresFixtureReliabilityTests.cs](../tests/Saldo360.Tests/Persistence/PostgresFixtureReliabilityTests.cs) | 1 → 1 | INFRA | Infra da própria suíte: clones concorrentes migrados realmente isolados, sem contaminação entre testes. |
| [ServiceAccountPersistenceTests.cs](../tests/Saldo360.Tests/Persistence/ServiceAccountPersistenceTests.cs) | 3 → 3 | INFRA, CORE | Migration/bindings de serviço, unicidade concorrente e FK composta impedindo owner cruzado. |
