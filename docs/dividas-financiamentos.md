# Dívidas e financiamentos (T13.1)

O módulo registra contratos familiares sem transformar o cadastro em lançamento, previsão, parcela ou pagamento. São suportadas as modalidades `PersonalLoan`, `VehicleFinancing`, `Mortgage`, `BusinessLoan`, `RenegotiatedDebt` e `Other`.

## Valores contratuais

- `originalPrincipal`: principal originalmente contratado;
- `outstandingPrincipal`: principal ainda não amortizado, quando conhecido;
- `outstandingBalance`: saldo devedor informado pelo credor na data de referência;
- `installmentAmount`: valor contratual da parcela, sem inferir seus componentes.

Esses campos são independentes. O sistema não multiplica parcelas restantes para estimar saldo devedor e não calcula juros futuros.

Taxas são decimais exatos e preservam o período `Monthly` ou `Annual`. Métodos `Price`, `Sac`, `Other` e `Unknown` são apenas informações contratuais; nenhuma tabela de amortização é calculada nesta etapa.

## Estados e conta

Os estados são `Active`, `Suspended`, `Settled` e `Archived`. Suspender, retomar e liquidar são operações explícitas; arquivar é lógico e preserva o histórico. A conta preferencial deve pertencer à mesma família e estar ativa, mas seu vínculo não representa pagamento.

## REST

- `GET /api/families/{familyId}/debts?includeArchived=false`
- `POST /api/families/{familyId}/debts`
- `GET /api/families/{familyId}/debts/{id}`
- `PUT /api/families/{familyId}/debts/{id}`
- `POST /api/families/{familyId}/debts/{id}/suspend`
- `POST /api/families/{familyId}/debts/{id}/resume`
- `POST /api/families/{familyId}/debts/{id}/settle`
- `DELETE /api/families/{familyId}/debts/{id}`

Valores monetários e taxas são strings decimais com duas casas. Datas são civis (`yyyy-MM-dd`). Escritas exigem autenticação, participação na família e CSRF.

## Limitações

Não há geração de parcelas, pagamentos, amortizações, juros futuros, integração com Agenda/Orçamentos, associação automática a recorrências ou faturas de cartão. Recorrências existentes não são convertidas nem canceladas. A conciliação explícita será tratada na T13.2.
## Parcelas e pagamentos (T13.2)

Parcelas são cadastradas explicitamente; o sistema não gera cronogramas Price/SAC. Cada parcela possui número, vencimento e valor decimal exato. Componentes de principal, juros, seguros e encargos são opcionais e não são inferidos.

Pagamentos parciais ou múltiplos podem ser registrados pelos endpoints `/debts/{debtId}/installments/{installmentId}/payments`. Cada pagamento exige `operationId` idempotente e cria exatamente um lançamento bancário com origem `DebtInstallmentPayment`. Reenvios retornam o mesmo pagamento; valores que excedem o saldo da parcela são rejeitados. O cancelamento é lógico e cancela também o lançamento bancário na mesma transação.

Parcelas com saldo restante aparecem na Agenda como obrigações `DebtInstallment`, pelo valor ainda devido. Pagamentos já registrados não são exibidos novamente como compromisso. Parcelas sem conta preferencial permanecem não alocadas e não são atribuídas arbitrariamente a outra conta.

As projeções usam `remainingAmount = scheduledAmount - validPaidAmount`; parcelas vencidas são incorporadas uma única vez a partir da data-base. A conciliação explícita com previsões permanece fora desta etapa.

Estados derivados da parcela: `Pending`, `PartiallyPaid`, `Paid`, `Overdue` e `Cancelled`. A distinção entre valor pago e amortização é preservada: somente componentes explicitamente informados reduzem a principal; nenhum componente é deduzido por diferença.
## Interface web (T13.4/T13.4.1)

A seção **Dívidas** foi adicionada à navegação da família e funciona em desktop e telas pequenas usando os componentes compartilhados. O formulário de cadastro e edição envia o contrato completo, preservando campos opcionais como `null`; valores monetários continuam strings decimais e não são calculados no React.

O detalhe apresenta principal original, principal em aberto, saldo contratual e o histórico de parcelas (número, vencimento, valor nominal, pago, restante e estado). Ações de suspender, retomar, liquidar e arquivar atualizam a lista sem duplicar dados financeiros.

Pagamentos podem ser registrados quando existe conta responsável. Cada parcela mantém um `operationId` estável durante a sessão para que reenvios e duplo clique sejam idempotentes no backend. O histórico é carregado sob demanda e permite cancelamento lógico após confirmação explícita; o cancelamento atualiza a parcela e as projeções por meio dos endpoints existentes.

A troca de família cancela a consulta anterior e limpa a seleção, evitando que uma resposta antiga apareça no contexto novo. A pré-visualização e edição não implementam conciliação visual; essa etapa permanece fora da T13.4.1.

## Conciliação contextual (T13.4.2)

Cada parcela aberta apresenta uma seção dedicada para consultar previsões de despesa pendentes da mesma família e, quando possível, da conta responsável. A seleção é manual e a comparação mostra dívida, vencimento, valor nominal, restante, conta e categoria ao lado da previsão. Divergências de valor ou data são destacadas sem conversão numérica ou alteração automática.

Antes do `POST /api/families/{familyId}/debt-installment-reconciliations`, a interface exige revisão explícita e informa que parcela e previsão permanecem armazenadas, que a obrigação será contabilizada uma única vez e que nenhum pagamento bancário será criado. O vínculo ativo é consultável e pode ser removido por `DELETE`; a remoção é lógica e não cancela a previsão.

As ações **Ver na Agenda**, **Ver no Orçamento** e **Ver no Planejamento** preservam a família e a competência da parcela. Após conciliar ou desfazer, a família é notificada para invalidar Agenda, saldos, Orçamentos e Planejamento. Consultas são abortadas na troca de família e erros de elegibilidade/conflito são exibidos sem esconder a resposta do servidor.

Limitações: a lista de previsões usa o endpoint paginado existente, filtros de despesa/conta como auxílio e carregamento incremental por página; a API continua sendo a autoridade final para elegibilidade, concorrência e vínculos 1:1. Não há conciliação automática nem cálculo financeiro no React.

### Validação frontend

Foram adicionados testes comportamentais para cadastro com nulos, edição preservando precisão textual e reenvio de pagamento com a mesma chave. O Vitest padrão (`npm test -- ...`) pode permanecer aguardando workers neste ambiente; a execução determinística foi validada com `vitest run --pool=threads --maxWorkers=1 --no-file-parallelism`, que passou para `FinancialDebts.test.tsx` (3 testes) e `FinancialCalendar.test.tsx` (17 testes).
