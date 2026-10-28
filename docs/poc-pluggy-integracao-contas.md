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

- **Validação com dados reais pendente.** Nenhuma regra desta etapa foi exercitada contra os três Itens reais; a estabilidade da T09.2 (≥ 2 sincronizações) ainda precisa ser medida e é pré-requisito para confiar no pareamento.
- **Fatura oficial só com total exato.** Encargos, juros, estornos de cartão, histórico parcial e itens pendentes mantêm a fatura aberta e o pagamento em revisão; não há lançamento automático de encargos nem de estorno de cartão (ficam em revisão).
- Quando a compra não traz fatura do banco, usa-se o calendário do cartão (dias informados), que pode diferir do fechamento real; compras já lançadas dessa forma não são movidas depois.
- Cada transação de cartão é uma compra de uma parcela: não há ligação entre parcelas nem projeção de parcelas futuras além das que a Pluggy devolve.
- Transferências só entre contas bancárias integradas; saque, resgate e transferência com contas não integradas ficam em revisão. Estorno bancário (`ESTORNO`/`DEVOLUCAO`) vai para revisão, sem reduzir a despesa original.
- Só contas `BANK` e `CREDIT`, só BRL; investimentos e empréstimos são ignorados. Data de negócio = dia UTC informado pelo banco.
- Não há "desvincular" nem trocar o início da importação depois do vínculo; vincular a uma conta existente exige escolher a data (movimentos anteriores não são importados).
- Pagamento de fatura de cartão cujo débito sai de conta não integrada fica em revisão (o Saldo360 não cria saída em conta que não acompanha).
- Mudança de origem num lançamento depois de pareado em transferência/pagamento desfaz o par e o reavalia; se não houver nova correspondência, vai para revisão.
- Sem agendamento: a sincronização continua manual.
