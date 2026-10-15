# Cartões de crédito

## Conceito

`FinancialCreditCard` representa uma obrigação de crédito pertencente a uma família. Ele não é uma conta financeira: uma compra futura não movimenta imediatamente o saldo bancário; o pagamento de uma fatura será tratado em etapa posterior.

Esta etapa cobre somente cadastro, consulta, edição e arquivamento lógico. Não cria compras, parcelas, faturas, pagamentos, previsões ou transferências.

## Cadastro

Cada cartão possui instituição emissora, nome identificador, titular opcional, limite nominal, dia de fechamento, dia de vencimento e conta financeira preferencial de pagamento opcional. O titular precisa ser usuário membro da mesma família. A conta de pagamento precisa pertencer à família e estar ativa no momento do cadastro; desativar uma conta depois não remove o histórico do cartão.

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
