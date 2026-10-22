# T09 — Bootstrap do orçamento a partir da planilha

22/10/2026 — **T09.2: importador e dry-run integral implementados; CanExecute=false; importação não executada**.

As seções 1–8 preservam o diagnóstico anterior às extensões. O estado posterior, decisões autorizadas,
alterações e limites da T09.1 estão nas seções 9–13. A T09.2 está nas seções 14–20. Não interpretar
encerramentos históricos como estado atual do importador, nem dry-run como liberação de carga ou deploy.

A especificação manda interromper toda a carga diante de ambiguidades estruturais não previstas.
A leitura da fonte e dos contratos atuais comprovou incompatibilidades de representação; não foi
implementado ou executado importador parcial para contorná-las. A T09 não está concluída.

## 1. Fonte e escopo

Fonte fornecida pelo responsável: [Orçamento Diego 2026-2027](https://docs.google.com/spreadsheets/d/1D9gdAWdNqMc4nv_XwDGExe8OjRe9zrCesIe6_Q-iWfk/edit).
É uma planilha nativa Google Sheets, não um XLSX local. Metadados consultados indicam última alteração
em `2026-10-17T15:53:11.534Z`, locale `pt_BR` e fuso `America/Sao_Paulo`.
Esse timestamp não é um hash nem uma revisão imutável da fonte.

Leituras delimitadas usadas na análise:

- `Parâmetros!A1:D22`;
- `Metas Mensais!A1:L13`, com inspeção dos valores literais/formatos de `B2:F13`;
- `Despesas Fixas!A1:L30`;
- `Lançamentos!A1:Q100`;
- `Histórico compromissos 08-10!A1:J65`;
- `Movimentos Conta!A1:L10`.

Não houve edição da planilha, coleta de Gmail ou consulta aos links de documentos citados nas observações.
As ferramentas conectadas de Drive/Sheets foram usadas somente para leitura. As instruções de análise
de planilhas orientaram a separação de fonte, fórmulas, snapshots e competências; não houve criação de workbook.

Workspace alvo explícito: `01a163b9-4b06-74f8-ac02-05bdc7cfb14e`.
Não selecionar primeiro workspace, provisionar outro ou inferir ownership por Family/autoria.
O subject externo informado na especificação não substitui ApplicationUser.Id, OwnerUserId ou WorkspaceId.
Qualquer execução futura deve revalidar existência, Personal ativo e proprietário persistido, sem alterá-lo.
Não houve nova consulta ao banco do servidor nesta pré-análise; o estado vazio anterior está registrado no
[relatório da recriação](recriacao-limpa-financial-workspace.md), não constitui uma nova verificação desta tarefa.

## 2. Padrões autorizados pelo responsável

Após apontada a ausência de campos obrigatórios dos cartões, o responsável informou limite de R$ 10.000,00,
fechamento dia 5 e vencimento dia 10 e confirmou seu uso como padrão, inclusive vencimento das contas fixas.

- Aplicar aos quatro cartões: Black, Nubank/Nu Empresas, Credicard e Zaffari/Mercado.
- Nas contas fixas sem dia informado, usar vencimento dia 10.
- Preservar dias explícitos: Solar 6, DMAE 11, carro 24, CEEE específica de outubro 15 e IPTU 8.
- Não alterar valores conhecidos das despesas nem inventar saldo, número do cartão ou conta de pagamento.

Esses padrões têm origem na decisão do responsável, não na planilha. Resolvem limite/calendário obrigatório,
mas não resolvem as incompatibilidades abaixo. Campos como emissor ainda precisam de mapeamento fundamentado;
o cadastro atual também exige instituição emissora não vazia.

## 3. Inventário de fonte e mapeamento pretendido

As contagens abaixo são de pré-análise, **não resultado de dry-run de um importador implementado**.
Criados/atualizados nesta tarefa: **zero** em todos os aggregates.

| Grupo | Quantidade identificada/solicitada | Mapeamento e situação |
|---|---:|---|
| Categorias de despesa | 10 | FinancialCategory; dedupe case-insensitive e proveniência ainda precisam ser implementados. |
| Contas | 2 | FinancialAccount Checking/PF e Checking/PJ, BRL. |
| Cartões | 4 | FinancialCreditCard; padrões autorizados, sem cartão chamado 242. |
| Recorrências não-cartão | 11 | FinancialRecurrence: 4 PJ e 7 PF, respeitando início/fim. |
| Recorrências de cartão | 6 | Sem representação equivalente no contrato atual de recorrências. |
| Previsões específicas | 7 | FinancialPlannedTransaction; não realizadas/pagas. |
| Parcelamentos | 34 linhas | 32 grupos após juntar apenas os dois pares explicitamente divididos (Kalunga e Amazon). Não significa 32 purchases válidas. |
| Ocorrências parceladas conhecidas | 107 | Soma da quantidade desde a competência nas 34 linhas, não quantidade original de parcelas das compras. Nenhuma foi gerada. |
| Compras únicas de cartão | 4 solicitadas; 5 na fonte | Uma linha adicional exige decisão; ver seção 5. |
| Orçamentos mensais | 12 | Out/2026–set/2027; metas agregadas sem mapeamento equivalente atual. Número de targets de destino indefinido. |
| Parâmetros de planejamento | Conforme especificação | Importar só equivalências reais; não converter salário/hora, envelopes ou snapshots em despesas fictícias. |

`Lançamentos` contém 50 registros: 34 Parcelado, 6 Recorrente e 10 Único.
Os dez únicos são cinco de cartão, quatro históricos sem cartão e um exemplo Ignorar.
Não foram encontrados IDs repetidos entre as 50 linhas. Os 37 IDs do Histórico estão todos presentes em
Lançamentos: não carregar o Histórico como segunda lista independente. Não foi verificada duplicidade no
destino; isso exige a futura leitura do workspace e um mecanismo de proveniência estável.

Para Itaú PF, a especificação escolhe o snapshot **R$ -13.826,92 em 08/10/2026**, correspondente ao último
saldo conhecido em Movimentos Conta. O parâmetro antigo de R$ -15.284,39 não é o saldo escolhido.
Itaú Empresas permanece sem saldo inicial. O domínio interpreta saldo inicial como início da data-base;
a carga solicitada não pode reintroduzir os movimentos até 08/10 já absorvidos pelo snapshot.

## 4. Bloqueios comprovados no código

### Recorrência de cartão não existe no contrato atual

[RecurrenceInput/Dto](../src/Saldo360.Application/Recurrences/FinancialRecurrenceDtos.cs) recebem AccountId,
não CreditCardId. O [serviço](../src/Saldo360.Application/Recurrences/FinancialRecurrenceService.cs)
exige uma conta válida e materializa previsões bancárias. Usá-lo para as seis assinaturas de cartão
mudaria a natureza da obrigação e poderia duplicar a saída bancária no pagamento da fatura.
Criar compras mensais avulsas também não entrega a recorrência de cartão solicitada.

### Metas agregadas não são metas por categoria

[FinancialBudgetTarget](../src/Saldo360.Domain/Budgets/FinancialBudgetTarget.cs) exige CategoryId;
BudgetTargetGroupingType só contém Category. O total do orçamento é a soma dos targets ativos no
[repositório](../src/Saldo360.Infrastructure/Budgets/FinancialBudgetRepository.cs).

Não existe mapeamento direto para fixos PF R$ 7.000, cartões R$ 5.000 e total PF R$ 12.000 sem inventar
categorias, repartir valores sem autorização ou contar o total como uma terceira despesa/meta somável.
A recuperação aprovada em reais também não equivale ao RecoveryTargetPercentage de
[FinancialPlanningMonthlyParameter](../src/Saldo360.Domain/Planning/FinancialPlanningScenario.cs).
Guardar esses valores apenas em Notes não atende à entrega funcional de metas/targets.

### Purchase atual não representa qualquer cronograma histórico explícito

[FinancialCreditCardPurchase](../src/Saldo360.Domain/CreditCards/FinancialCreditCardPurchase.cs) exige total,
data da compra e quantidade original e gera parcelas de 1 até N por divisão igual de centavos.
O [Application Service](../src/Saldo360.Application/CreditCards/FinancialCreditCardPurchaseService.cs)
também escolhe competências a partir da data/calendário do cartão, sem entrada de cronograma individual.

Há linhas intermediárias como 7/10: a quantidade restante não é uma nova compra de quatro parcelas.
Não inferir total original multiplicando parcela, data original ou histórico desconhecido.
Mesmo Kalunga, cujo cronograma completo conhecido soma R$ 335,30, diverge do rateio automático:

| Parcela | Fonte | Rateio atual de R$ 335,30 em 3 |
|---|---:|---:|
| 1/3 | R$ 111,78 | R$ 111,77 |
| 2/3 | R$ 111,76 | R$ 111,77 |
| 3/3 | R$ 111,76 | R$ 111,76 |

Existe outro aggregate, [FinancialCreditCardImportedInstallment](../src/Saldo360.Domain/CreditCards/FinancialCreditCardImportedInstallment.cs),
para obrigações históricas sem reconstruir a compra. Porém seu
[repositório](../src/Saldo360.Infrastructure/CreditCards/FinancialCreditCardImportedInstallmentRepository.cs)
exige fatura correspondente existente. Não cria FinancialCreditCardPurchase nem satisfaz automaticamente
a exigência da T09 de Purchase + Installments. Avaliar esse caminho requer aprovar explicitamente o mapeamento,
a preparação legítima das faturas e as datas, sem faturas artificiais por totais consolidados.

### Proveniência e atomicidade da carga ainda não estão implementadas

Os contratos de contas, categorias, recorrências, compras e metas examinados não possuem o conjunto genérico
SourceSystem/SourceDocument/ExternalReference da T09. O importador histórico tem ImportId/RowNumber/ExternalId,
mas isso não cobre os demais aggregates. Não há importador administrativo desta carga implementado.
Não alegar idempotência usando apenas descrição/valor ou existência de um nome.

Antes de executar, definir proveniência durável por workspace/fonte/ID, comportamento em fonte alterada e
preflight completo. Chamadas API sequenciais que já gravam os primeiros cadastros antes de descobrir um
bloqueio posterior não atendem à condição de abortar antes de escrita por ambiguidade estrutural.

## 5. Divergências da fonte a decidir

1. `Lançamentos!A51:Q51` contém uma quinta compra única de cartão de R$ 33,90 (controle remoto), ausente
   das quatro compras especificadas. A própria observação declara o cartão como inferido, a conferir.
   Não incluída nem descartada automaticamente; decidir escopo e cartão antes da carga.
2. As metas de recuperação em `Metas Mensais!E2:E13` são números literais, não fórmulas, mas vários têm
   mais de duas casas decimais; por exemplo, outubro tem valor bruto aproximadamente 1270,376 e exibição
   R$ 1.270,38. Preservar o bruto como evidência e aprovar a precisão monetária de destino; não apresentar
   arredondamento como preservação numérica exata nem recalcular o snapshot pelos lançamentos.
3. A provisão alimentação/imprevistos descreve envelope de consumo, não despesa adicional cumulativa.
   Não somar automaticamente R$ 2.000 aos compromissos já registrados nem duplicar a meta de cartões.
4. Parâmetros sem campo correspondente ficam sem mapeamento até decisão: valor/hora PJ, horas/dia,
   reserva tributária percentual e subenvelopes não são receita/despesa efetiva. O percentual de recuperação
   tem campo conceitualmente próximo em cenários, mas cenário/semântica precisam ser definidos sem confundir
   esse percentual com o valor aprovado mensal em reais.

## 6. Exclusões deliberadas previstas

- PF-007 Plano de saúde inativo não vira recorrência ativa.
- Exemplo Ignorar e movimentos históricos absorvidos no saldo-base não viram transações.
- Histórico repetido não vira segunda carga dos mesmos IDs.
- Painel, Resumo Mensal, totais de Cartões Mensal e projeções calculadas não viram fatos financeiros.
- Não criar pagamento/fatura fechada de outubro a partir de total histórico, nem marcar despesa como paga.
- Não criar Family/Shared, alterar owner, habilitar automações ou restaurar os documentos antigos.

## 7. Dry-run, execução e validação

**Dry-run do importador: não disponível. Execução: não realizada. Idempotência: não demonstrada.**
A análise somente leitura da fonte não substitui o comando zero-write exigido com --workspace-id,
--source, --dry-run e --execute. Não há contagens de Created/Skipped no destino a declarar como validadas.
Nenhuma categoria, conta, compra, fatura, recorrência, previsão ou meta foi criada nesta tarefa.

Validação realizada: leitura delimitada da fonte, conferência de IDs/contagens, inspeção dos contratos
e cálculo demonstrativo em centavos do rateio Kalunga. Apenas este relatório local foi acrescentado.
Sem build, testes funcionais, CI, push, deploy, reset, importação, alteração de flags ou escrita no servidor.
Não foi revalidada a saúde/contagem financeira do ambiente nesta pré-análise.

## 8. Decisão necessária para prosseguir

Os padrões fornecidos pelo responsável foram registrados e não serão perguntados novamente.
Resta aprovar como atender às lacunas: extensão explícita e limitada do modelo/contratos para recorrências
de cartão, metas agregadas/snapshots e cronogramas conhecidos, ou revisão formal do escopo/mapeamento da carga.
Não habilitar uma importação parcial como solução implícita.

Uma vez decididas as representações e a quinta compra, implementar o importador com proveniência/testes,
produzir dry-run integral para revisão e somente então executar a carga autorizada. Não iniciar refatoração
transversal nem adaptar regras financeiras apenas para reproduzir fórmulas da planilha.

## 9. T09.1 — decisão e implementação mínima

A especificação T09.1 autoriza resolver os três bloqueios de domínio e preparar proveniência/contratos,
**sem implementar ou executar o importador T09**. Os testes usam dados exclusivamente sintéticos em bases
descartáveis; nenhum dado financeiro do responsável foi cadastrado. Não houve conexão ao servidor nesta etapa.

### Recorrências: exatamente um destino

FinancialRecurrence e seus contratos aceitam AccountId **ou** CreditCardId, nunca ambos/nenhum.
O domínio, Application Service e CHECK PostgreSQL aplicam a exclusividade. Cartão precisa estar ativo no
mesmo workspace, categoria deve ser despesa e autorização continua sendo a do owner do Personal.
Frequência, intervalo, valor, estimativa e limites de início/fim continuam na definição.

Recorrências bancárias mantêm a materialização de FinancialPlannedTransaction. Recorrências de cartão
materializam uma obrigação por ocorrência pelo domínio normal de compra à vista + parcela, com o
calendário normal de fechamento/vencimento. Só esse fluxo legítimo cria a fatura correspondente à cobrança;
não é criação de total artificial extraído da planilha. Não cria transação bancária nem pagamento.
A data da ocorrência de cartão é a data programada da cobrança, não o vencimento bancário nem um artifício
para forçar competência. O importador deverá validar sua correspondência com a competência conhecida.

FinancialRecurrenceMaterialization passa a apontar para PlannedTransactionId **ou** CreditCardPurchaseId.
A identidade workspace/recorrência/número, lock e unicidade impedem materialização repetida. Não trocar
o cartão ou o tipo de destino depois de materializar. A relação persistida mantém a origem na recorrência;
o mecanismo compartilhado de proveniência registrará a definição da fonte na futura carga.
O rateio normal das compras parceladas não foi alterado.

### Metas agregadas: snapshots separados

[FinancialMonthlyAggregateTarget](../src/Saldo360.Domain/Budgets/FinancialMonthlyAggregateTarget.cs) usa
unicidade `(WorkspaceId, Competence, TargetType)`, competência ancorada no primeiro dia e valor monetário
não negativo com até duas casas. Tipos iniciais: FixedExpenses, CreditCards, TotalExpenses e RecoveryAmount.
Não possui método de soma de tipos. TotalExpenses é um snapshot independente, não um terceiro componente.
FinancialBudget/FinancialBudgetTarget por categoria continuam com seu significado e cálculo atuais.

[ApprovedRecoverySnapshot](../src/Saldo360.Application/Bootstrap/ApprovedRecoverySnapshot.cs) converte
somente o valor bruto informado: arredondamento decimal para duas casas, midpoint AwayFromZero.
Metadata preserva o texto numérico bruto e a política usada. Não consulta lançamentos nem recalcula metas.
O valor monetário pronto e essa metadata devem ser fornecidos juntos pelo futuro importador.

### Parcelas conhecidas: sem compra/fatura inventadas

FinancialCreditCardImportedInstallment ganhou modo explícito com Competence, DueDate opcional,
SourceSystem e ExternalReference. InvoiceId, TransactionDate, números e identificadores do fluxo antigo
podem ser nulos quando realmente desconhecidos. Não usar Guid.Empty, data fictícia, parcela 1 ou quantidade
remanescente como substitutos de fatos ausentes. Os campos obrigatórios do fluxo antigo continuam validados.

[KnownCardInstallmentInput](../src/Saldo360.Application/Bootstrap/IFinancialBootstrapExtensions.cs) aceita
valor exato por ocorrência. O serviço valida owner/cartão/categoria no mesmo W e, quando DueDate não é
informada, deriva o dia do calendário aprovado dentro da competência conhecida, sem deduzir compra original.
Kalunga pode manter 111,78 / 111,76 / 111,76; uma parcela 7/10 continua 7/10. Números desconhecidos ficam nulos.
Nenhuma purchase ou fatura é exigida/criada para persistir esse registro.

Obrigações conhecidas sem fatura participam do limite, projeção por conta, evolução/mensal e execução por
categoria, sem duplicar as que já têm InvoiceId. Sem conta de pagamento ou data atribuível, a projeção fica
explicitamente incompleta, em vez de inventar uma alocação.
Há comando de Application para vínculo **explícito** a fatura real já existente, somente com W/cartão/
competência/vencimento compatíveis, sem pagamentos ativos. Não cria a fatura e não preenche data original
da compra. Após vínculo, os totais/pagamentos normais usam essa mesma obrigação, sem segunda saída bancária.

## 10. Proveniência e contratos para T09

[FinancialBootstrapProvenance](../src/Saldo360.Domain/Bootstrap/FinancialBootstrapProvenance.cs) registra
WorkspaceId, SourceSystem, SourceDocument, ExternalReference, EntityType/EntityId, ContentHash e MetadataJson.
Unicidade PostgreSQL: `(WorkspaceId, SourceSystem, SourceDocument, ExternalReference)`, inclusive após
cancelamento do registro financeiro. Para a carga futura, usar BudgetSpreadsheet / Orçamento Diego 2026-2027
e IDs estáveis da fonte, com sufixo explícito de ocorrência/subentidade quando uma linha gerar vários registros.
Isso não é chave por descrição/valor e não usa Family ou subject OIDC como escopo.

Os novos comandos de metas/parcelas salvam aggregate e proveniência juntos na mesma transação sob lock
do workspace. Mesmo conteúdo retorna AlreadyImported e mesmo EntityId; mesma chave com conteúdo distinto
retorna Conflict, sem UPDATE silencioso. Cinco chamadas concorrentes devem gerar somente um registro.
Fingerprint versionado usa contratos tipados e preserva metadata; escala decimal semanticamente equivalente
não muda o hash. Alteração da evidência bruta é alteração de conteúdo, não arredondamento silencioso.

O repositório de proveniência também permite **staging sem commit isolado**, para a futura aplicação dos
demais Application Services no mesmo contexto/transação. Ele não autoriza gravar marcadores desacoplados
dos aggregates nem substitui a autorização do caso de uso. EntityType/EntityId é referência de rastreabilidade,
não uma nova relação de ownership. Não foi criada infraestrutura genérica de ETL ou endpoint de importação.

[BootstrapContracts](../src/Saldo360.Application/Bootstrap/BootstrapContracts.cs) define Created,
AlreadyImported, Unmapped, Conflict e Invalid, itens por aggregate/referência e contagens. Em dry-run,
Created significa **seria criado**, não escrita executada. Inspeção de proveniência é consultiva.
CanExecute é somente elegibilidade técnica, nunca autorização nem aprovação de dry-run. Unmapped só pode
deixar de bloquear quando a exclusão estiver explicitamente aprovada; Invalid/Conflict continuam bloqueantes.
O futuro importador ainda precisa implementar o preflight integral zero-write e o plano revisável.

Decisões de fonte da T09.1 que substituem as pendências da seção 5:

- Controle remoto R$ 33,90: **não importar**, apresentar Unmapped/NeedsReview com exclusão aprovada.
- Recuperação: preservar bruto em metadata e persistir snapshot arredondado, sem recalcular.
- Manter os padrões autorizados da seção 2 e as datas explícitas.
- Não inventar mapeamento para parâmetros sem campo correspondente, nem duplicar envelopes/metas.

## 11. Migration e impactos operacionais

Única migration nova: [20261022165204_AddBudgetBootstrapExtensions](../src/Saldo360.Infrastructure/Persistence/Migrations/20261022165204_AddBudgetBootstrapExtensions.cs).
Cria as duas tabelas novas, adiciona campos/FKs/CHECKs e torna anuláveis somente os vínculos necessários
de recorrência/materialização/parcela conhecida. Migrations históricas não foram reescritas.
Registros bancários e importações antigas continuam representáveis, sem backfill de valores financeiros.

As duas tabelas novas recebem o trigger existente de coordenação com reset/writers. O inventário do reset
descobre suas dependências pelo modelo e inclui metas agregadas e proveniência; não deixar dedupe apontando
para dados removidos após um reset autorizado. A nova FK de materialização também respeita a ordem de exclusão.
Down recusa dados novos que não cabem no schema anterior; não apagar metas/proveniência nem inventar IDs/datas
para forçar downgrade. Migration não aplicada no servidor ou em bases persistentes nesta tarefa.

API de recorrências tem campos adicionais; contratos antigos de conta continuam aceitos. Dinheiro REST
continua texto decimal. Frontend recebeu somente ajustes de compatibilidade: identifica destino cartão,
não abre o formulário bancário para convertê-lo e reconhece materialização de cartão/novos itens do reset.
Não foi criada UI nova de metas agregadas, importador ou seletor/cadastro de recorrência de cartão.
O fluxo bancário existente permanece disponível. Esses novos comandos são portas de Application para T09,
não rotas administrativas públicas habilitadas.

## 12. Validação local da T09.1

- Backend: regressão completa final **888/888**, sem ignorados, em **15 min 32 s**, com PostgreSQL 17
  descartável. Revalidação dirigida **59/59**, sem ignorados, em 1 min 52 s, incluindo recorrências,
  parcelas importadas, novas extensões, migrations, reset e concorrência.
- Frontend: **289/289 em 30 arquivos** na rodada final com dois workers; build aprovado. Lint sem erros,
  com nove avisos preexistentes. Inclui compatibilidade da apresentação de recorrência de cartão.
- Compilação backend/solução sem warnings/erros; EF confirma ausência de alterações de modelo pendentes.
- Diff/formatação conferidos, inclusive whitespace dos arquivos novos. Artefatos locais em
  `.artifacts/t09-tests/t09-1-final-regression.trx` e `t09-1-final-directed.trx`, fora do Git.
- A primeira regressão ampla teve 874/885 aprovados: dez falhas decorreram de uma constraint nova que
  restringia row_number válido no schema antigo; a constraint foi corrigida sem alterar essa linha antiga.
  A outra falha era parâmetro GroupingType ausente no novo teste de integração, corrigido no teste.
  Os onze casos passaram na revalidação dirigida. A fixture de reset foi ampliada para popular também
  as duas tabelas novas, e a verificação de inventário passou de 30 para 32 grupos, sem reduzir cobertura.
- Uma rodada frontend teve falha no teste preexistente de conciliação de dívidas (busca imediata de alert).
  Não foi alterado esse componente/teste; a execução completa com paralelismo limitado passou 289/289.

Cobertura nova: conta/cartão exclusivos no domínio/API, materialização de cartão idempotente e sem banco/
pagamento, snapshots independentes e únicos, bruto/arredondamento da recuperação, números desconhecidos
preservados, cronograma explícito de centavos, ausência de compra/data/fatura inferidas, conflito de fonte,
cinco chamadas concorrentes, isolamento por owner/W, inspeção consultiva, exclusão aprovada no contrato
de dry-run, projeção/limite/execução por categoria, vínculo explícito a fatura real e reset dos novos dados.
O ensaio de migration preserva uma importação válida de linha 1 no schema anterior, com os mesmos ID,
valor, numeração, data e fatura, sem backfill. Down com snapshot novo é recusado e preserva os registros.

As rodadas dirigidas se sobrepõem à ampla e não são somadas como testes únicos. Todos os cenários de
escrita/reset usam PostgreSQL 17 descartável e dados sintéticos. Não houve CI remota, teste de navegador
real nem ensaio com dados do responsável nesta T09.1.

## 13. Limite da entrega e pendências de T09

Nenhum importador/script de execução da planilha foi implementado ou chamado. Não foi executado dry-run
produtivo, bootstrap financeiro, reset do ambiente, deploy, push ou alteração de flags/owner/família.
Não foram acessados banco/storage produtivos nem habilitadas automações. O ambiente do responsável não
recebeu os dados dos testes.

**T09.1 concluída localmente: domínio e contratos prontos para implementar o importador T09.**
Isso não conclui T09: ainda faltam snapshot/manifesto estável da fonte, mapeamento fundamentado de campos,
orquestração dos Application Services/proveniência, preflight integral, dry-run aprovado e execução própria.
Não inferir data original, número ausente, emissor ou conta pagadora para preencher uma validação.
Não somar totais derivados, não duplicar histórico absorvido pelo saldo-base e não cadastrar parâmetros
sem correspondência funcional como se estivessem suportados. Nenhuma importação parcial foi autorizada
por consequência destas extensões. **Parar ao concluir T09.1; não iniciar a carga automaticamente.**

## 14. T09.2 — importador administrativo

Implementado projeto console separado [Saldo360.BudgetBootstrap](../src/Saldo360.BudgetBootstrap/Program.cs),
com wrapper [import-initial-budget.ps1](../scripts/import-initial-budget.ps1).
Não há endpoint, DI na API, job ou execução automática em deploy/login/reset.
A migration T09.1 está integrada ao código, mas **não foi aplicada no servidor**.

Componentes: [snapshot](../src/Saldo360.Application/Bootstrap/SpreadsheetSnapshot.cs),
[mapper](../src/Saldo360.Application/Bootstrap/BudgetSpreadsheetMapper.cs),
[grafo de domínio](../src/Saldo360.Infrastructure/Bootstrap/SpreadsheetDomainGraph.cs),
[preflight/orquestrador](../src/Saldo360.Infrastructure/Bootstrap/BudgetSpreadsheetImporter.cs) e
[snapshot mínimo do destino](../src/Saldo360.Infrastructure/Bootstrap/ReadOnlyBudgetDestinationSnapshot.cs).
O grafo valida o conjunto em memória pelas factories/calendário normais, sem DbContext ou motor financeiro novo.

**Nenhum `--execute` foi chamado no ambiente persistente.** Escritas do caminho futuro foram ensaiadas
somente em PostgreSQL descartável com fonte sintética. Created significa “seria criado”, não gravação.

## 15. Snapshot estável e manifesto privado

Captura nativa em **2026-10-22T18:17:50Z**, após duas leituras iguais dos mesmos ranges e metadados
conferidos antes/depois. Última alteração declarada pelo Drive: `2026-10-17T15:53:11.534Z`, inalterada na
conferência final. Esse timestamp não é revisão Google imutável; a reprodução usa o snapshot local congelado.

Ranges: `Parâmetros!A1:D22`, `Metas Mensais!A1:L13`, `Despesas Fixas!A1:L30`, `Lançamentos!A1:Q100`,
`Histórico compromissos 08-10!A1:J65`, `Movimentos Conta!A1:L10`. Fórmulas, efetivos e bruto numérico são
separados. Painel/Resumo/totais consolidados não foram fontes de fatos. Não houve edição da planilha nem
consulta aos documentos/links presentes nas observações.

Hash lógico SHA-256 (JSON UTF-8 compacto com identidade, ranges ordenados, células, fórmulas e bruto;
exclui timestamps de transporte e apresentação):
`94332027a04f07feb9f14c729e59662c65f59ec08eefc4a07bd0df6009815c32`.

Artefatos privados sob `.artifacts/budget-bootstrap/`, fora do Git e do contexto Docker:

- `source-20261022.json`: snapshot; SHA-256 físico
  `9a206d40ec2b4fec4bdd94e8effd1be06da46e09ac9fcdd684ba46276816f609`.
- `destination-post-validation-20261022.ndjson`: SELECTs finais do destino; SHA-256 físico
  `d0d8fa972f8bb4905d8787fad54067413c40258a96367c09bb8608f9cf8922b8`.
- [dry-run-t09-2-final-20261022.json](../.artifacts/budget-bootstrap/dry-run-t09-2-final-20261022.json):
  relatório definitivo local; SHA-256 físico
  `762a9b7312a7e27e71fd45769f2f1b3ddf9f7c17a9f307d8fea88cc82266a97f`.
- `manifest-final-20261022.json`: índice privado dos arquivos, hashes, timestamps e modos.

PlanHash: `4082af2235f688acc4923d64584b2da24ca299ee0bab26943d92e560fe6d1aa8`.
ReportHash: `a065d8ad99067dfa5d4575815ef8cec794c4da614ee9a97c8115774553491c49`.
Hash lógico do snapshot de destino:
`6a01bc479fe9b76103ad5028bd399dcfe61676d3a9420d874d6d18d8cb04d727`.

O relatório integral lista **284 itens**, com ExternalReference, EntityType, descrição em Payload.Name,
valores/datas/referências, IntendedAction, Status, Reason, SourceLocation e proveniência. Não inclui notas
originais com UC/Gmail, tokens ou documentos. Hash físico do arquivo não se confunde com hash lógico.

## 16. Dry-run real e mapeamento

Alvo exclusivo: `01a163b9-4b06-74f8-ac02-05bdc7cfb14e`. Modo: **ReadOnlyDestinationSnapshot**.
O envio da captura completa a um helper remoto foi barrado pela revisão automática por risco de exposição;
não houve upload nem criação desse helper. A alternativa executada manteve a planilha local e exportou
somente identidade/schema e contagens do alvo, pelo [leitor SQL](../scripts/read-initial-budget-destination.sql).
Não apresentar essa modalidade como conexão direta da CLI ao servidor.

As leituras PostgreSQL de **18:41:39Z**, **18:57:38.410016Z** e **19:35:06.984493Z** usaram `REPEATABLE READ READ ONLY`,
confirmando `transaction_read_only=on`. Todas mostraram Personal ativo, owner persistido válido e inalterado,
**42 migrations**, todos os cadastros/fatos financeiros vazios, documentos/checkpoints/bindings zero,
uma auditoria preexistente de reset vazio e zero intenções de cleanup. As contagens permaneceram iguais
entre as três capturas; a última compõe o relatório definitivo.
Tabelas T09.1 ausentes são indisponíveis (`-1`), **não zero conhecido**.

| Aggregate planejado | Quantidade | Mapeamento |
|---|---:|---|
| Categorias Expense | 10 | Nomes canônicos case-insensitive. |
| Contas | 2 | Itaú PF -13.826,92 em 08/10; Empresas sem saldo inicial. |
| Cartões | 4 | Black/Itaú e Nubank com evidência; dois emissores bloqueados. |
| Recorrências em conta | 11 | AccountId; início/fim e dias explícitos/default 10. |
| Recorrências em cartão | 6 | CreditCardId; quatro calendários coerentes, duas datas ausentes. |
| Previsões específicas pendentes | 7 | Estimativa/provisão em metadata; sem realização. |
| Compras normais | 9 | Cinco parceladas completas + quatro únicas aprovadas. |
| Parcelas normais | 22 | 18 parceladas + quatro únicas, pelo rateio normal. |
| Faturas derivadas de compras | 8 | Black/Nubank, nov/2026–fev/2027; nenhum total histórico de outubro. |
| Parcelas históricas conhecidas | 89 | Sem inventar compra/data/total; 15 dependem do Credicard bloqueado. |
| Metas agregadas | 48 | Quatro snapshots independentes por competência, por 12 meses. |
| Proveniências planejadas | 216 | Uma por entidade, inclusive parcelas/faturas; nenhuma persistida. |

As 34 linhas parceladas formam 32 grupos: cinco compras completas + 27 históricos. As 107 ocorrências
conhecidas são **18 parcelas normais + 89 históricas**, sem duplicar Kalunga/Amazon. Kalunga mantém
111,78 / 111,76 / 111,76; 7/10 continua 7/10–10/10, não compra nova de quatro parcelas. As cinco compras
completas são cama, dois presentes, Amazon e Ozempic. Os quatro únicos aprovados ficam nas compras, não no banco.

Recorrências são somente definições, sem materialização automática. Evidências de cobrança: iCloud 05/10,
ChatGPT 27/09, Kindle 23/09 e Prime sem anúncios 12/09, coerentes com competência outubro no calendário 5/10.
Claude e manutenção Zaffari não têm dia suficiente; não usar o primeiro dia da competência como cobrança.
Limite/fechamento/vencimento dos quatro cartões: 10.000 / 5 / 10; sem número, conta pagadora ou titular inventados.

Metas não geram FinancialBudgetTarget por categoria nem somam TotalExpenses aos componentes. RecoveryAmount
preserva o bruto entregue pela API em metadata e arredonda somente o snapshot para duas casas, AwayFromZero;
não recalcula pelos gastos. Não se alega recuperar as teclas originalmente digitadas no Google Sheets.

## 17. Status e bloqueios revisáveis

**CanExecute=false. Zero escritas financeiras. Não há autorização de carga parcial.**

| Status | Itens |
|---|---:|
| Created (somente plano) | 197 |
| AlreadyImported | 0 |
| Unmapped | 67 |
| Conflict | 0 |
| Invalid | 20 |

Cinco causas-raiz dos Invalid:

1. `CARD:Credicard`: MissingIssuerEvidence; não inferir banco pela marca.
2. `CARD:Zaffari/Mercado`: MissingIssuerEvidence; não inferir instituição pelo nome comercial.
3. `REC-003:recurrence`: MissingChargeDate; competência de Claude + IOF não comprovável.
4. `REC-ZAF-001:recurrence`: MissingChargeDate; idem para manutenção Zaffari.
5. Destino: T09.1MigrationNotApplied; código integrado não significa schema implantado.

Os outros 15 Invalid são `PAR-CR-001:installment:4..6`, `PAR-CR-002:installment:4..5` e
`PAR-CR-003:installment:3..12`, por InvalidCardDependency. Valores/competências estão no plano, sem descarte
ou transferência para outro cartão. Não há outro Invalid de parsing no relatório definitivo.

As **67 exclusões Unmapped são explicitamente aprovadas pelo escopo**:

- 37 IDs do Histórico já representados em Lançamentos;
- oito movimentos até 08/10 e quatro lançamentos bancários absorvidos pelo saldo-base;
- exemplo Ignorar, plano de saúde inativo e controle remoto R$ 33,90: um cada;
- 15 parâmetros: oito UnsupportedButKnown e sete referências já mapeadas/substituídas/não factuais.

UnsupportedButKnown: valor/hora PJ, horas/dia, reserva tributária percentual, referência percentual de
recuperação, submeta de parcelas/recorrências, submeta alimentação/imprevistos, provisão de consumo e início.
Não são despesas/receitas adicionais. Referências incluem metas já capturadas, reserva Solar, saldo inicial
antigo substituído, mês inicial e horizonte. Parâmetro desconhecido não recebe exclusão aprovada automática.
Controle remoto mantém NeedsReview com exclusão aprovada e, isoladamente, não impediria CanExecute.

## 18. Interface, proveniência e atomicidade futura

Reprodução local, sempre com **arquivo de saída novo**:

```powershell
./scripts/import-initial-budget.ps1 --workspace-id 01a163b9-4b06-74f8-ac02-05bdc7cfb14e --source .artifacts/budget-bootstrap/source-20261022.json --destination-snapshot .artifacts/budget-bootstrap/destination-post-validation-20261022.ndjson --dry-run --output .artifacts/budget-bootstrap/novo-dry-run.json
```

Sem snapshot de destino, exige ConnectionStrings__Saldo360 explícita e força conexão/transação somente
leitura. Não há fallback, ensure de workspace ou migration automática. Destino não vazio exige inspeção
direta de proveniência/conteúdo; a modalidade de snapshot nunca alega AlreadyImported só por nome/contagem.

`--execute` está preparado, **não utilizado no ambiente persistente**. Exige relatório executável e seu
hash aprovado, além de `--fresh-source`: captura posterior à revisão e com até cinco minutos. Mudança de
SourceId, ModifiedAt ou hash lógico exige novo dry-run. A captura atualizada deve ser refeita pela integração
nativa somente leitura, não copiada com timestamp editado. A CLI lê snapshots, não autentica no Google
por conta própria nem declara revisão Google criptograficamente atestada.

O caminho futuro recalcula o plano e revalida diretamente o destino/owner sob o lock de workspace usado
pelos writers/reset. Só então faz staging de todos os aggregates e proveniências no mesmo DbContext,
um SaveChanges e uma transação. Falha reverte o conjunto, sem commit individual por aggregate. Não
materializa recorrências, realiza previsões, paga faturas ou escreve arquivos.

SourceSystem/SourceDocument: BudgetSpreadsheet / Orçamento Diego 2026-2027. IDs de cada linha são mantidos
com sufixos, inclusive nos pares Kalunga/Amazon. Contas/categorias/cartões sem ID na fonte recebem chaves
lógicas explícitas `ACCOUNT:...`, `CATEGORY:...`, `CARD:...`; faturas usam cartão/competência derivados de
compra real. Mesmo conteúdo e entidade inalterada: AlreadyImported; conteúdo/entidade editada/removida ou
referência fora de W: Conflict. Não deduplicar por descrição+valor nem atualizar dados do usuário silenciosamente.

## 19. Conferências e ausência de efeitos no ambiente

- Histórico não duplicado; movimentos até 08/10 não reimportados; saldo-base PF no plano **-13.826,92**.
- Nenhum cartão 242; controle remoto excluído; plano de saúde não ativado.
- Compras não viram FinancialTransaction; nenhum pagamento/transação bancária planejado.
- Nenhuma fatura histórica artificial, total de Painel/Resumo ou envelope adicional importado.
- Nenhum aggregate, proveniência, owner ou workspace gravado no servidor. Auditoria preexistente preservada.
- Somente SELECTs no banco, com read-only confirmado. Sem migration, reset, cutover, PDF, Gmail,
  reanalysis, confirmação, binding, alteração de flags ou habilitação de automações.
- Sem commit, push, CI remota ou deploy nesta T09.2. Snapshots/relatórios completos privados locais.
- Conferência consultiva adicional: API/web/PostgreSQL healthy; seis fontes de coleta e flags de e-mail,
  classificação, extração, reconciliação, confirmação, binding e agentes consultadas permaneceram false.

## 20. Validação T09.2 e parada

Testes usam fonte sintética, IDs contratuais e casos de borda aprovados, nunca export privado da planilha.
Cobrem parsing/hash/mudança de fonte, duplicatas, emissores, datas/calendários, conta/cartão, compras
completas/históricas, Kalunga/7-10, bruto/arredondamento, exclusões, saldo-base, proveniência/idempotência,
edição de destino, isolamento, zero-write e rollback após falha durante SaveChanges. Snapshot mínimo:
schema ausente não é vazio; owner/inativo/Shared/destino não vazio bloqueiam.

- Novos testes dirigidos: **34/34**, sem ignorados, em 18 s, com PostgreSQL 17 descartável.
  Artefato: `.artifacts/t09-2-tests/t09-2-final-directed-v3.trx`.
- Build Release de toda a solução aprovado, sem warnings/erros. Nenhuma alteração de modelo/migration nova.
- Frontend: **289/289 em 30 arquivos**, build aprovado e lint sem erros (nove avisos preexistentes).
- Wrapper PowerShell validado: replay dos mesmos snapshots produziu SourceHash/PlanHash/ReportHash
  idênticos e os mesmos 284 itens. Manifesto inclui também hashes dos assemblies Release revisados.
- Diff/whitespace conferidos, incluindo arquivos novos. Snapshots privados continuam ignorados pelo Git.
- A primeira regressão ampla teve 916/921 aprovados: os 888 casos existentes passaram; cinco falhas
  dos casos novos decorreram da fixture Amazon ainda sem o descritor explícito de parcela. Fixture corrigida,
  sem alterar valores da fonte ou flexibilizar as validações. A rodada dirigida final acima passou integralmente.
- Houve uma tentativa de rebuild dirigido concorrente com esse testhost que parou por lock de DLL no
  Windows; nenhuma alteração funcional foi feita para contorná-lo. A validação posterior foi serializada.
- Regressão backend completa: **922/922**, sem ignorados, em **17 min 56 s**;
  `.artifacts/t09-2-tests/t09-2-final-regression-v2.trx`.
  O último ajuste, posterior ao início dessa rodada, preserva a descrição da própria linha FUT de Kalunga
  em vez de repetir o rótulo 1/3 de OUT. Não altera valores/numeração/contagens. Foi revalidado nos mesmos
  **34/34** casos dirigidos após o ajuste (`t09-2-final-description-directed.trx`, Debug, 34 s).
  Build Release final da solução novamente aprovado, sem warnings/erros; revalidação final também em
  **Release: 34/34**, sem ignorados, em 17 s (`t09-2-final-release-directed.trx`).
  As rodadas se sobrepõem: não somar seus números como testes únicos adicionais.

**Parar na apresentação do dry-run.** Resolver os quatro dados de cartão, revisar/aprovar a implantação
T09.1 separadamente e produzir novo dry-run executável para autorização explícita. Este relatório,
com CanExecute=false, **não autoriza `--execute`**.
