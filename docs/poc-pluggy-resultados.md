# T09.1 — POC Open Finance com Pluggy: resultados

**Estado: verificado com dados reais em 2026-10-28** (três Itens do Meu Pluggy: Itaú PF, Itaú PJ e Nubank PJ).

Os dados abaixo vêm de consultas diretas à API da Pluggy com as credenciais locais, usando os `itemId` cadastrados no laboratório, e de agregados (contagens, intervalos, presença de campos). Nenhuma descrição, valor ou número de conta foi copiado para este documento. As consultas foram feitas por um script de sondagem, e não pela tela de diagnóstico; ele replica o que o diagnóstico mede, mas com contagens por conta e seus próprios critérios. Os Itens estavam em `UPDATED/SUCCESS`, última sincronização em 27/10/2026, autorização válida até 27/10/2027.

**Ressalva central:** os três Itens usam o conector **MeuPluggy** (`type=PERSONAL_BANK`, `isOpenFinance=false`), ou seja, não são conexões Open Finance regulamentadas. O nome da instituição de cada Item ("Itaú PF" etc.) é o rótulo que você cadastrou; a API não identifica o banco de origem no conector. Conclusões valem para o Meu Pluggy, não para os conectores diretos ou regulados das mesmas instituições.

## Matriz de cobertura

| Recurso | Itaú PF | Itaú PJ | Nubank (PJ) | Uso no Saldo360 |
|---|---|---|---|---|
| Saldo | Disponível: 1 conta corrente + 3 cartões, com `balance`, `bankData.closingBalance`, atualizado em 27/10 | Disponível: 1 conta corrente + 1 cartão | Disponível: 1 conta corrente + 1 cartão | Contas |
| Transações | Disponível: conta 723 (28/10/2025–14/12/2026); cartões 39, 872 e 153 | Disponível: conta 130 (30/10/2025–27/10/2026); cartão 24 | Disponível, **pouco volume na conta**: conta 2 (11/12/2025–05/05/2026); cartão 61 | Lançamentos |
| Limites | Disponível: cheque especial contratado/utilizado na conta; limite total, disponível e pagamento mínimo nos 3 cartões | Idem (1 conta, 1 cartão) | Idem; `creditData.level` ausente | Projeção |
| Cartões | Disponível: bandeira, nível, número, vencimento (`balanceDueDate`); **`balanceCloseDate` sempre ausente** na conta do cartão | Idem | Idem, sem `level` | Cartões |
| Faturas | Disponível: 12, 12 e 11 por cartão (vencimentos 05/11/2025–15/10/2026); **nenhuma fatura futura/aberta** | Disponível: 12 (15/11/2025–15/10/2026) | Disponível: 12 (04/11/2025–13/10/2026), com encargos em 2 faturas | Cartões |
| Parcelas | Disponível em 2 dos 3 cartões: 344 e 96 transações parceladas, 52 e 12 com data futura (até 07/2027) | **Sem parcelamentos** retornados | Disponível: 36 transações parceladas, 13 com data futura (até 01/2027) | Previsões |

Legenda: "Disponível" = consulta ok com registros. Os números de contas/cartões mostram cada conta; quando há cartões diferentes no mesmo Item eles aparecem separados por vírgula.

### Campos observados

- **Contas**: sempre presentes `balance`, `number`, `bankData`/`creditData`; `marketingName` quase sempre ausente; `creditData.balanceCloseDate` sempre nulo; `taxNumber` e `owner` aparecem em pelo menos parte das contas (o laboratório não os exibe).
- **Transações (todas as contas)**: `id`, `date`, `description`, `descriptionRaw`, `amount`, `currencyCode`, `type`, `status`, `category`/`categoryId` (quase 100%), `operationType`. **Nunca vieram**: `balance`, `providerCode`, `paymentData.referenceNumber`, `creditCardMetadata.totalAmount`. `amountInAccountCurrency` aparece em 0,1% a 8% das transações de cartão.
- **Pix/transferências**: `paymentData` com pagador/recebedor em boa parte das transações de conta (ex.: Itaú PF, `payer` em 694 de 723); `merchant.name` é esparso (19 de 723 na conta Itaú PF), `merchant.cnpj` é mais frequente.
- **Cartões**: `creditCardMetadata.billId` presente na maior parte das efetivadas; `installmentNumber`/`totalInstallments` presentes em todas as parceladas; **`totalAmount` do parcelamento nunca vem** (a documentação diz que só conectores diretos o entregam).
- **Status**: transações `PENDING` existem: 2 na conta do Itaú PF e, nos cartões, 2, 64 e 16 (Itaú PF), 1 (Itaú PJ) e 25 (Nubank). Há `PENDING` com data futura na conta do Itaú PF.
- **Faturas**: `dueDate`, `totalAmount`, `minimumPaymentAmount`, `allowsInstallments` em todas; **`billClosingDate` em 100%** e **`payments[]`** (valor, data, `paymentMode`, `valueType`) em 5 de 12, 12 de 12 e 11 de 11 (Itaú PF), 12 de 12 (Itaú PJ), 11 de 12 (Nubank). `financeCharges` só no Nubank (2 faturas: `LATE_PAYMENT_FEE`, `IOF`, `LATE_PAYMENT_REMUNERATIVE_INTEREST`, `OTHER`).
- **Investimentos**: só o Itaú PF retornou 3 (1 CDB e 2 fundos), todos com `status=TOTAL_WITHDRAWAL`. **Empréstimos**: nenhum conector declarou `LOANS` (Não suportado).
- **Histórico**: cerca de 12 meses (a partir de 27–30/10/2025) em todas as contas.
- **Duplicidade**: nenhum ID repetido; 2 grupos com mesma data, valor e descrição na conta do Itaú PF (podem ser lançamentos legítimos repetidos).

## Como preencher a matriz

1. Siga [poc-pluggy.md](poc-pluggy.md) para configurar as credenciais e cadastrar o `itemId` de cada instituição.
2. No Laboratório Pluggy, selecione a conexão (*Inspecionar*) e use *Gerar diagnóstico*.
3. Copie para esta tabela, por instituição, a situação de cada recurso. Para cada conta, o diagnóstico mostra contagem, intervalo de datas retornado, campos presentes e ausentes e limitações medidas (pendentes × efetivadas, IDs repetidos, grupos com mesma data/valor/descrição, registros futuros, truncamento).
4. Não extrapole: um campo "ausente" significa ausente em todos os registros consultados naquele momento.

## O que o contrato documentado indica (não verificado com dados reais)

Fonte: documentação pública da Pluggy consultada em 2026-10-28. São expectativas de contrato, não resultados.

- **Autenticação**: `POST /auth` com `clientId`/`clientSecret` devolve `apiKey`, enviada depois no cabeçalho `X-API-KEY`. A validade de 2 horas da chave vem do conhecimento prévio do contrato, não da página de referência consultada; o cliente renova com folga (110 min) e também renova uma vez ao receber 401, o que cobre uma validade diferente.
- **Item** (`GET /items/{id}`): `status`, `executionStatus`, `lastUpdatedAt`, `consentExpiresAt`, `error{code,message}` e `connector{name,type,products,isOpenFinance…}`. A lista de valores de `status` não é enumerada na página de referência; o laboratório trata `UPDATED`, `UPDATING`, `LOGIN_ERROR`, `OUTDATED`, `WAITING_USER_INPUT`/`WAITING_USER_ACTION` e classifica qualquer outro como erro.
- **Contas** (`GET /accounts?itemId=`): `balance`, `currencyCode`, `number`, `bankData{transferNumber, overdraftContractedLimit, overdraftUsedLimit, unarrangedOverdraftAmount, closingBalance…}` e `creditData{creditLimit, availableCreditLimit, minimumPayment, balanceCloseDate, balanceDueDate, brand, level…}`.
- **Transações** (`GET /v2/transactions`): paginação por cursor (`after`; a resposta traz `next` como query string pronta, ou `null` na última página); filtros `dateFrom`/`dateTo`; página padrão de 500. `status` ∈ {PENDING, POSTED}; `type` ∈ {DEBIT, CREDIT}; `paymentData`, `creditCardMetadata` (`installmentNumber`, `totalInstallments`, `totalAmount`, `billId`, `cardNumber`…) e `merchant`. A documentação indica que `totalAmount` só vem de conectores diretos e `billId` só de Open Finance. O endpoint legado `GET /transactions` está marcado como obsoleto e **não é usado**.
- **Faturas** (`GET /bills`): `page`/`pageSize`; `dueDate`, `totalAmount`, `minimumPaymentAmount`, `allowsInstallments` e `financeCharges[{type, amount, currencyCode, additionalInfo}]`. A página de referência consultada **não lista** `billClosingDate` nem `payments[]`, mas as respostas reais do Meu Pluggy os trazem (ver "Campos observados"); o laboratório passou a lê-los.
- **Empréstimos** (`GET /loans?itemId=`) e **investimentos** (`GET /investments?itemId=`) só são consultados quando o conector declara o produto (`LOANS`/`INVESTMENTS`) em `connector.products`.
- **Parcelas**: não há endpoint próprio. O diagnóstico deriva parcelas das transações de cartão com `totalInstallments > 1` e informa quantas têm data futura — é isso que mostrará se parcelas futuras são entregues.

## Limitações conhecidas da POC

- **Mudança de status e duplicidade**: um diagnóstico é um retrato único. Transições PENDING→POSTED só podem ser comprovadas comparando sincronizações sucessivas (a POC não persiste dados da Pluggy). A heurística de duplicidade (mesma data, valor e descrição) pode sinalizar lançamentos legítimos repetidos.
- **Histórico**: o diagnóstico percorre até `Pluggy:MaxTransactionPages` páginas (padrão 20) por conta; se houver mais, o relatório informa truncamento.
- **IDs internos**: contas da Pluggy não são persistidas na POC, então não existe ID interno de conta. As rotas de transações/faturas recebem o UUID da conta na Pluggy, mas o backend só atende se a conta pertencer a um Item vinculado ao orçamento do usuário (consulta `GET /accounts/{id}` e confere o `itemId`); caso contrário responde 404. Itens e conexões usam ID interno.
- **Rotas**: seguem o padrão do projeto (`/api/financial-workspaces/{workspaceId}/pluggy-poc/...`), e não o `/api/pluggy-poc/...` sugerido, porque autorização e o middleware de escrita do projeto são resolvidos pelo `workspaceId` da rota.
- **Autorização**: não há papéis além do dono do orçamento. O laboratório fica oculto (404) enquanto `Pluggy:Enabled` for falso e só responde ao dono do orçamento. Como o Meu Pluggy dá acesso às contas pessoais de quem criou as credenciais, um mesmo `itemId` só pode ser vinculado uma vez (índice único).
- **Rate limit**: ao receber 429 o diagnóstico interrompe as demais consultas e marca os recursos restantes com erro `RateLimited`, em vez de insistir.

## Recomendações de aproveitamento (com base nos dados reais acima)

| Área | Aproveitamento proposto | Evidência e ressalva |
|---|---|---|
| Contas | Conferir saldo (saldo informado × calculado) | `balance` presente em todas as contas, atualizado na última sincronização (27/10). Só o saldo atual é entregue; não há saldo por dia. |
| Lançamentos | Sugerir lançamentos para **revisão**, nunca lançar automaticamente | Histórico de ~12 meses, nenhum `id` repetido, `descriptionRaw` sempre presente. Ressalvas: transações `PENDING` precisam de tratamento (podem mudar), `balance` por lançamento não vem, e a estabilidade do ID entre sincronizações só se prova comparando coletas. |
| Projeção | Usar limite disponível/utilizado e cheque especial como sinal de risco de caixa | Presentes nas contas e cartões; podem ser zero. |
| Cartões e faturas | Detectar fatura (fechamento, vencimento, valor, pagamento mínimo) sem depender de PDF; conciliar pagamentos da fatura | `billClosingDate` e `dueDate` em 100% das faturas e `payments[]` na maioria. **Não há fatura aberta/futura** retornada: a mais recente já venceu (13–15/10), então a fatura corrente e as parcelas por vir continuam dependendo de transações do cartão ou do PDF. |
| Previsões | Alimentar parcelas futuras de compras no cartão | Há transações com `installmentNumber`/`totalInstallments` e data futura (até 07/2027) em 3 dos 5 cartões. Falta `totalAmount` da compra e não há garantia de que todas as parcelas restantes estejam listadas; precisa de conciliação com o plano de parcelas do Saldo360. |
| Dívidas | Saldo devedor de empréstimos | Nenhum conector declarou `LOANS`: não aproveitável com estes Itens. |
| Investimentos | Informativo | Apenas 3 posições (todas `TOTAL_WITHDRAWAL`) no Itaú PF. |

**Riscos de aproveitamento:** os três Itens são do conector MeuPluggy (não regulado), com autorização por 12 meses e dados que dependem da coleta do próprio conector. Qualquer integração definitiva exigirá decisões fora desta POC: persistência dos dados, idempotência por ID de transação, tratamento de transações `PENDING` que mudam, política de consentimento/expiração e conciliação com os módulos existentes.
