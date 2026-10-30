# R00 — Limpeza de escopo e preparação da reconstrução

Primeira etapa da reconstrução funcional e visual do Saldo360 (inspirada no Folio). A R00 **retirou do produto ativo** a importação documental, a conferência e a
conciliação, e **preservou** o Pluggy e o agente de IA de classificação. Nenhuma das etapas seguintes (R01 a R05) foi implementada aqui.

Decisões do produto que orientam a limpeza (não implementadas na R00): categorias em dois níveis (categoria e subcategoria); despesa fixa como classificação
independente de categoria e forma de pagamento; metas por categoria, uma meta consolidada de despesas fixas e **um teto único para todos os cartões somados**
(sem teto por cartão); as metas são perspectivas sobre os mesmos gastos e nunca se somam como despesas distintas.

## 1. O que foi retirado

| Fluxo | O que saiu |
|---|---|
| Coleta por e-mail | Coletor Gmail, serviço hospedado (`FinancialEmailCollectorHostedService`), lock PostgreSQL, checkpoint, rotas `/api/financial-email-import/*`, busca do último e-mail por cartão, `docker-compose.collectors.yml` e a configuração `FinancialEmailImport`/Gmail. **Nenhum agendamento de coleta é registrado**; habilitar a configuração antiga não tem efeito (há teste). |
| Inbox e processamento documental | Inbox de documentos, armazenamento de arquivos (`FinancialDocuments:StoragePath`), classificação documental, análise/consolidação de faturas por PDF, pipeline OCR/visual (Poppler/Tesseract removidos da imagem da API), laboratório de extração, ferramentas offline `StatementDiagnostic`, `StatementExtractionLab` e `DocumentCutover`. |
| Importação manual CSV/PDF | Pré-visualização, confirmação e pré-conciliação de CSV/PDF, parsers de fatura (Itaú, Nubank), importação de parcelas, aba **Cartões > Importações**. |
| Contas de serviço | Contas de serviço, identificadores com HMAC, vínculo com recorrências, extração e confirmação de contas de consumo (`/api/me/service-accounts`, segredo HMAC e script de instalação). |
| Conferências e conciliações | Conciliação de previsão × fatura (**Cartões > Conciliações**), conciliação de parcelas de dívida e a tela/rotas públicas de **conciliação de saldos bancários**. As travas "previsão conciliada não pode ser editada/cancelada/realizada" e "compra conciliada não pode ser cancelada" saíram junto. |
| Navegação | Item **Documentos** do menu, abas de Cartões "Importações" e "Conciliações", painel de conciliação de saldos em Contas, conciliação dentro de Dívidas, campo "Query Gmail" do cadastro de cartão. |
| Reset do orçamento | A fila de remoção física de arquivos (`/financial-reset/cleanup`, "Repetir cleanup") e a exigência de cutover de documentos: não existem mais arquivos de documento. O reset continua apagando os dados financeiros. |
| Infra e CI | `Dockerfile.ocr-tests`, faixa de **OCR nativo** do CI (`ocr`), opção de benchmark "documents", variáveis `FINANCIAL_DOCUMENT_CLASSIFICATION_*`, `SERVICE_BILL_*`, `DocumentaryImport`, volumes de Gmail/HMAC/documentos no compose de produção e no `migrate`, scripts `enable-service-bill-readonly`, `service-bill-readonly-baseline` e `install-service-account-hmac-secret`. Pacote `UglyToad.PdfPig`. |
| Docs vivos | `coleta-email-financeiro`, `gmail-search-diagnostics`, `inbox-documentos-financeiros`, `importacao-cartoes`, `extracao-faturas-agente`, `conciliacao-faturas`. Os relatórios `T*.md` permanecem como **registro histórico** e descrevem funcionalidades que não existem mais. |

## 2. O que foi preservado por dependência funcional

* **Pluggy** (inalterado): autenticação e conexão (`PluggyClient`, `PluggyApiKeyProvider`), vínculos instituição/conta/cartão/workspace, sincronização manual e
  automática (`PluggyTransactionSyncService`, `PluggyAutoSyncRunner`, `PluggyAutoSyncHostedService`, desligada por padrão), motor de lançamentos
  (`PluggyPostingEngine`), faturas e pagamentos do banco, categorização e a **revisão de lançamentos do Pluggy** (`PluggyReviewService`: decide como um movimento
  do banco entra no razão; é parte da integração, não conferência documental).
* **Idempotência e integridade da sincronização**: identificação externa e unicidade (`pluggy_imported_transactions`, chaves de duplicidade), pareamento de
  pagamento de fatura com o débito bancário sem dupla contagem, créditos como ajustes de fatura, isolamento por workspace.
* **Domínio de cartões**: cartões, compras/parcelas, faturas, pagamentos, ajustes e projeção (`CardProjectionEngine`). A importação documental saiu; o domínio ficou.
* **Cálculo de saldos**: `AccountBalanceReconciliationService` (saldo do banco × saldo calculado, âncoras) continua como **serviço interno** que alimenta o Dashboard e a
  cobertura do Planejado × Projetado × Realizado (T10.1). Suas rotas públicas e a tela foram retiradas. O nome "reconciliation" ficou por herança; renomear é backlog.
* **Agente de IA** (um só cliente): ver seção 3.

## 3. Pontos de integração Pluggy e IA

**Pluggy** — `src/Saldo360.Application/Pluggy/*`, `src/Saldo360.Infrastructure/Pluggy/*`, endpoints `src/Saldo360.Api/Pluggy/*`; configuração `Pluggy:*`
(`Pluggy:AutoSync:*` desligado por padrão); credenciais somente por user-secrets/variáveis de ambiente.

**Agente de IA** — extraído do módulo de faturas para uma área própria, sem criar um segundo cliente nem um motor paralelo:

| Antes | Depois |
|---|---|
| `ICreditCardStatementAgentProvider` (Application/CreditCards) | `IAiCompletionProvider` (`Saldo360.Application.Ai`) |
| `CreditCardStatementAgentOptions` / `...Mode` | `AiAgentOptions` / `AiAgentMode` (mesma seção de configuração) |
| `CreditCardStatementAgentUnavailableException` | `AiProviderUnavailableException` (mesmos códigos de falha) |
| `OpenAiCreditCardStatementAgentProvider` (Infrastructure/CreditCards, com imagens e esquemas de fatura) | `OpenAiCompletionProvider` (`Saldo360.Infrastructure.Ai`, somente texto + esquema JSON estrito) |

Preservado do provider: Responses API com `store:false` e saída estruturada estrita; credencial por arquivo (`ApiKeyFile`) sem cair para chave inline; limite de
resposta; códigos de falha em lista fechada (nunca a mensagem ou o corpo do provider); `OnUsage` para contagem de tokens. A classificação
(`OpenAiFinancialClassificationModel`, `FinancialClassificationService`) continua com seus limites operacionais (`FinancialClassification:*`: lote, máximo por
execução, custo máximo, confiança mínima) e só usa a IA com `AiEnabled` **e** o agente habilitado **e** credencial. As decisões manuais de categoria seguem
protegidas pelo fluxo existente.

> A seção de configuração continua se chamando **`CreditCardStatementAgent`** (nome histórico): renomeá-la agora invalidaria variáveis de ambiente e segredos já
> provisionados. A renomeação fica para a R04 (central de automação). O valor `Mode` continua aceitando `Shadow`/`AgentPrimary`; qualquer valor diferente de
> `Disabled` habilita o agente.

## 4. Esquema do banco

**R00 não altera o esquema e não adiciona migration.** Instalação limpa é permitida pelo produto, mas **não é exigida** por esta etapa: bancos existentes continuam
compatíveis. Ficaram **dormentes** (sem leitura nem escrita pela aplicação, ainda mapeados e incluídos no inventário do reset):

`financial_document_inbox_items`, `financial_email_collector_checkpoints`, `financial_credit_card_import_batches`, `financial_credit_card_import_items`,
`financial_credit_card_imported_installments`, `financial_credit_card_invoice_consolidations`, `financial_planned_invoice_reconciliations`,
`financial_debt_installment_reconciliations`, `service_accounts`, `service_account_identifiers`, `service_account_bindings`, `financial_workspace_reset_file_cleanup`
e a coluna `financial_credit_cards.gmail_query`.

Os gatilhos de fatura oficial continuam no banco (os testes ainda os exercitam com evidência sintética); a fatura passa a ser fechada pela instituição (Pluggy) ou
manualmente. Remover as tabelas/colunas dormentes é uma única migration de "limpeza de esquema", adiada para a reconstrução do modelo (R02/R03), junto com a
instalação limpa.

## 5. Validação

Backend (.NET 10) e frontend foram compilados e testados; os números estão na entrega. Cobertura específica da R00:

* `RetiredFlowsUnavailableTests` (backend, rápido): nenhuma rota dos fluxos retirados está mapeada; requisições autenticadas às rotas antigas retornam 404; nenhum
  job de e-mail/documento está registrado mesmo com a configuração antiga **habilitada**, e o job de sincronização do Pluggy continua; o agente de IA continua
  resolvendo por um único provider e desligado por padrão; os módulos retirados não existem mais nos assemblies.
* Navegação (frontend): `AppShell.test` garante a ausência de Documentos, Inbox, e-mail, importação, conciliação e conferência.
* Regressão Pluggy/IA mantida: sincronização repetida sem duplicidade, pareamento de pagamentos, categorização com IA simulada e testes do provider (HTTP simulado).
* Os testes de conciliação de saldos passaram a exercitar o serviço diretamente (as rotas saíram); o cálculo continua coberto.

**Não houve validação com Pluggy real nem com a OpenAI real nesta etapa**: os testes usam clientes simulados e nenhuma chamada paga foi feita.

## 6. Pendências e backlog (nenhuma bloqueia a R01)

* Remover o esquema dormente (tabelas/coluna acima) e as entidades de domínio correspondentes — junto da instalação limpa.
* Renomear a seção `CreditCardStatementAgent` e `AiAgentMode` na R04.
* Renomear `AccountBalanceReconciliationService` e a coluna "ReconciliationStatus" do Dashboard quando o novo Dashboard (R01) redefinir a confiabilidade de saldos.
* As áreas de teste `Documents`/`Services` e seu roteamento no CI ficaram sem testes próprios; simplificar quando o CI for revisto.
* O Dashboard atual ainda exibe a confiabilidade da referência bancária e contagens de lançamentos em revisão do Pluggy; a R01 decide a apresentação.

## 7. Próxima entrega

**R01 — Novo Dashboard e Navegação.** Pré-requisitos atendidos: navegação sem os fluxos retirados, serviços de saldo e de plano × realizado intactos, Pluggy e IA
preservados.
