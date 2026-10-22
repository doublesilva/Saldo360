# T09 — Bootstrap do orçamento a partir da planilha

22/10/2026 — **T09.1 implementada e validada localmente. Domínio pronto para implementar o importador T09; importação não executada**.

As seções 1–8 preservam o diagnóstico anterior às extensões. O estado posterior, decisões autorizadas,
alterações e limites da T09.1 estão nas seções 9–13. Não interpretar o diagnóstico histórico como descrição
dos novos contratos nem esta implementação local como liberação de importação ou deploy.

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
