# Cartões de crédito

## Importações (T10.3.5)

A aba **Importações** reúne prévias CSV/PDF, seleção explícita, categorias, associação de parcelas e histórico. PDF continua somente em análise; toda gravação depende de confirmação explícita nos endpoints existentes.

## Interface da família (T09.6)

A seção **Cartões** está disponível na navegação de cada família. Ela lista cartões ativos e arquivados, permite cadastrar/editar/arquivar, consultar limite (inclusive disponível negativo), compras, faturas por competência, parcelas, pagamentos e a projeção dos próximos seis meses. A tela usa exclusivamente os endpoints existentes; valores monetários permanecem como strings decimais e nenhuma soma é feita no navegador. Pagamentos só são oferecidos para faturas fechadas e o formulário usa a conta padrão do cartão quando ela estiver configurada.

Após qualquer alteração, a tela recarrega cartões, limite, compras, faturas, pagamentos e projeção para evitar dados obsoletos. Cartões arquivados continuam visíveis para consulta histórica e não podem receber novas compras ou alterações.

## Auditoria de integração (T09.7)

O fluxo integrado foi validado em PostgreSQL real com compra parcelada, orçamento, pagamento integral, cancelamento e cartão sem conta preferencial. Compra, fatura e pagamento permanecem entidades distintas; o pagamento altera o saldo uma única vez e não duplica o consumo do orçamento. Consulte o [relatório da auditoria](T09.7-auditoria-integracao.md).



Parcelas intermediárias de extratos são tratadas pela T10.3.3 somente por vínculo explícito a uma parcela normal existente ou como obrigação histórica unitária. Obrigações históricas exigem fatura/competência existente, não criam lançamentos bancários nem parcelas adicionais e são incluídas no total da fatura, limite utilizado, pagamentos e projeções.

A pré-visualização PDF da T10.3.4 é textual e conservadora. Ela mantém página e texto de origem, sinaliza divergências e não altera cartões, faturas ou saldos. Não há OCR nem reconhecimento automático de instituições.

## Conceito

`FinancialCreditCard` representa uma obrigação de crédito pertencente a uma família. Ele não é uma conta financeira: uma compra futura não movimenta imediatamente o saldo bancário; o pagamento de uma fatura será tratado em etapa posterior.

Esta etapa cobre somente cadastro, consulta, edição e arquivamento lógico. Não cria compras, parcelas, faturas, pagamentos, previsões ou transferências.

## Cadastro

Cada cartão possui instituição emissora, nome identificador, titular opcional, limite nominal, dia de fechamento, dia de vencimento e conta financeira preferencial de pagamento opcional. O titular precisa ser usuário membro da mesma família. A conta de pagamento precisa pertencer à família e estar ativa no momento do cadastro; desativar uma conta depois não remove o histórico do cartão.

O cadastro aceita opcionalmente `lastFourDigits`, sempre como texto com exatamente quatro algarismos (`0000` a `9999`). Zeros à esquerda são preservados e a API nunca recebe nem devolve o número completo. Cartões anteriores à migration permanecem compatíveis com valor `null`. Não há unicidade artificial: cartões legítimos podem compartilhar emissor e final, e a identificação de faturas retorna ambiguidade em vez de escolher o primeiro registro.

O limite é `decimal` não negativo, com até duas casas, e é serializado pela API como string decimal exata. Ele é apenas o limite nominal: limite utilizado ou disponível ainda não é calculado.

Fechamento e vencimento aceitam dias de 1 a 31. Dias 29, 30 e 31 são armazenados sem ajuste; a política para meses sem esse dia será definida quando forem implementados os ciclos de fatura.

## API

```text
GET    /api/families/{familyId}/credit-cards
GET    /api/families/{familyId}/credit-cards/{cardId}
POST   /api/families/{familyId}/credit-cards
PUT    /api/families/{familyId}/credit-cards/{cardId}
DELETE /api/families/{familyId}/credit-cards/{cardId}
```

As consultas retornam somente cartões da família da rota. A listagem omite arquivados por padrão; use `includeArchived=true` para incluí-los. DELETE apenas marca o cartão como arquivado e preserva os dados cadastrais.

## Persistência e limitações

A migration `AddFinancialCreditCards` cria a tabela, índices, checks de limite/dias, FKs para família, conta e titular e FKs de auditoria. Nenhuma movimentação financeira ou cálculo de saldo/orçamento é alterado.

Ainda não há regra de determinação da fatura, compras, parcelas, encargos, cashback, integração com Agenda ou Orçamentos. Essas decisões ficam para as etapas posteriores.

## Compras e parcelas (T09.2)

Nota de atualização (23/10/2026): a [T09.1 de gestão de compras](T09.1-gestao-compras-cartoes.md)
acrescenta preview, edição protegida e cadastro em andamento. As rotas atuais usam
`/api/financial-workspaces/{workspaceId}`, não as rotas históricas de família descritas abaixo.
As limitações históricas de edição devem ser lidas junto dessa atualização; não houve nova carga no servidor.

Uma compra no cartão é uma obrigação de crédito, não um lançamento bancário. A API de compras exige cartão ativo, categoria de despesa da família, descrição, data, valor total positivo e de 1 a 120 parcelas. Compras à vista no crédito usam uma parcela; não equivalem a débito em conta.

```text
POST   /api/families/{familyId}/credit-cards/{cardId}/purchases
GET    /api/families/{familyId}/credit-cards/{cardId}/purchases?from=yyyy-MM-dd&through=yyyy-MM-dd
GET    /api/families/{familyId}/credit-cards/{cardId}/purchases/{purchaseId}
POST   /api/families/{familyId}/credit-cards/{cardId}/purchases/{purchaseId}/cancel
```

Os centavos são distribuídos deterministicamente: o valor é convertido em centavos decimais exatos, a divisão inteira define a base e o restante é acrescentado às primeiras parcelas. Assim, R$ 100,00 em três vezes resulta em R$ 33,34, R$ 33,33 e R$ 33,33; a soma permanece exatamente igual ao total.

O cancelamento é transacional, preserva a compra e marca todas as parcelas como `Cancelled`. Não é permitido editar silenciosamente valor ou parcelamento. Nenhuma compra altera saldo bancário, orçamento ou projeção.

## Ciclos e faturas (T09.3)

Ao criar a compra, cada parcela é vinculada atomicamente a uma fatura persistida. O ciclo é definido pelo dia de compra e pelo fechamento configurado no cartão: começa no dia seguinte ao fechamento anterior e termina no fechamento atual, inclusive; uma compra no próprio dia de fechamento pertence a esse ciclo. Dias de fechamento e vencimento inexistentes no mês são ajustados para o último dia do mês.

O vencimento é a primeira ocorrência estritamente posterior ao fechamento. Portanto, se o dia nominal de vencimento coincidir com o fechamento, ele será levado ao mês seguinte. A competência da fatura é o mês do vencimento. Alterações futuras na configuração do cartão não reclassificam parcelas já vinculadas.

As faturas persistidas são consultadas por competência, sem criação de faturas vazias. O status é derivado na leitura usando `America/Sao_Paulo`: `Open` enquanto hoje for menor ou igual ao fechamento e `Closed` depois dele. Não há pagamentos ou estados adicionais nesta etapa. Cancelar uma compra cancela suas parcelas e a composição da fatura passa a considerar somente parcelas ativas.

```text
GET /api/families/{familyId}/credit-cards/{cardId}/invoices?from=yyyy-MM&through=yyyy-MM
GET /api/families/{familyId}/credit-cards/{cardId}/invoices/{invoiceId}
```

Os valores de fatura são serializados como strings decimais exatas. As rotas exigem participação na família e retornam apenas faturas do cartão informado.

## Integração com saldo projetado (T09.5.1)

Faturas ainda devidas são aplicadas ao saldo projetado somente na data de vencimento, usando `remainingAmount`; faturas vencidas continuam incluídas uma única vez. Pagamentos efetivos já estão no saldo realizado e não são subtraídos novamente. O cartão só aloca a obrigação quando `DefaultPaymentAccountId` referencia uma conta da mesma família; sem essa referência, a obrigação permanece identificada como não alocada e não é atribuída arbitrariamente.

As projeções por conta, evolução diária e planejamento mensal reutilizam a mesma agregação de parcelas ativas menos pagamentos ativos. Não há conciliação heurística de previsões antigas: uma previsão comum só será deduplicada mediante vínculo explícito futuro com a fatura; descrição, valor ou data não são usados como identidade.

## Pagamentos de fatura (T09.4)

Um pagamento é uma liquidação da obrigação da fatura, não uma nova despesa de consumo. Cada pagamento cria exatamente um lançamento efetivo de saída na conta escolhida, com origem `CreditCardInvoicePayment`, e mantém vínculo explícito com esse lançamento. Como não possui categoria, ele não participa da execução de orçamentos nem duplica a compra do cartão.

Faturas fechadas podem receber pagamentos integrais ou parciais. O detalhe informa `totalAmount`, `paidAmount`, `remainingAmount` e `paymentStatus` (`Unpaid`, `PartiallyPaid` ou `Paid`). O saldo devido é sempre limitado a zero. Fatura aberta, fatura vazia, conta inexistente/inativa e valor acima do saldo devido são rejeitados; a conta preferencial do cartão é apenas sugestão para clientes futuros.

```text
GET  /api/families/{familyId}/credit-cards/{cardId}/invoices/{invoiceId}/payments
POST /api/families/{familyId}/credit-cards/{cardId}/invoices/{invoiceId}/payments
POST /api/families/{familyId}/credit-cards/{cardId}/invoices/{invoiceId}/payments/{paymentId}/cancel
```

Criação e cancelamento são transacionais. O cancelamento é lógico, preserva o histórico e cancela também o lançamento efetivo, restaurando o saldo bancário. A criação usa isolamento serializável e revalida o saldo dentro da transação, impedindo pagamentos concorrentes que ultrapassem o devido. O banco mantém unicidade entre pagamento e lançamento.

Uma compra não pode ser cancelada quando isso faria o total ativo de alguma fatura ficar abaixo do valor já pago. Não há juros, multas, estornos, reembolsos, rotativo ou pagamentos antecipados de parcelas nesta etapa.

## Projeção e limite (T09.5)

As consultas `GET /api/families/{familyId}/credit-cards/{cardId}/invoice-projection?from=yyyy-MM&months=1..12` e `GET /api/families/{familyId}/credit-cards/{cardId}/limit` agregam as parcelas e pagamentos persistidos. O limite utilizado é `obrigações ativas − pagamentos ativos`; o disponível é `limite nominal − utilizado` e `overLimitAmount` é o máximo entre zero e o excesso. Compras parceladas comprometem todas as parcelas ativas, inclusive faturas futuras.

No orçamento, cada parcela é reconhecida na competência da sua fatura como `creditCardCommitted`. Pagamentos não entram novamente no consumo e não alteram metas. Faturas sem conta de pagamento continuam visíveis, mas não são atribuídas arbitrariamente ao saldo de uma conta.
