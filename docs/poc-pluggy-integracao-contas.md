# T09.3 — Integração das contas Pluggy ao modelo financeiro do Saldo360

Liga contas bancárias e cartões da Pluggy ao **cadastro de contas e ao núcleo financeiro que já existe**. As transações importadas pela T09.2 passam a ser a fonte do **realizado** das contas integradas, sem criar um segundo núcleo: o Dashboard, a Agenda Financeira, os saldos e as faturas continuam lendo as mesmas tabelas e regras de antes. O Saldo360 não depende do formato dos contratos da Pluggy — o formato só existe no cliente, no mapeador e nas tabelas de origem (T09.2).

Não há push nem deploy nesta etapa. A conciliação com **previsões** não faz parte desta tarefa.

## 1. Modelos existentes reaproveitados

| Já existia | Uso na T09.3 |
|---|---|
| `FinancialAccount` (conta corrente/poupança) | Recebe o vínculo; não ganhou coluna de origem — a origem é **derivada do vínculo**. Saldo inicial nunca é preenchido sozinho. |
| `FinancialTransaction` | Receita/despesa bancária importada (origem nova `PluggyImport`) e saída do pagamento de fatura (origem existente `CreditCardInvoicePayment`). |
| `FinancialTransfer` | Transferência entre duas contas integradas (um registro, sem receita/despesa). |
| `FinancialCreditCard`, `FinancialCreditCardPurchase`, `FinancialCreditCardInvoice`, `FinancialCreditCardInvoicePayment`, consolidação oficial de fatura | Cartão, compra (uma parcela por transação), fatura e pagamento. |
| `FinancialAccountService.GetBalanceAsync`, `FinancialBalanceCalculator`, T06.3/T07 | Saldo calculado e projeção, **sem alteração**. |
| `PluggyItemLink` (T09.1), `PluggyImportedTransaction` e coletas (T09.2) | Itens e transações de origem; permanecem imutáveis para o usuário. |

## 2. Alterações no domínio

- **Nova origem** `FinancialTransactionOrigin.PluggyImport`. `FinancialTransaction` ganhou `CreateFromPluggy`, `IsImported`, `Recategorize` e `ApplyImportedSource`. `Update` recusa lançamento importado (só a categoria é corrigível).
- **`FinancialCreditCardPurchase`**: `CreateImported` (compra de uma parcela) e `Recategorize`.
- **Entidades novas** (`Saldo360.Domain.Pluggy`):
  - `PluggyAccountLink` — vínculo persistente conta do Saldo360 ↔ conta externa (Item, `accountId` externo, tipo, instituição/nome exibido, situação, última sincronização, início da importação, **valores observados** de saldo/limites). Sem número de conta, sem credenciais.
  - `PluggyPosting` — **rastro** de cada transação importada: o que ela gerou no núcleo (lançamento, transferência, compra, pagamento), estado (`WaitingPosted`, `Applied`, `NeedsReview`, `Covered`, `Ignored`), motivo, categoria (`Mapped`/`User`), e o retrato aplicado (valor/data) usado para detectar mudança de origem.
  - `PluggyCardBill` — fatura informada pelo banco (datas e total), evidência para localizar a compra na fatura certa e fechar a fatura oficial.
- Origem **Manual/Pluggy** na conta: `AccountOrigins`, derivada do vínculo (`FinancialAccountDto.Origin` e `Integration`, campos adicionais — contratos anteriores continuam válidos).

## 3. Migrations

Somente criação de tabelas; nenhuma tabela existente foi alterada e nenhum dado foi removido.

| Migration | Conteúdo |
|---|---|
| `20261028172653_AddPluggyFinancialIntegration` | `pluggy_account_links`, `pluggy_postings`. |
| `20261028181549_AddPluggyCardBills` | `pluggy_card_bills`. |

Garantias no banco: vínculo único por (conexão, conta externa); uma conta/cartão do Saldo360 só integra uma conta externa; vínculo é conta **ou** cartão (check); **um rastro por transação importada** (`ux_pluggy_postings_imported`) e um rastro por lançamento/compra/pagamento gerado; estado `Applied` sempre aponta para o registro gerado (check); FKs compostas pelo orçamento. As tabelas têm `workspace_id` e entram no reset do orçamento e no inventário do importador de planilha.

## 4. Contratos e DTOs

Rotas sob `/api/financial-workspaces/{workspaceId}/pluggy-integration`. Dono do orçamento, sessão e CSRF (escritas); `Pluggy:Enabled` desligado → 404. Corpos recusam campos desconhecidos (`workspaceId`, `userId`…). Valores monetários são texto decimal exato.

| Rota | Função |
|---|---|
| `GET /status`, `GET /connections` | Disponibilidade; Itens do orçamento. |
| `GET /connections/{id}/accounts` | Contas/cartões devolvidos pela Pluggy para o Item, com número **mascarado**, tipo sugerido, sugestões de cartão (fechamento, vencimento, limite) e se já estão vinculados. |
| `POST /links` | Vincula: `CreateNew` (cria conta/cartão com os metadados, após confirmação) ou `LinkExisting` (exige `importFrom`). O `pluggyAccountId` é **conferido no backend** contra as contas do Item. |
| `GET /links`, `GET /links/{id}` | Vínculos; detalhe com saldos (observado, calculado, diferença), sugestão de saldo inicial, limites de cartão e faturas banco × Saldo360. |
| `POST /links/{id}/opening-balance-suggestion` | Aplica o saldo inicial sugerido **só com `confirm: true`**; o valor é recalculado no servidor; substituir um saldo existente exige `replaceExisting`. |
| `POST /reprocess` | Reprocessa as transações já importadas (idempotente). |
| `GET /review`, `POST /review/{postingId}/resolve` | Fila de revisão e decisão (`AsIncome`, `AsExpense`, `AsCardPurchase`, `Ignore`), validada pelo sentido do movimento. |
| `PUT /transactions/{id}/category` (rota existente de lançamentos) | Corrige só a categoria de lançamento importado. `PUT /transactions/{id}` completo responde 409 para importados. |

`GET /accounts` ganhou `origin` (`Manual`/`Pluggy`) e `integration` (nome exibido, situação, última sincronização, saldo informado pelo banco).

Configuração: nenhuma chave nova obrigatória; `Pluggy:BillMaxPages` (padrão 3) limita as páginas de 50 faturas por cartão por sincronização.

## 5. Regras de transformação (`PluggyFinancialMapper`, puro)

Usa campos **estruturados** (tipo `DEBIT`/`CREDIT`, operação, metadados de cartão). Descrição nunca decide sozinha uma transferência ou um pagamento de fatura. Sentido vem do tipo, não do sinal do valor; valor é sempre centavos exatos (mais de duas casas → revisão, nunca arredondado); moeda diferente de BRL → revisão.

| Situação | Resultado |
|---|---|
| Conta bancária, `POSTED`, `DEBIT`/`CREDIT` | Despesa/receita realizada (`PluggyImport`), na data do banco (dia UTC). |
| `PENDING`, data futura | Aguarda (`WaitingPosted`): **não** entra no realizado. Ao efetivar, entra. |
| Anterior a `importFrom` | Ignorada (não gera rastro). |
| Compra ou parcela de cartão (`POSTED`) | Compra de **uma parcela** na fatura do cartão. Parcela `k/N` recebe o sufixo `(k/N)` só a partir dos metadados estruturados. Categoria: mapeada ou "Outros". |
| Crédito no cartão com operação `PAGAMENTO_FATURA` | Evidência do pagamento (`Covered`); nunca receita. |
| Débito bancário igual (valor) e próximo (±3 dias) a um pagamento de fatura de cartão integrado, **correspondência única** (ou, para o débito rotulado como pagamento de cartão, igual a um `payments[]` da fatura, ±2 dias) | Pagamento de fatura (ver abaixo e §13). A dica "descrição cita fatura" só desempata. |
| Transferência na mesma instituição, débito × crédito de mesmo valor em contas integradas diferentes (±1 dia), **correspondência única** | `FinancialTransfer`; não infla receitas/despesas. |
| `PIX`/`TED`/`DOC` bancário (disposição `TransferProbe`), débito × crédito de **mesma operação**, mesmo valor, contas integradas diferentes (±1 dia), **correspondência única** | `FinancialTransfer` (substitui o lançamento simples já criado). Sem ponta oposta numa conta integrada continua receita/despesa (a maioria dos PIX é com terceiros); ambíguo → revisão `AmbiguousTransferCounterpart`. Nunca usa a descrição. *(Regra acrescentada na validação com dados reais, §11.)* |
| Transferência/pagamento sem contrapartida, ambíguos, saque, resgate, estorno, crédito/empréstimo, crédito no cartão, operação de cartão desconhecida | **Revisão**: fora de receitas, despesas e saldos até decisão. |
| Categoria da Pluggy | Mapeada por nome (sem acento/caixa) ou apelido conhecido **só se o orçamento tiver a categoria**; senão sem categoria. |

**Pagamento de fatura sem duplicar consumo.** O consumo está nas compras do cartão (faturas); o pagamento é a saída de caixa (`CreditCardInvoicePayment`) e baixa a fatura. *Revisado na T09.3.2 (§13):* a fatura é considerada **fechada pelo banco** quando a data de fechamento informada chega e há total, independentemente de conciliação; o pagamento (parcial ou integral) é registrado nessa fatura quando a correspondência é inequívoca (crédito de pagamento no cartão ou `payments[]` da fatura), sem exigir fatura oficial nem conciliação integral.

**Idempotência e preservação.** Um rastro por transação importada; reprocessar não duplica. Mudança de valor/data/descrição de origem atualiza o registro gerado (nunca a categoria). Categoria escolhida pelo usuário (e a de compras) não é sobrescrita. Lançamento/transferência/pagamento cancelado pelo usuário **não é recriado**. Se um lado de um par muda, o par é desfeito (cancelado) e reavaliado. Fatura oficial é imutável: mudança de origem depois do fechamento aparece como `OfficialDiverges` e nada é reescrito.

## 6. Integração com os serviços financeiros existentes

- **Dashboard / Agenda / saldos / projeção**: nenhuma consulta foi alterada. Como o realizado é `FinancialTransaction`/`FinancialTransfer`, receitas, despesas, histórico por período, Agenda (item com `origin: PluggyImport`, somente leitura) e saldos refletem os movimentos automaticamente. Pendentes/em revisão ficam fora. Transferências aparecem como transferências.
- **Cartões**: o consumo vira obrigação de fatura (orçamentos, limite usado, projeção) como qualquer compra; o pagamento é uma única saída de caixa.
- **Saldos separados** (tela de contas): *saldo informado pelo banco* (Pluggy) × *saldo calculado pelo Saldo360* (regra T06.3: exige saldo inicial) × *diferença* (só quando os dois existem) × limite de cheque especial informado (não compõe saldo). Cartão: limite/usado/disponível do Saldo360 e saldo devedor/limite informados pelo banco; **nunca soma saldo de caixa**.
- **Saldo inicial**: nada é escrito sozinho. Depois de uma sincronização completa e sem itens em revisão, o Saldo360 sugere `saldo informado − movimentos desde o primeiro dia importado`; o usuário aplica com confirmação. Sem base consistente, a sugestão aparece como **indisponível** com o motivo.

## 7. Interface

Contas → *Nova conta*: escolha de **Origem** (Manual preservado / Pluggy). Pluggy: Item → conta/cartão devolvidos → criar com metadados ou vincular a existente (data de início obrigatória) → confirmação. Lista de contas: origem, banco/nome vinculado, situação, última sincronização, saldos separados, sugestão de saldo inicial, reprocessar. Seção *Integração bancária*: cartões integrados (limites, faturas banco × Saldo360) e **Movimentos para revisar**. Lançamentos: marcação "Importado da Pluggy", sem edição completa, categoria corrigível; a categoria nula agora é exibida como "Sem categoria". O React só formata o que a API calcula.

## 8. Segurança

Autenticação/autorização existentes (dono do orçamento) e CSRF; Item e conta de outro orçamento → 404; conta externa duplicada → 409; IDs do navegador são validados contra o Item e a propriedade; credenciais e números completos nunca saem do backend; nenhuma operação financeira externa é iniciada; respostas sem cache. Trilha de auditoria: autor e datas nos registros gerados, rastro por transação, consolidação oficial com hash e snapshot (sem dados bancários).

## 9. Testes

- `PluggyFinancialMapperTests` (puros): tipos, POSTED/PENDING, cartão/parcela, fatura, transferência, estorno, precisão, categoria, pareamento.
- `PluggyFinancialIntegrationTests` (API + PostgreSQL + Pluggy simulado, 33 cenários): conta manual, listagem/seleção, vínculo, ID inválido/estrangeiro, duplicidade, isolamento, autenticação/CSRF/desabilitado, POSTED×PENDING, idempotência, atualização de origem, categoria preservada, cancelamento do usuário, precisão, compras/parcelas, pagamento de fatura (pareado, ambíguo, sem evidência, total divergente), transferência interna (e desfazer), estornos, revisão, saldos observado×calculado e sugestão, cartão sem saldo de caixa, Dashboard/Agenda.
- Front: `PluggyIntegration.test.tsx`, `AccountsPluggy.test.tsx`, ajustes em `Transactions.test.tsx`.

## 10. Limitações

- **Validação com dados reais** feita no laboratório em 28/10/2026 (ver §11): estabilidade da T09.2 comprovada; os achados e limitações da validação estão em §11.
- **Fechamento e conciliação são independentes (T09.3.2, §13).** Encargos, juros, estornos de cartão, histórico parcial e itens pendentes não impedem o fechamento nem o pagamento: aparecem como divergência ou conciliação incompleta. Estornos e cashback com referência à fatura viram crédito da fatura; sem referência, revisão.
- Quando a compra não traz fatura do banco, usa-se o calendário do cartão (dias informados), que pode diferir do fechamento real; compras já lançadas dessa forma não são movidas depois.
- Cada transação de cartão é uma compra de uma parcela: não há ligação entre parcelas nem projeção de parcelas futuras além das que a Pluggy devolve.
- Transferências só entre contas bancárias integradas; saque, resgate e transferência com contas não integradas ficam em revisão. Estorno bancário (`ESTORNO`/`DEVOLUCAO`) vai para revisão, sem reduzir a despesa original.
- Só contas `BANK` e `CREDIT`, só BRL; investimentos e empréstimos são ignorados. Data de negócio = dia UTC informado pelo banco.
- Não há "desvincular" nem trocar o início da importação depois do vínculo; vincular a uma conta existente exige escolher a data (movimentos anteriores não são importados).
- Pagamento de fatura de cartão cujo débito sai de conta não integrada fica em revisão (o Saldo360 não cria saída em conta que não acompanha).
- Mudança de origem num lançamento depois de pareado em transferência/pagamento desfaz o par e o reavalia; se não houver nova correspondência, vai para revisão.
- Sem agendamento: a sincronização continua manual.

## 11. Validação com dados reais (laboratório, 28/10/2026)

Executada só no banco local `saldo360_pluggy_lab`, com os três Itens MeuPluggy (Itaú PF, Itaú PJ e Nubank PJ). Este relatório registra **apenas contagens e situações**: nenhum valor individual, descrição, identificador de Item/conta, número de conta/cartão ou credencial.

### 11.1 Migrations aplicadas

`AddPluggyFinancialIntegration` e `AddPluggyCardBills`, nesta ordem, com a connection string do laboratório (conferida antes). Backup do banco e contagem de linhas de todas as tabelas antes e depois: só mudaram o histórico de migrations (+2) e as três tabelas novas (`pluggy_account_links`, `pluggy_postings`, `pluggy_card_bills`). Nenhuma tabela operacional existente foi alterada ou esvaziada; não houve reset do orçamento. O banco principal (`saldo360`) não foi tocado.

### 11.2 Sincronização real (estabilidade da T09.2)

| Conexão | Contas | Transações | 1ª coleta (novas) | 2ª e demais coletas |
|---|---|---|---|---|
| Itaú PF | 4 (1 conta + 3 cartões) | 1787 | 1787 | 0 novas, 0 atualizadas, 1787 inalteradas |
| Itaú PJ | 2 (1 conta + 1 cartão) | 154 | 154 | 0 novas, 0 atualizadas, 154 inalteradas |
| Nubank PJ | 2 (1 conta + 1 cartão) | 63 | 63 | 0 novas, 0 atualizadas, 63 inalteradas |

Foram 4 coletas por conexão (2 antes do vínculo das contas, 1 depois do vínculo bancário, 1 depois do vínculo dos cartões), todas `Completed`, 0 erros, 0 coletas incompletas, 0 ausentes. Total: **2004 transações** (855 bancárias, 1149 de cartão); 0 IDs externos duplicados; 0 mudanças de status ou de valor registradas; todos os IDs preservados entre coletas. Há 3 grupos (6 transações) com a mesma chave de possível duplicidade (a chave não é identidade; não há duplicidade real). 79 transações têm data futura (cartão parcelado e agendamentos) e 5 não são BRL; ambos os casos não entram no realizado.

### 11.3 Associação das contas (pela interface)

Origem Pluggy → Item → conta/cartão devolvido → criar com os metadados. Vinculadas **3 contas bancárias** (Itaú PF corrente, Itaú PJ corrente, Nubank PJ) e **5 cartões** (3 do Itaú PF, 1 do Itaú PJ, 1 do Nubank PJ). A Pluggy não informa o dia de fechamento dos cartões; os dias foram informados pelo usuário. Importação sem data de início (todo o histórico devolvido). Duplicidade: a interface desabilita a conta já vinculada e o servidor responde **409** ao reenvio da mesma conta externa (404 para conexão inexistente e 400 para conta que não pertence ao Item); nenhuma linha criada nessas tentativas.

### 11.4 Resultado financeiro

| Indicador | Resultado |
|---|---|
| Rastros (um por transação importada) | 2004: 1684 aplicados, 209 em revisão, 111 aguardando (pendentes/futuros) |
| Lançamentos financeiros ativos criados | 769 (643 despesas, 126 receitas) |
| Transferências entre contas integradas | 23 (46 pontas) |
| Compras de cartão (uma parcela cada) | 869 |
| Lançamentos substituídos por transferência (cancelados pelo motor) | 46 |
| Movimentações enviadas para revisão | 209 |
| Pendentes/futuras que viraram lançamento | 0 |
| Lançamentos/compras duplicados (mesmo rastro em mais de um registro) | 0 |

Revisão por motivo: crédito no cartão 71 (estornos/créditos), pagamento de fatura sem fatura oficial 46, operação de cartão não classificada 32, operação de crédito bancária 19, operação de crédito no cartão 13, transferência sem contrapartida integrada 12, moeda estrangeira 5, resgate de investimento 4, pagamento de fatura sem débito bancário 4, estorno de cartão 1, possível pagamento de fatura não confirmado 1, saque 1.

**Conciliação com a Pluggy.** Para cada conta bancária, total de movimentos efetivados da Pluggy = aplicado + em revisão, com **diferença de 0 centavo** nas três contas (antes e depois da regra de transferência). Nas compras de cartão, soma importada = soma aplicada, 0 centavo de diferença. As diferenças em relação ao agregado bruto da Pluggy se explicam integralmente por: pendentes/futuros (não contabilizados), itens em revisão e transferências entre contas integradas (que saem de receitas e despesas).

**Dashboard e Agenda.** Receitas e despesas realizadas da competência 2026-10 conferem em centavos com a soma dos lançamentos no banco; pendentes aparecem zerados; a Agenda lista os mesmos itens realizados; saldo realizado e fechamento projetado aparecem como **indisponíveis** (sem saldo inicial) com cobertura parcial, e o bloco de cartões mostra os 5 cartões e as faturas sem fechamento oficial.

**Categorias.** O orçamento do laboratório não tinha categorias; foram criadas duas (Alimentação e Serviços) para o teste. A categoria escolhida pelo usuário num lançamento importado (origem `User`) **sobreviveu à coleta seguinte** e ao reprocessamento. O mapeamento por nome só vale para lançamentos criados depois de a categoria existir (ver 11.5). Todos os 769 lançamentos continuam sem categoria, exceto o do teste.

**Saldos.** Observado (banco) disponível nas 3 contas; **calculado indisponível** nas 3 (sem saldo inicial; nenhum saldo foi ajustado ou aplicado). Sugestão de saldo inicial: indisponível em Itaú PF e Itaú PJ (movimentos em revisão, `ReviewPending`) e disponível no Nubank PJ, **não aplicada** (depende de confirmação). Por isso a divergência saldo observado × calculado **não pôde ser medida**: ela só existe depois que o usuário informar um saldo inicial independente (por exemplo, de um extrato) e resolver a revisão; a sugestão do sistema é, por construção, igual ao observado.

### 11.5 Problemas encontrados

1. **PIX/TED/DOC entre contas do próprio usuário contados como receita e despesa** (achado do usuário na tela de lançamentos). A regra de transferência só reconhecia `TRANSFERENCIA_MESMA_INSTITUICAO`; transferências PF↔PJ chegam como `PIX`/`TED`. Isso inflava receitas e despesas do mês. **Corrigido** (disposição `TransferProbe`, §5): 23 pares reais foram pareados no reprocessamento (receitas e despesas de 2026-10 caíram o mesmo valor, o resultado líquido do mês não mudou, reprocessar de novo não altera nada). 12 créditos `TRANSFERENCIA_MESMA_INSTITUICAO` continuam em revisão porque a outra ponta está numa conta não integrada. Testes adicionados (11.7).
2. **Nenhuma fatura de cartão pôde ser fechada oficialmente** com dados reais, e portanto **nenhum pagamento de fatura foi registrado**. Das 51 faturas do banco já fechadas com total, 50 têm algum item pendente ou em revisão (créditos, estornos, operações não classificadas) e uma não tem transações importadas; o critério "total do banco = soma exata das compras importadas, sem itens em aberto" é conservador demais para a realidade desses cartões. As faturas não foram fechadas artificialmente e a trava `OFFICIAL_INVOICE_REQUIRED_FOR_PAYMENT` foi mantida.
3. **Pagamentos de fatura continuam como despesa bancária simples.** 39 débitos bancários (categoria estruturada "pagamento de cartão" da Pluggy) seguem como despesa porque a fatura correspondente não é oficial. No fluxo de caixa isso está correto (o dinheiro saiu), mas o consumo desses cartões também aparece nas compras do cartão; consumos categorizados poderiam ser somados duas vezes em visões de consumo por categoria. Nada foi reclassificado automaticamente.
4. **Categorias criadas depois da importação não são aplicadas retroativamente** aos lançamentos já criados (o mapeamento roda quando o rastro nasce). Itens sem categoria precisam de ação do usuário.
5. **Dia de fechamento dos cartões não vem da Pluggy** (fechamento ausente nos três Itens): o cadastro depende do usuário. Dois cartões (fechamento e vencimento no mesmo dia 15) foram aceitos pelo cadastro; vale confirmar com a fatura real.
6. Detalhes operacionais: a primeira carga do seletor de conexões é lenta (consulta a Pluggy por Item); a sugestão de saldo inicial só existe depois de uma sincronização posterior ao vínculo.

### 11.6 Pendências para o usuário (não executadas)

- Resolver os 209 movimentos em revisão (principalmente créditos/estornos de cartão e operações de crédito) e decidir o tratamento de pagamentos de fatura.
- Informar saldo inicial a partir de fonte independente; só então a divergência observado × calculado passa a ser mensurável.
- Decidir a categorização automática dos lançamentos importados: o agente existente é de extração de faturas em PDF e não está configurado neste ambiente.

### 11.7 Testes direcionados

- `--filter "Area=Pluggy"` após a correção das transferências: **195 aprovados, 0 falhas** (inclui 3 testes novos: `PixTedAndDocAreTransferProbesAndNeverDependOnTheDescription`, `PixBetweenIntegratedAccountsBecomesATransferAndAlreadyAppliedMovementsAreReplaced`, `AmbiguousPixCounterpartsGoToReviewInsteadOfGuessing`).
- Suíte completa do backend: 1678 aprovados, 3 falhas (as mesmas 3 de `RealOcrToolTests`, que dependem de Poppler/Tesseract nativos ausentes nesta máquina e não têm relação com a Pluggy).
- Os testes de mapeador passaram a usar operação `BOLETO` como padrão (antes `PIX`), porque `PIX` agora é um caso próprio.

## 12. T09.3.1 — Categorização financeira híbrida e reutilizável

Ordem fixa para cada lançamento importado: **escolha manual → regras do domínio → categoria da Pluggy (catálogo) → IA como último recurso**. A IA nunca decide o que é transferência, pagamento de fatura ou estorno, não cria nada por conta própria (quando nenhuma categoria existente serve, ela apenas **sugere** uma nova, que o backend valida e cria) e só roda sob demanda, com confirmação explícita. Como nas seções anteriores, este relatório registra apenas contagens.

### 12.1 Arquivos alterados

- **Novos (Application):** `Classification/FinancialClassificationModels.cs`, `FinancialClassificationRules.cs` (regras, catálogo e sanitização), `FinancialClassificationService.cs`, `OpenAiFinancialClassificationModel.cs`; `Pluggy/PluggyCategorization.cs` (orquestração e contratos).
- **Novo (Infrastructure):** `Pluggy/PluggyCategorizationRepository.cs`; migration `20261028221507_AddPluggyCategoryClassification`.
- **Alterados:** `PluggyPosting` (origem e auditoria da categoria), `PluggyFinancialMapper.cs` (o mapeador legado delega ao catálogo único), `PluggyPostingEngine.cs` (lançamentos novos já nascem com categoria local), `CreditCardStatementAgentDtos.cs` e `OpenAiCreditCardStatementAgentProvider.cs` (modelo por chamada e leitura de uso de tokens, compatível com o que já existia), DTO/repositório/resposta de lançamentos (origem da categoria), `PluggyFinancialEndpoints.cs`, DI.
- **Front:** `api/pluggyIntegration.ts`, `pluggyFormat.ts`, `components/PluggyIntegration.tsx` (painel de categorização), `Transactions.tsx` (indicador da origem da categoria) e testes.
- **Testes:** `Classification/FinancialClassificationTests.cs`, `Pluggy/PluggyCategorizationIntegrationTests.cs`, `Support/FakeFinancialClassificationModel.cs`.

### 12.2 Contratos e serviços reutilizados

- **IA:** o `ICreditCardStatementAgentProvider` existente (Responses API, `store:false`, JSON Schema estrito), o `HttpClient` nomeado `CreditCardStatementAgent` e a mesma credencial/`ApiKeyFile`. Não há segundo cliente OpenAI, assistente remoto nem framework de agentes: o precedente é `FinancialDocumentClassificationAgent`, que também reaproveita o provider com prompt e schema próprios.
- **Domínio:** `FinancialCategory` (unicidade por nome e tipo), `FinancialTransaction.Recategorize`, `FinancialCreditCardPurchase.Recategorize`, o rastro `PluggyPosting` (única linha por transação importada) e o endpoint existente de correção de categoria.
- **Serviço novo e único:** `IFinancialClassificationService` (entrada normalizada `FinancialClassificationInput`, independente da origem; `ClassifyLocal` e `ClassifyWithAiAsync`). Futuros consumidores (OFX/CSV, OCR, comprovantes) usam o mesmo serviço; só a persistência é específica da origem.

### 12.3 Mapeamentos e regras

- **Regras determinísticas (antes de qualquer categoria de consumo):** pagamento de fatura de cartão (rótulo estruturado da Pluggy), transferência entre contas do titular, transferência a terceiros (PIX/TED/boleto) e estornos/ajustes. Nenhuma recebe categoria de consumo; transferência a terceiros e estorno ficam marcados para revisão; compra de cartão com rótulo de pagamento de fatura é sinalizada (a compra continua com o marcador "Outros", pois não pode ficar sem categoria).
- **Catálogo Pluggy → Saldo360** (≈ 80 categorias em inglês para ≈ 28 nomes canônicos): por exemplo supermercado/mercearia → Alimentação; restaurante/delivery → Restaurantes (ou Alimentação, se for a que o usuário tem); compras/eletrônicos → Compras; livraria → Livros e papelaria; serviços digitais/streaming → Assinaturas; telecomunicações/internet → Telecomunicações; energia, água, combustível, farmácia, saúde, seguros, tributos/IOF, tarifas e juros, pets, lazer, educação, viagens, doações; rendimentos de investimento → Rendimentos (receita); salário → Salário.
- **Reaproveitamento sem duplicar:** a busca ignora caixa, acento, pontuação e espaçamento e aceita apelidos (ordem de preferência por entrada). Só quando nenhum nome aceito existe, em nenhum estado (inclusive categoria inativa que o usuário desativou), a categoria canônica é criada, uma vez, com o tipo certo (despesa ou receita). Uma categoria da Pluggy sem entrada no catálogo nunca vira categoria nova: vai para o fallback.
- **Retroativo:** aplicado aos lançamentos e compras de cartão já importados que não tenham categoria manual. Categoria do usuário é preservada em qualquer execução (inclusive `force`), tanto a registrada pelo rastro quanto uma alterada diretamente no lançamento. Compra em fatura oficial é imutável (trigger do banco) e fica fora. Reavaliar nunca remove categoria já aplicada.

### 12.4 Modelo e privacidade

- **Modelo:** `gpt-6.1-sol` por padrão desde 28/10/2026 (opção `FinancialClassification:Model`; a execução descrita em 12.7 usou `gpt-4o-mini`, antes da troca; os preços para estimar custo são opções e precisam ser ajustados ao modelo vigente), saída estruturada estrita, lotes de 20, até 200 itens, 60 mil tokens de entrada e US$ 0,50 estimados por execução, 2 tentativas com espera para limite de taxa/timeout/rede (autenticação e resposta inválida não repetem). Todos os limites são `Options`. A chave vem de `CreditCardStatementAgent` (arquivo secreto em produção); só no laboratório local foi lida de `OPEN_AI_TEST_API_KEY` para o ambiente do processo por um lançador fora do repositório, sem impressão nem gravação. Nada disso chega ao React.
- **Enviado:** descrição saneada (≤ 80 caracteres), tipo (receita/despesa), tipo de operação, categoria da Pluggy e a lista de categorias ativas, com códigos temporários (sem IDs internos, valores, datas, contas ou nomes de conta).
- **Removido/recusado antes do envio:** CPF/CNPJ, e-mail, URL, telefone, UUID/chaves, sequências numéricas e números mascarados; nunca são enviadas transferências (PIX/TED/DOC e rótulos de transferência) nem descrições que parecem nome de pessoa; se a sanitização remove mais da metade das palavras, a descrição não é enviada. Nesses casos o item fica para revisão manual.
- **Papel e contexto do agente:** o prompt apresenta o agente como **consultor financeiro** que recebe a lista de categorias existentes e a de lançamentos ainda não categorizados: para cada lançamento verifica se ele se encaixa numa categoria atual (e a relaciona) ou, se não, sugere uma **nova categoria** genérica (1 a 3 palavras, sem números, empresas ou pessoas). A sugestão vem em campo separado (`suggestedCategoryName`) e nunca como identificador.
- **Sugestão de categoria nova:** o backend aceita a sugestão apenas com `needsReview=false` e confiança mínima; nome seguro (3 a 40 caracteres, só letras, até 4 palavras, nada genérico como "Outros" ou "Diversos"); equivalente ativa existente (sem diferenciar caixa, acento ou espaços) é reutilizada; equivalente desativada pelo usuário nunca é recriada; limite de categorias novas por execução (padrão 10). A criação é do backend, uma vez por nome, só depois da confirmação do usuário.
- **Validação da resposta:** código existente no menu enviado, categoria ativa do tipo do lançamento, `needsReview=false` e confiança mínima 0,80 (apenas indicação auxiliar); respostas inválidas, ambíguas ou sem evidência ficam em revisão. A justificativa persistida é curta, sem dígitos nem caracteres de controle; prompts e respostas brutas não são guardados nem logados.
- **Falhas:** sem credencial ou IA desligada → só local (`AiUnavailable`); falha do provedor → resultado local mantido, itens não tentados continuam elegíveis; a sincronização bancária nunca chama a IA nem depende dela.

### 12.5 Persistência e auditoria

Migration `AddPluggyCategoryClassification` (aditiva, em `pluggy_postings`): `category_classified_at`, `category_method_version` (por exemplo `rule:…`, `pluggy-catalog:…`, `ai:<modelo>:<prompt>`), `category_reason`, `category_needs_review` e a restrição `category_source` ampliada para `None/Mapped/User/Pluggy/Rule/AI` (`Mapped` é o legado e equivale a Pluggy). Nenhuma tabela existente foi alterada além de `pluggy_postings`; aplicada só no laboratório.

### 12.6 Interface

Contas → Integração bancária: painel "Categorização dos lançamentos importados" (contadores por origem: Pluggy, regras, IA, você, sem categoria segura, não avaliados), botão "Categorizar automaticamente" (local, sem confirmação) e "Categorizar também com IA…" (confirmação explícita com o que é enviado; desabilitado, com explicação, se a IA não estiver disponível). O resultado mostra o que foi categorizado por origem, categorias criadas, itens preservados por terem sido alterados pelo usuário, tokens e custo estimado. Em Lançamentos, cada lançamento importado mostra "Categoria: Pluggy/Regra/IA/Você" ou "Sem categoria segura: revisar", e a correção de categoria continua disponível.

### 12.7 Resultado no laboratório (dados reais, só contagens)

| Etapa | Resultado |
|---|---|
| Universo (lançamentos bancários + compras de cartão aplicados) | 1638 |
| Categorizados pela Pluggy (catálogo) | 1448 (1289 nesta execução + 159 já mapeados antes) |
| Fechados por regra (não são consumo, sem categoria) | 69 (31 transferências entre contas do titular, 38 pagamentos de fatura) |
| Escolha do usuário (preservada) | 1 |
| Sem categoria segura / em revisão de categoria | 120 (108 transferências a terceiros, 7 compras de cartão com rótulo de pagamento de fatura, 5 sem evidência) |
| Categorias criadas pelo catálogo | 27 (de 3 para 30 no orçamento) |
| Reexecução local | 0 alterações, 0 categorias criadas (idempotente) |
| Valores, datas, contas, parcelas e transferências | **hashes idênticos antes e depois** (nenhum saldo ou total mudou) |

**IA (uma execução, após confirmação do usuário):** 4 lançamentos enviados em 1 chamada (o 5º não foi enviado por não poder ser anonimizado), **895 tokens de entrada e 128 de saída, custo estimado US$ 0,0002**, modelo `gpt-4o-mini`. Resultado: **0 categorizados pela IA** (2 respostas rejeitadas pela validação de categoria/tipo e 2 marcadas pelo próprio modelo como descrição insuficiente); os 4 ficaram em revisão, e a reexecução com IA não os reenvia.

### 12.8 Testes

- `FinancialClassificationTests` e `PluggyCategorizationIntegrationTests`: **74 testes** (unitários e de API sobre PostgreSQL, sem nenhuma chamada externa: o fallback de IA é substituído em todo o host de testes). Cobrem: preservação de categoria manual, mapeamento Pluggy com variações de acento/caixa/espaço, criação controlada e sem duplicidade (inclusive categoria inativa), tipos receita/despesa, movimentações especiais, fallback de IA, sanitização (CPF/CNPJ, e-mail, telefone, UUID, números mascarados, nomes e transferências), resposta válida, categoria inexistente/inativa/de tipo errado/de outro orçamento, resposta ambígua e ausente, falha de autenticação, timeout e limite de taxa (com e sem recuperação), limites de itens e custo, confirmação obrigatória, reprocessamento idempotente, categorização retroativa, lançamentos novos na sincronização, compra de cartão em fatura aberta e oficial, valores intactos, propriedade do orçamento e exposição da origem da categoria na API.
- Front: 427 testes aprovados (inclui o painel de categorização e o indicador de origem), `tsc` e build ok.
- Backend, suíte completa: **1720 aprovados, 3 falhas** (as mesmas 3 de `RealOcrToolTests`: dependem de Poppler/Tesseract nativos ausentes nesta máquina).

### 12.9 Limitações

1. **A IA aceitou 0 de 4** nesta amostra (executada com `gpt-4o-mini`, antes da troca do modelo padrão e da inclusão da sugestão de categoria nova). Com a regra de aceitação conservadora (confiança ≥ 0,80, categoria do menu e do tipo certo) e descrições curtas e saneadas, o ganho prático da IA é pequeno aqui; o grosso foi resolvido por catálogo e regras. O prompt e o limiar são ajustáveis, mas não foram afinados com dados reais.
2. **108 transferências a terceiros ficam sem categoria**: a finalidade não é identificável sem o nome da outra ponta, que nunca é enviado. Cabe ao usuário categorizar.
3. **7 compras de cartão com rótulo de pagamento de fatura** e 12 compras no total continuam com o marcador "Outros" (compra de cartão não pode ficar sem categoria).
4. Compras de fatura oficial não são recategorizadas (imutáveis). Hoje nenhuma fatura é oficial no laboratório, mas isso vale no futuro.
5. Limpar a categoria manualmente (voltar para "Sem categoria") não é registrado como escolha do usuário; uma nova execução pode reaplicá-la.
6. Se o usuário renomear uma categoria criada pelo catálogo, a execução seguinte pode criar a canônica de novo. As 27 criadas são granulares por escolha do catálogo e podem ser renomeadas, desativadas ou mescladas manualmente.
7. A correção de categoria de compras de cartão não está nesta tela (só lançamentos bancários em Lançamentos).
8. Habilitar a IA depende da configuração do agente de faturas (`CreditCardStatementAgent`: habilitado, modo e credencial) além de `FinancialClassification:AiEnabled`; no laboratório foi usado o modo `Shadow` apenas para satisfazer o provider.
9. A estimativa de tokens antes da chamada é heurística (caracteres ÷ 3); o custo informado usa a tabela de preços configurada, não a fatura da OpenAI.
10. Execução manual: não há agendamento nem categorização automática durante a sincronização além das etapas locais.

## 13. T09.3.2 — Correção das regras financeiras com dados Pluggy

Corrige as inconsistências que a validação com dados reais (§11) expôs: faturas, pagamentos, créditos, saldo e datas de fechamento. Como nas seções anteriores, só contagens e situações: nenhum valor individual, descrição ou identificador.

### 13.1 Causas das limitações documentadas

| Limitação (§11.5) | Causa encontrada |
|---|---|
| Nenhuma fatura fechada e nenhum pagamento registrado | O fechamento só ocorria com **conciliação exata** (total do banco = soma das compras, sem itens em aberto) e o banco do Saldo360 só aceita pagamento em fatura **oficial**. Fechamento e conciliação eram a mesma coisa. |
| 39 pagamentos de fatura como despesa simples | Dependiam dessa fatura oficial; sem ela o pagamento ia para revisão e o débito ficava como saída simples. |
| Faturas do Saldo360 não batiam com as do banco | As compras entraram **antes** de as faturas do banco serem lidas e foram alocadas pelo calendário configurado. Além disso, os números informados como "fechamento" dos cartões eram, na verdade, os **dias de vencimento**: o fechamento real do banco (por exemplo, dia 8 num cartão com vencimento no dia 15) nunca foi usado. |
| Estornos, créditos e encargos em revisão | O mapeador só conhecia compra de cartão por operação `PAGAMENTO`; estorno, cashback e encargos (IOF, tarifas, juros) caíam em "operação não classificada" e deixavam a fatura incompleta para sempre. |
| Saldo calculado indisponível sem saldo inicial manual | O cálculo só aceitava o saldo inicial do cadastro; o saldo observado no banco servia apenas como sugestão manual. |

### 13.2 Alterações no domínio financeiro

- **Fatura (`FinancialCreditCardInvoice`)**: novos campos `InstitutionClosedAt`, `InstitutionTotal` e `InstitutionSource`. "Fechada pela instituição" é independente de "oficial" (consolidação com documento), de pagamento e de conciliação. O total bancário é guardado como veio (arredondado a centavos na fatura; o valor exato fica em `pluggy_card_bills`; saldo credor negativo vira obrigação zero). `GetStatus` passa a considerar fechada a fatura oficial **ou** fechada pela instituição.
- **Fatura do banco (`PluggyCardBill`)**: encargos informados, pagamento mínimo e o estado de **conciliação** (`NotVerified`, `Reconciled`, `Divergent`, `Incomplete`) com motivo e o total dos componentes importados. Nova tabela `pluggy_card_bill_payments` com os `payments[]` informados pelo banco (valor, data, modo): referência estruturada para reconhecer a saída bancária.
- **Rastro (`PluggyPosting`)**: novo tipo `CardCredit` (crédito de fatura gerado por estorno, devolução ou cashback) e vínculo com o ajuste de fatura criado; `ApplyBillPayment` aceita pagamento sem a ponta do cartão (evidência por `payments[]`).
- **Âncora de saldo (`FinancialAccountBalanceAnchor`)**: valor observado, instante e data de negócio, natureza (`CurrentBalance`, `AvailableBalance`, `Unknown`), origem, confiabilidade (`Reliable`, `Provisional`, `Unreliable`) e o saldo inicial derivado (valor e data). Histórico mantido; uma vigente por conta.
- **Mapeador**: encargos (IOF, tarifas, juros, multa) e operações genéricas com categoria de consumo do banco viram compra de cartão; estorno (`ESTORNO`), cashback e crédito com categoria de consumo viram `CardCredit`; transferência, operação de crédito contratada e crédito sem evidência continuam em revisão.
- **Cálculo de saldo**: o saldo inicial efetivo é o manual (que sempre prevalece) ou, sem ele, o derivado da âncora confiável, no repositório de contas, no Dashboard e na projeção/Agenda. A API de saldo informa `openingSource` (`Manual` ou `BalanceAnchor`).

### 13.3 Migration

`20261029000121_AddPluggyInvoiceClosureCreditsAndBalanceAnchors` (aditiva): colunas em `financial_credit_card_invoices` e `pluggy_card_bills`; coluna e índice único em `pluggy_postings` (ajuste de fatura) com as restrições de tipo ampliadas; tabelas `pluggy_card_bill_payments` e `financial_account_balance_anchors`; e a função da trava de pagamento (`saldo360_official_payment_guard`), que continua recusando pagamento em fatura **aberta** e agora também aceita fatura fechada pela instituição. O `Down` restaura a função original. Aplicada só no laboratório.

### 13.4 Regras de fechamento e de pagamento

- **Fechamento**: uma fatura do banco é fechada quando a data de fechamento informada (`billClosingDate`) chegou **e** há total; fatura só com vencimento nunca é considerada fechada, e fechamento ausente jamais é substituído pelo vencimento. Itens PENDING não alteram o fechamento. Não depende de conciliação, não cria ajuste e preserva o total do banco.
- **Três dimensões independentes** (também na tela de integração do cartão): ciclo (Aberta/Fechada pelo banco), pagamento (Não paga/Parcialmente paga/Paga, pelo maior entre o registrado e o informado pelo banco) e conciliação (Não verificada/Conciliada/Divergente/Incompleta). Incompleta = itens pendentes ou em revisão da fatura (exceto evidências de pagamento) ou histórico importado que não cobre o ciclo; sem isso, total igual = Conciliada, diferente = Divergente. A diferença só é registrada, nunca compensada.
- **Pagamento**: saída de caixa, sem categoria de consumo (as compras continuam sendo o consumo). Reconhecido por (1) crédito de pagamento no cartão casado com o débito bancário (valor exato, data próxima, correspondência única) ou (2) `payments[]` da fatura casado com o débito que o banco rotulou como pagamento de cartão. A fatura de destino só é aceita quando inequívoca: o pagamento aparece em `payments[]` de uma única fatura, ou uma única fatura fechada comporta exatamente o valor, ou há uma única candidata (pagamento parcial); faturas que o banco já informa como pagas por outros pagamentos deixam de ser candidatas. Parcial é suportado e não depende da conciliação. Ambíguo, sem fatura, excedente ou sem conta integrada fica em revisão. Débito que já era despesa simples é substituído pelo pagamento (o lançamento antigo é cancelado, nunca somado).
- **Créditos**: estorno, devolução e cashback com referência à fatura do banco viram **crédito da fatura** (ajuste do tipo estorno/crédito), com a categoria da compra quando existe, reduzindo a obrigação e o consumo da categoria nos orçamentos; nunca são receita. Sem referência à fatura vão para revisão. Identificador externo estável impede duplicação.
- **Encargos e tarifas** entram como consumo do cartão (compra de uma parcela), com a categoria do catálogo.
- **Compras nas faturas do banco**: compras alocadas antes por calendário migram, sem mudar valor nem data, para a fatura real do banco (`billId`); as faturas de calendário que ficaram vazias e sem nenhuma referência são removidas.

### 13.5 Saldos e âncora

O saldo observado de cada conta bancária integrada é guardado como referência. O saldo inicial derivado é o observado menos os movimentos do intervalo coberto por movimentos consistentes; o intervalo começa no dia seguinte ao último movimento postado que o Saldo360 não contabiliza (em revisão ou ignorado); pendentes não entram. Antes dessa data o saldo é **indisponível** (nunca estimado). Não gera receita nem despesa, não ajusta diferenças e não grava nada no cadastro da conta; o saldo inicial manual continua prevalecendo. O limite do cheque especial **nunca é somado**: o saldo observado já vem líquido (negativo quando em uso). A referência é considerada não confiável (e não é usada) quando a conexão não está ativa, a conta nunca teve coleta completa, a natureza do saldo é desconhecida ou incoerente (saldo positivo com cheque especial em uso). Com saldo derivado da âncora, a diferença "banco − Saldo360" não é exibida (seria zero por construção).

### 13.6 Fechamento e vencimento dos cartões

O dia habitual vem do histórico de faturas do banco: `billClosingDate` para o fechamento e `dueDate` para o vencimento, sempre separados. A sugestão usa as 6 faturas fechadas mais recentes e só indica um dia quando pelo menos 2/3 delas concordam (cada dia é avaliado separadamente); caso contrário não sugere nada. Alterar o cadastro do cartão exige confirmação explícita (`POST /links/{id}/card-schedule-suggestion` com `confirm`), e o valor é recalculado no servidor.

### 13.7 Reprocessamento e resultado no laboratório

Operação controlada = o próprio reprocessamento (`POST /pluggy-integration/reprocess`, também executado ao fim de cada coleta): idempotente, sem excluir histórico bancário, preservando categorias manuais e transferências PF/PJ. Executado só no `saldo360_pluggy_lab`, após uma nova coleta (que trouxe os pagamentos das faturas).

| Indicador | Resultado |
|---|---|
| Faturas do banco já fechadas (data de fechamento passada) | 59 |
| Faturas do Saldo360 (todas as do banco com total ou pagamento) | 52, todas **fechadas pela instituição** (0 oficiais); 0 faturas de calendário restantes |
| Compras de cartão (uma parcela cada) | 895 (eram 869: +26 encargos e débitos genéricos com categoria de consumo); 895 de 895 na fatura real do banco |
| Estornos, devoluções e cashback aplicados como crédito de fatura | 65 (nenhum virou receita) |
| Pagamentos de fatura reconhecidos | 48 (47 pelo crédito de pagamento no cartão, 1 por `payments[]` da fatura) |
| Dos 39 débitos bancários que eram despesa simples e tinham o rótulo de pagamento de cartão | 21 reclassificados como pagamento; 18 permanecem como saída simples por não haver nenhuma evidência estruturada (nem `payments[]` nem crédito no cartão) |
| Estado de pagamento das 52 faturas fechadas | 45 pagas, 3 parcialmente pagas, 3 não pagas, 1 zerada |
| Conciliação das 59 faturas | 21 conciliadas, 9 divergentes, 29 incompletas (19 com itens em aberto, 10 com histórico que não cobre o ciclo) |
| Itens em revisão | de 197 para 60 |
| Movimentos de caixa (lançamentos ativos) | 781 → 783: +2, itens novos da coleta de 29/10; o resto só mudou de natureza (despesa simples → pagamento de fatura) |
| Reprocessar de novo | 0 criados, 0 atualizados |
| Âncoras de saldo | 3 contas com saldo calculado disponível sem saldo inicial manual (1 confiável, 2 provisórias); Dashboard com saldo realizado disponível |

### 13.8 Divergências restantes

- 9 faturas divergentes: 8 com diferença de R$ 100,00 ou mais e 1 menor; refletem compras e encargos que o banco cobra e a Pluggy não devolveu como transações (ou valores financiados), e permanecem apenas registradas.
- 29 faturas incompletas: 19 por itens ainda em revisão (principalmente operações de crédito contratadas no cartão, créditos sem categoria de consumo e moeda estrangeira) e 10 porque o histórico importado começa dentro do ciclo.
- 5 pagamentos informados pelo banco e 4 créditos de pagamento no cartão sem saída bancária correspondente (cartões pagos por conta não integrada): ficam como evidência, sem pagamento criado.
- 3 faturas não pagas e 3 parciais, que o Saldo360 não consegue ligar a nenhuma saída bancária.
- 18 débitos rotulados como pagamento de cartão sem evidência (provavelmente cartões de outras instituições não integradas).
- *(Atualizado na T09.3.3, §14: as pagadoras foram configuradas e a cobertura de cartões fechou; o que resta é o saldo de abertura do mês corrente.)* A cobertura do Dashboard continuava "incompleta" porque os cartões não tinham **conta de pagamento padrão** cadastrada: as obrigações de cartão ficam sem conta alocada e as faturas não pagas não entram na projeção. É cadastro do usuário.

### 13.9 Itens tratados fora do código a pedido do usuário

- Entradas "Entrada GRPQA LTDA" (aluguel) de 08/10/2026 e as 11 mensais anteriores (nov/2025 a set/2026) estavam em revisão (rótulo `TRANSFERENCIA_MESMA_INSTITUICAO` sem contrapartida integrada) e foram contabilizadas como **receita** por decisão do usuário, pelo fluxo de revisão do produto.
- O débito de teste que foi recategorizado na T09.3.1 teve a categoria removida.

### 13.10 Testes

- Mapeador (puro): estornos, cashback, créditos por categoria, encargos, operação genérica com categoria de consumo, operação de crédito contratada e transferência no cartão.
- `PluggyCardScheduleTests`: sugestão consistente, independência entre fechamento e vencimento, histórico insuficiente/aberto/sem fechamento, só as faturas mais recentes.
- Integração (API + PostgreSQL + Pluggy simulado), entre outros: fatura fechada e conciliada; fatura com compras ausentes (divergente, sem ajuste); item PENDING (incompleta, fechamento mantido); encargos conciliados; estorno e cashback como crédito de fatura (sem receita e sem duplicar ao reprocessar); pagamento integral por `payments[]` sem duplicar consumo; pagamento parcial; pagamento ambíguo; pagamento sem conta integrada; débito já lançado reclassificado; âncora com histórico limitado e saldo antes do intervalo indisponível; cheque especial nunca somado e natureza do saldo contraditória; sugestão de fechamento/vencimento com confirmação e histórico inconsistente; reprocessamento idempotente com categoria manual, transferência PF/PJ e pagamentos preservados; consistência do Dashboard e da Agenda. Testes anteriores ajustados às regras novas (fatura fechada pelo banco não é imutável; fechamento não depende de conciliação).
- Front: três dimensões por fatura, sugestão de dias com confirmação, origem do saldo (referência bancária).
- Resultados: suíte completa do backend (1.765 testes): 1.759 aprovados e 6 falhas, 3 delas em `RealOcrToolTests` (dependem de Poppler/Tesseract reais, ausentes neste ambiente, sem relação com a integração) e 3 regressões desta tarefa (inventário de reset e snapshot do importador não conheciam as tabelas `pluggy_card_bill_payments` e `financial_account_balance_anchors`); foram corrigidas (fixture de reset, contagem de 42 tabelas e inventário do snapshot somente leitura) e as áreas de reset, bootstrap e importador passaram 76/76 na reexecução focada (a suíte completa não foi repetida depois da correção; resta apenas o OCR ambiental). Frontend: 430 testes aprovados, `tsc` e build sem erros.

### 13.11 Limitações

1. A fatura fechada pelo banco **não é oficial**: continua editável (compras e créditos podem mudar se o banco corrigir os dados). Fatura oficial (extrato com documento) segue sendo outro fluxo, inalterado.
2. O total bancário não substitui a composição nas obrigações: Dashboard, Agenda e projeção continuam somando compras, encargos e créditos importados menos pagamentos. Fatura divergente ou incompleta pode **subestimar** a obrigação; a diferença aparece na tela de integração, mas ainda não alimenta a cobertura do Dashboard.
3. Pagamento só é reconhecido com evidência estruturada (crédito no cartão ou `payments[]`) e saída em conta integrada; pagamento por conta não integrada não gera movimento.
4. Estorno reduz o consumo da categoria quando existe uma categoria equivalente à da compra no momento do processamento; estornos aplicados antes de a categoria existir ficam sem categoria.
5. O saldo derivado da âncora supõe que o saldo observado e as transações vieram da mesma coleta; movimentos lançados depois do instante observado entram a partir da data seguinte. Metas e alocações continuam exigindo saldo inicial manual.
6. O limite do cheque especial é só exibido; saldo disponível (com limite) não é calculado.
7. Faturas passadas anteriores ao início do histórico importado ficam como "histórico não cobre o ciclo".

## 14. T09.3.3 — Conta principal e contas pagadoras

Objetivo: configurar uma conta principal por orçamento e a conta bancária que normalmente paga cada cartão, sem tocar em saldos, movimentações ou pagamentos já feitos. Lab: `saldo360_pluggy_lab`. Nenhum commit, push ou deploy; a T09.4 não foi iniciada. Só contagens e nomes de produto aparecem aqui: nada de valores, descrições, identificadores ou números de conta.

### 14.1 Conta principal

- **Uma por workspace**, garantida no banco por um índice único parcial (`ux_financial_accounts_primary`, `WHERE is_primary`). Só **conta bancária (corrente ou poupança) ativa** pode ser principal: o domínio recusa e uma restrição de verificação no banco (`ck_financial_accounts_primary_eligible`) repete a regra. Dinheiro em espécie não é conta bancária. Cartão não é uma conta financeira, então não tem rota nem identificador que o aceite como principal.
- **Troca atômica**: a operação roda numa transação com o workspace bloqueado (`SELECT ... FOR UPDATE`, o mesmo padrão do motor de lançamentos). Desmarca a anterior, grava, marca a nova e confirma tudo junto. Trocas concorrentes são serializadas e terminam com exatamente uma principal (teste com 6 requisições simultâneas).
- **Desativar ou deixar de ser bancária**: a conta principal não pode ser desativada nem virar dinheiro sem uma de duas decisões explícitas na mesma requisição: indicar uma **conta substituta** (`replacementPrimaryAccountId`, bancária, ativa e do mesmo orçamento) ou **remover a condição** (`isPrimary=false`). Substituta inválida ou a própria conta recusam a operação inteira (nada muda).
- **Não altera nada financeiro**: saldos, lançamentos, transferências, pagamentos e a conta pagadora dos cartões ficam idênticos (teste compara saldos, lançamentos, transferências, pagadoras e o consolidado da agenda antes e depois da troca). A principal é **só a seleção padrão** das telas; não é a origem obrigatória de nenhum pagamento.
- **API**: `PUT /accounts/{id}/primary` (define), `DELETE /accounts/{id}/primary` (remove, idempotente), e `isPrimary` / `replacementPrimaryAccountId` opcionais no cadastro e na edição (ausente = não altera, para que edições comuns, o saldo inicial sugerido e outros fluxos nunca removam a principal sem querer).
- **Interface**: selo "Conta principal", botões "Definir como principal" / "Remover condição de principal" (só em conta bancária ativa, com confirmação), caixa "Definir como conta principal" no formulário, e a origem **Pluggy ou Manual** continua visível em cada conta. Desativar a principal avisa e envia a remoção explícita.

### 14.2 Conta bancária pagadora padrão dos cartões

- O cartão já tinha a preferência (`DefaultPaymentAccountId`); a T09.3.3 a torna **individual, explícita e mais estrita**: a conta precisa ser **bancária, ativa e do workspace** (dinheiro, conta inativa e conta de outro workspace são recusados, inclusive no vínculo Pluggy). Não há restrição de escopo: **conta PF pode pagar cartão PJ** e o inverso.
- Alterar a pagadora é permitido e **não reescreve nada**: pagamentos de fatura já registrados continuam vinculados à conta que realmente pagou, transferências e lançamentos não mudam, e a configuração **nunca registra pagamento**. A regra anterior continua valendo: enquanto houver conciliação ativa de fatura planejada daquele cartão, a pagadora só muda depois de desfazê-la.
- **Escopo do cartão**: o cartão não tinha PF/PJ próprio. Agora a API devolve `origin` (Manual/Pluggy) e `scope` **derivado do vínculo Pluggy**: é o escopo das contas bancárias da mesma conexão, e fica sem escopo quando não há conta bancária de escopo único. Assim "Nubank PJ" e "Itaú Empresas" continuam identificados como PJ mesmo pagos pela conta PF. O escopo não muda com a pagadora.
- **Interface**: o formulário lista só contas bancárias ativas (com PF/PJ e "principal"), explica que é preferência, e o detalhe do cartão mostra "Cartão PF/PJ" e a pagadora padrão.

### 14.3 Pagamento de cartão PJ por conta PF

O caminho é o mesmo da T09.3.2 e foi **testado ponta a ponta** com conexões separadas: o pagamento vira **uma única saída de caixa na conta PF**, liquida a fatura do cartão PJ, **não cria segunda despesa de consumo** e **não cria transferência PF/PJ** (nenhuma regra ou movimentação a justificaria). A conta PJ fica sem movimento. Consumo por cartão (faturas) e caixa por conta pagadora (movimentos da conta) são consultas separadas. Reprocessar e trocar a pagadora depois mantêm o histórico e a contagem.

### 14.4 Dashboard, Agenda e projeções

- **Agenda**: com principal ativa, abre **já selecionada** na conta principal (sem uma consulta consolidada à toa); "Todas as contas" segue um clique adiante e a escolha do usuário nunca é sobrescrita. Sem principal, ou com principal inativa, continua consolidada. As projeções por conta seguem a conta selecionada.
- **Dashboard**: não tem filtro por conta, então não há seleção inicial a trocar; ele mostra o selo "Principal" na lista de contas e o consolidado de todas as contas **não muda** ao definir a principal (teste compara fluxo de caixa, indicadores e cobertura).
- **Efeito da pagadora nos cálculos**: com a pagadora definida, as obrigações do cartão passam a ser alocadas à conta pagadora nas projeções (Agenda por conta, saldos projetados, cobertura do Dashboard). É o uso previsto da preferência; a fórmula não mudou.

### 14.5 Regras operacionais do Itaú PJ

As informações (impostos, cartão Itaú PJ, contabilidade, parcelas e seguro do PRONAMPE) **não foram transformadas em regra de código**: o sistema não cria despesa, não presume categoria e não assume que toda saída do Itaú PJ pertence a elas. A classificação continua pelas regras já existentes (manual, regras, catálogo, IA sob confirmação); se for desejado, essas preferências podem entrar depois como sugestão de categoria ou conferência, nunca como lançamento automático.

### 14.6 Migration

`20261029013947_AddPrimaryFinancialAccount`: coluna `is_primary` (padrão falso), índice único parcial por workspace e restrição de verificação (bancária e ativa). Aplicada no lab; o `Down` remove os três. Não há tabela nova (inventários de reset e do importador inalterados). Backup do lab antes da migration em arquivo fora do repositório.

### 14.7 Configuração inicial e resultado no laboratório

Feita pela API real, na sessão do usuário, **sem criar nenhuma movimentação**:

| Configuração | Resultado |
|---|---|
| Conta principal | Itaú PF (única do workspace) |
| Cartões PF (3) | Itaú PF como pagadora |
| Nubank PJ (cartão "company") | Itaú PF como pagadora |
| Cartão do Itaú PJ (Itaú Empresas Visa) | Itaú PJ como pagadora |

Cartões e contas mantêm o vínculo Pluggy; os nomes vieram da API (nada assumido). Comparação antes e depois: contagens de lançamentos, transferências, pagamentos de fatura, faturas, compras, ajustes, lançamentos por estado e âncoras **idênticas**; fluxo de caixa realizado, saldo realizado e indicadores do Dashboard **idênticos** (o realizado da Agenda sai dos mesmos lançamentos). Mudaram apenas, como esperado: a cobertura de cartões do Dashboard (obrigações de cartão sem conta de pagamento: **sim para não**) e a projeção da Agenda, que agora inclui as obrigações dos cartões alocadas às pagadoras.

Pendência já existente e **não causada** por esta tarefa: a projeção do mês corrente no Dashboard segue sem fechamento calculável porque duas contas têm saldo inicial derivado da âncora com data de referência dentro do mês (Itaú PF em 07/10 e Itaú PJ em 22/10), então o saldo de abertura do primeiro dia do mês não existe; por isso a cobertura geral continua "parcial".

### 14.8 Correções do code-review da T09.2/T09.3.2 feitas junto

Dois defeitos reais, com testes que falham sem a correção: (1) estorno/cashback cujo valor muda na origem recriava o ajuste com o mesmo identificador externo e violava o índice único, derrubando o sync (agora o identificador ganha versão: `pluggy:{id}`, `:v2`, `:v3`); (2) o `payments[]` já explicado em rodada anterior voltava a concorrer com outro débito igual (agora pagamentos já registrados contam como consumidos). Também: `ReconciledAt` só muda quando o resultado muda, o `ValueType` do pagamento entra na comparação de idempotência e o resumo da classe `PluggyCardBill` voltou ao lugar. **Não alterado de propósito**: a fatura fechada pelo banco continua espelhando correções da origem (não é oficial; a divergência aparece na conciliação sem compensação); metas continuam exigindo saldo inicial manual.

### 14.9 Testes

- Backend novo: `PrimaryAccountAndCardPayerTests` (12) cobre primeira principal, substituição, concorrência, não bancária/inativa, restrições do banco, isolamento entre workspaces, desativação com/sem substituta, remoção explícita, ausência de efeito financeiro, Dashboard, pagadora por tipo/conta/estado e PF pagando cartão PJ; mais 3 testes de integração Pluggy (PF paga cartão PJ, estorno alterado na origem, pagamento já registrado).
- Frontend novo: contas (6), agenda (3), cartões (1) e Dashboard (1).
- Resultados: suíte completa do backend (1.780 testes): 1.777 aprovados e 3 falhas, todas em `RealOcrToolTests` (dependem de Poppler/Tesseract reais, ausentes neste ambiente, sem relação com esta tarefa). Frontend: 441 testes aprovados, `tsc` e build sem erros; o lint só traz avisos antigos de outros componentes.
