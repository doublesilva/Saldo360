# Conciliação explícita de previsões e faturas

## Objetivo

A conciliação associa explicitamente uma previsão de despesa existente à obrigação de uma fatura de cartão. Ela não cancela, edita ou realiza a previsão e não cria pagamento. Enquanto o vínculo ativo existir, a fatura é a única fonte da obrigação no saldo projetado.

## Elegibilidade e cardinalidade

Podem ser vinculadas somente previsões pendentes, não canceladas, de despesa, sem lançamento efetivo, pertencentes à mesma família e com conta igual à conta preferencial do cartão. A fatura deve pertencer à família, possuir parcelas ativas e saldo devido positivo. Uma previsão e uma fatura podem ter no máximo um vínculo ativo.

O banco mantém o histórico de vínculos removidos na tabela `financial_planned_invoice_reconciliations`. Índices parciais únicos (`removed_at IS NULL`) impedem duplicidade ativa para cada lado. Todas as FKs usam a família como parte da chave e são `RESTRICT`.

## API

```text
POST   /api/families/{familyId}/invoice-reconciliations
GET    /api/families/{familyId}/invoice-reconciliations
GET    /api/families/{familyId}/invoice-reconciliations?invoiceId={id}
GET    /api/families/{familyId}/invoice-reconciliations?plannedTransactionId={id}
DELETE /api/families/{familyId}/invoice-reconciliations/{id}
```

O POST recebe `plannedTransactionId` e `creditCardInvoiceId`. O GET inclui vínculos removidos para rastreabilidade, com `removedAt` preenchido. Criação e remoção exigem CSRF e participação na família; conflitos de unicidade retornam `409`.

## Projeções e pagamentos

Sem vínculo, previsão e fatura mantêm o comportamento independente existente. Com vínculo, a previsão é excluída dos agregados de saldo, Agenda, evolução e execução orçamentária, enquanto a obrigação da fatura continua sendo calculada por parcelas ativas menos pagamentos ativos. Pagamento parcial reduz somente o saldo da fatura; quitação remove a obrigação sem cancelar a previsão original. Ao desfazer o vínculo, a previsão volta a participar normalmente.

O saldo realizado nunca é alterado pela conciliação. O pagamento continua criando o lançamento efetivo próprio da origem `CreditCardInvoicePayment`, uma única vez. O comprometimento orçamentário da parcela não é removido por pagamento.

## Ciclo de vida

Enquanto houver vínculo ativo, a previsão não pode ser editada, cancelada, realizada nem ter sua conta alterada. A conta preferencial do cartão também não pode ser trocada enquanto houver conciliações ativas, evitando deslocar silenciosamente a obrigação para outra conta. Nenhuma operação remove o vínculo implicitamente; a remoção exige DELETE explícito.

Previsões legadas não são comparadas por descrição, valor ou data. A aplicação não cancela previsões automaticamente nem cria vínculos por semelhança. A conciliação futura deve ser iniciada pelo usuário e auditada.

## Validação

Os cenários PostgreSQL da T10.1 cobrem vínculo, consulta, desvinculação, incompatibilidade de conta, isolamento familiar, projeção antes/depois e preservação da previsão. A migration incremental é `AddPlannedInvoiceReconciliations`.

## Interface React (T10.2)

Na seção **Cartões**, a aba **Conciliações** consulta vínculos ativos e histórico. O fluxo exige selecionar uma previsão de despesa pendente, uma fatura com saldo devido e conta compatível, revisar os dados e confirmar o POST.

Valores previstos e de fatura são exibidos separadamente como textos decimais. A interface não calcula diferenças, não altera contas e não aplica ajustes. O botão **Desfazer vínculo** pede confirmação e chama o DELETE; depois disso, as consultas financeiras da família são invalidadas.

Como o endpoint atual retorna apenas IDs, os detalhes são compostos pelos clientes existentes de previsões, faturas, cartões e contas. A Agenda não recebe marcador adicional nesta etapa, mas a previsão conciliada já não aparece como obrigação pendente duplicada.

