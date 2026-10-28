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
| Débito bancário igual (valor) e próximo (±3 dias) a um pagamento de fatura de cartão integrado, **correspondência única** | Pagamento de fatura (ver abaixo). A dica "descrição cita fatura" só desempata. |
| Transferência na mesma instituição, débito × crédito de mesmo valor em contas integradas diferentes (±1 dia), **correspondência única** | `FinancialTransfer`; não infla receitas/despesas. |
| `PIX`/`TED`/`DOC` bancário (disposição `TransferProbe`), débito × crédito de **mesma operação**, mesmo valor, contas integradas diferentes (±1 dia), **correspondência única** | `FinancialTransfer` (substitui o lançamento simples já criado). Sem ponta oposta numa conta integrada continua receita/despesa (a maioria dos PIX é com terceiros); ambíguo → revisão `AmbiguousTransferCounterpart`. Nunca usa a descrição. *(Regra acrescentada na validação com dados reais, §11.)* |
| Transferência/pagamento sem contrapartida, ambíguos, saque, resgate, estorno, crédito/empréstimo, crédito no cartão, operação de cartão desconhecida | **Revisão**: fora de receitas, despesas e saldos até decisão. |
| Categoria da Pluggy | Mapeada por nome (sem acento/caixa) ou apelido conhecido **só se o orçamento tiver a categoria**; senão sem categoria. |

**Pagamento de fatura sem duplicar consumo.** O consumo está nas compras do cartão (faturas); o pagamento é a saída de caixa (`CreditCardInvoicePayment`) e baixa a fatura. O banco do Saldo360 exige fatura **oficial** para registrar pagamento, então o motor fecha a fatura oficialmente (mesma auditoria das demais, sem documento) **quando o total informado pelo banco é exatamente a soma das compras importadas** e não há itens pendentes/em revisão da fatura. Sem isso, o pagamento vai para revisão ("sem fatura oficial") e a fatura aparece como divergente na integração do cartão.

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
- **Fatura oficial só com total exato.** Encargos, juros, estornos de cartão, histórico parcial e itens pendentes mantêm a fatura aberta e o pagamento em revisão; não há lançamento automático de encargos nem de estorno de cartão (ficam em revisão).
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
