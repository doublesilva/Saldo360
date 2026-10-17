# Pré-visualização de CSV de cartões (T10.3.1)

## Escopo

O endpoint `POST /api/families/{familyId}/credit-cards/{cardId}/imports/preview` recebe um CSV e devolve uma prévia normalizada. Nenhuma compra, parcela, fatura, lançamento, categoria ou saldo é gravado. A importação definitiva fica para a T10.3.2.

O usuário precisa participar da família e o cartão precisa pertencer à família e estar ativo. O arquivo não é persistido nem seu conteúdo financeiro completo é registrado em logs.

## Formato e mapeamento

O upload usa `multipart/form-data`, com o campo `file` e os campos opcionais:

`dateColumn`, `descriptionColumn`, `amountColumn`, `categoryColumn`, `installmentNumberColumn`, `installmentCountColumn`, `externalIdColumn`, `competenceColumn`, `delimiter`, `dateFormat` e `decimalFormat`.

As colunas `Data`, `Descrição` e `Valor` são usadas por padrão, com aliases genéricos `Date`, `Description` e `Amount`. O separador pode ser `,` ou `;`; sem configuração, é detectado pelo cabeçalho. O parser suporta aspas, separadores dentro de campos e quebras de linha em campos entre aspas.

Exemplo:

```csv
date;description;amount;external
20/10/2026;"Compra; loja";1.234,56;ext-1
```

## Interface de importações (T10.3.5)

Em **Cartões > Importações**, selecione um cartão ativo e escolha CSV ou PDF. CSV permite configurar colunas, separador, formato de data e padrão decimal; a prévia mostra situação, erros, avisos, parcelas e competência. Apenas linhas elegíveis podem ser selecionadas, e cada compra exige categoria de despesa ativa antes da confirmação.

Parcelas intermediárias não são selecionáveis como compras: a interface consulta candidatas e exige associação explícita ou oferece o registro histórico unitário. O `importId` é mantido durante a confirmação e não é recriado por cliques duplicados enquanto a requisição está em andamento.

PDF é exclusivamente análise. A tela apresenta metadados, página, tipo, origem e avisos, mas não oferece importação definitiva nem envia registros PDF ao endpoint CSV. Após confirmações CSV ou registros históricos, os módulos financeiros são invalidados; a prévia não invalida dados.

Para este arquivo, informe `delimiter=;`, `dateFormat=dd/MM/yyyy`, `decimalFormat=PtBr` e `externalIdColumn=external`.

## Datas e valores

Datas aceitas são `yyyy-MM-dd` e `dd/MM/yyyy`, ou o formato explicitamente informado. Valores preservam centavos em `decimal` e são devolvidos como strings, por exemplo `1234.56`. São aceitos padrões invariantes e `PtBr`, incluindo `R$ 1.234,56` e sinais negativos.

O modo `Auto` rejeita formas potencialmente ambíguas como `1.234` ou `1,234`; nesse caso, informe `decimalFormat`. Valores negativos recebem aviso e não são classificados automaticamente como compras ou estornos.

## Resposta

```json
{
  "totalRows": 1,
  "validRows": 1,
  "warningRows": 0,
  "invalidRows": 0,
  "ignoredRows": 0,
  "mapping": { "dateColumn": "date", "descriptionColumn": "description", "amountColumn": "amount", "delimiter": ";", "decimalFormat": "PtBr" },
  "rows": [{ "rowNumber": 2, "transactionDate": "2026-10-20", "description": "Compra; loja", "amount": "1234.56", "externalId": "ext-1", "status": "Valid", "errors": [], "warnings": [] }]
}
```

## Parcelas intermediárias e histórico (T10.3.3)

Linhas como `2/6` não são transformadas automaticamente em uma nova compra. Há dois fluxos explícitos: `POST .../imports/installments/matches` procura parcelas normais compatíveis; `POST .../imports/installments/link` exige `installmentId` e `confirm: true` para vincular a linha; `POST .../imports/installments/historical` cria uma única obrigação histórica, sem compra nem parcelas futuras. A competência `yyyy-MM` é obrigatória e deve corresponder a uma fatura existente.

`GET .../imports/installments/` lista o histórico e `DELETE .../imports/installments/{id}` cancela logicamente uma obrigação histórica ainda não consumida por pagamento. A identidade `(família, cartão, importId, rowNumber)` é idempotente; conteúdo diferente entra em conflito. `externalId` possui unicidade entre registros ativos do cartão. Não há deduplicação heurística entre operações distintas.

Obrigações históricas compõem fatura, limite, pagamentos e projeções, mas não criam lançamento bancário. Com categoria de despesa válida participam do orçamento; sem categoria não são atribuídas arbitrariamente. Não é permitido reconstruir parcelas anteriores/futuras.

## Pré-visualização de faturas PDF (T10.3.4)

`POST /api/families/{familyId}/credit-cards/{cardId}/imports/pdf/preview` recebe `multipart/form-data` com `file` e, opcionalmente, `issuerProfile`, `competence`, `dateFormat` e `password`. A senha manual existe apenas durante a requisição, não é persistida nem devolvida. O limite inicial é 10 MB e 50 páginas. O cartão deve pertencer à família, estar ativo e o solicitante deve participar dela.

São aceitos PDFs que possuam camada textual. O parser genérico preserva página e linha de origem, reconhece datas ISO/brasileiras, valores com padrão brasileiro, competência, vencimento, total e identificadores parciais do cartão quando houver evidência textual. Linhas com `02/06` são classificadas como `Installment`; pagamentos, créditos, estornos e encargos nunca são convertidos automaticamente em compras. Linhas ambíguas ficam como `Unknown`/`Warning`.

A abertura sem senha ocorre primeiro. Somente quando o PdfPig identifica proteção por senha o serviço consulta o material autorizado do titular configurado no cartão. São suportadas as estratégias `First5` e `First3`; a estratégia da instituição é tentada primeiro e, sem configuração, a ordem determinística é `First5`, `First3`, sem força bruta. O conteúdo é rebobinado entre tentativas. O prefixo, o CPF e as senhas derivadas não são gravados, registrados em log ou incluídos na resposta.

O provedor atual é um mock exclusivo de `Development` e `Testing`. Ele precisa ser habilitado e receber prefixos sintéticos por configuração externa ao Git:

```text
CreditCardPdfPasswordMock__Enabled=true
CreditCardPdfPasswordMock__Identities__{holderUserId}__First5=<prefixo-sintetico>
CreditCardPdfPasswordMock__IssuerStrategies__{instituicao}=First5
```

Em `Production`, o mock sempre responde como indisponível, mesmo que essas chaves sejam fornecidas. A integração real com atributos autorizados do Authelia continua pendente.

A resposta contém `statement`, `summary`, `items`, `pageCount`, o parser utilizado e `security.status`, com `Unprotected` ou `Unlocked`. A operação é somente leitura: o PDF não é armazenado, nenhuma compra/fatura/pagamento é criado e nenhum endpoint de confirmação é chamado. As falhas distinguem material de identidade indisponível, senha não resolvida, criptografia não suportada, corrupção e ausência de texto sem expor segredos. OCR, perfis de instituições, integração real com o Authelia, conciliação e importação definitiva ficam para etapas posteriores.

### Parser Itaú Mastercard (T15.3.2)

O parser `ItauMastercard` foi construído a partir de uma fatura Mastercard Black real, analisada somente no ambiente local e não incorporada ao repositório. O reconhecimento exige marcadores estruturais do Itaú e utiliza palavras com coordenadas para separar as duas colunas. O parser genérico permanece como fallback e sempre devolve `reconciliation.status = NeedsReview`, acompanhado de aviso de que a composição não foi validada.

O layout especializado reconhece:

- titular, quatro últimos dígitos do cartão, competência, emissão/fechamento, vencimento e total oficial;
- saldo da fatura anterior, pagamento e saldo financiado;
- compras nacionais, créditos, compras parceladas e parcelas de produtos/serviços;
- transação internacional e seu repasse de IOF;
- juros, mora, multa, IOF e demais encargos exibidos na seção da fatura;
- parcelas de próximas faturas, preservadas como `FutureInstallment` e marcadas com `includedInCurrentTotal = false`.

Cada item preserva `sourceText`, página, descrição original, descrição normalizada, natureza (`Debit`, `Credit`, `Informational` ou `Unknown`), valor decimal exato, parcela e participação no total atual. Parcelas futuras não são multiplicadas nem tratadas como compras completas.

A conferência segue a composição oficial apresentada no documento:

```text
saldo anterior + pagamentos/créditos + lançamentos atuais detalhados + encargos = total oficial
```

O objeto `reconciliation` retorna `officialTotal`, `componentsTotal`, `difference`, `hasUnknownCurrentItems` e um estado:

- `Reconciled`: diferença exatamente zero e nenhuma linha financeira corrente desconhecida;
- `Divergent`: os componentes reconhecidos não fecham o total;
- `NeedsReview`: total insuficiente ou linha corrente potencialmente financeira não reconhecida.

Na prova local, a amostra real foi classificada como `Reconciled`, com diferença `0.00`. Os testes versionados usam somente uma representação posicional sintética e anonimizada do layout.

### Pré-conciliação PDF (T15.3.3)

`POST /api/families/{familyId}/credit-cards/{cardId}/imports/pdf/pre-reconcile` aceita o mesmo `multipart/form-data` da prévia e executa uma comparação somente leitura com o cartão explicitamente selecionado. O endpoint exige autenticação, participação na família, cartão ativo e CSRF. O conteúdo do PDF, a senha e o nome do titular não são persistidos nem devolvidos pelo relatório.

O relatório mantém `reconciliation` como a conferência interna do documento e usa uma classificação independente para cada componente: `Create`, `AlreadyExists`, `NeedsReview`, `Conflict`, `Unsupported` ou `Informational`. Saldo anterior, saldo financiado, pagamentos e parcelas futuras são informativos. Encargos, créditos e estornos são reconhecidos como ajustes de fatura suportados pelo domínio, mas ainda não podem ser confirmados a partir do PDF; eles nunca são convertidos em compras positivas. Uma parcela intermediária isolada permanece em revisão e não gera a compra original nem parcelas futuras.

A identidade do documento é `pdf-sha256:{hash}`, calculada sobre os bytes integrais, e a identidade de componente é `pdf:{hash}:p{page}:r{row}`. Nesta etapa essas chaves são apenas apresentadas em memória. Se uma identidade externa igual já estiver nos registros de importação CSV ou de parcela histórica, conteúdo idêntico resulta em `AlreadyExists` e conteúdo divergente em `Conflict`. Comparações por data, valor, descrição, parcela ou competência produzem somente candidatos `NeedsReview`; não constituem deduplicação.

A validação de destino confronta família, cartão, titular configurado, competência, fechamento e vencimento. O cadastro atual de cartão não guarda os últimos dígitos, portanto o número mascarado sempre é explicitado como `NeedsReview`; não se associa fatura por emissor ou coincidência de total. Compras integrais suportadas, sem identidade nem candidata e em ciclo não conflitante, podem ser marcadas `Create`. O impacto estimado é separado como débito, crédito e líquido na categoria `Uncategorized`: nenhuma categoria, conta ou compra original é inventada.

A leitura do banco ocorre em snapshot `REPEATABLE READ`, em consultas em lote sobre compras/parcelas, importações, obrigações históricas, ajustes, faturas e pagamentos. Nenhuma entidade é adicionada ou modificada; a importação definitiva de PDF continua fora do escopo.

### Ajustes financeiros de fatura (T15.3.4)

`FinancialCreditCardInvoiceAdjustment` representa componentes que alteram a fatura sem constituir compra ou pagamento. Os tipos iniciais são `Fee`, `Credit` e `Refund`. O valor persistido é sempre positivo; `Debit` soma ao total e `Credit` subtrai. A combinação é explícita e validada: taxas são débito, enquanto crédito e reembolso são crédito.

O total passa a ser:

```text
parcelas ativas + parcelas históricas ativas + ajustes Debit ativos - ajustes Credit ativos
```

O total bruto não é limitado a zero. Apenas o saldo devido continua como `max(0, total - pagamentos ativos)`. Ajustes integram o limite utilizado e as projeções de fatura/conta pela mesma composição, mas não criam `FinancialTransaction` e não são pagamentos.

`ExternalId`, como `pdf:{documentHash}:p{page}:r{row}`, é único permanentemente por família e cartão. Reenvio com conteúdo idêntico é idempotente; conteúdo divergente é conflito. O cancelamento é lógico e mantém a identidade. Um débito não pode ser cancelado se o novo total ficar abaixo do valor já pago.

`CategoryId` é opcional e, quando informado, deve apontar para categoria ativa da mesma família. Ajustes não entram no orçamento nesta etapa, mesmo categorizados: a semântica de estorno/crédito por competência exige decisão funcional própria e não foi inferida. Não foi criado endpoint público de criação manual; ajustes são persistidos somente pelo fluxo confirmado de importação PDF descrito a seguir.

### Confirmação definitiva de PDF (T15.3.5)

`POST /api/families/{familyId}/credit-cards/{cardId}/imports/pdf/confirm` recebe `multipart/form-data` com `file`, `confirm=true`, `documentId`, `categories` em JSON e os mesmos campos opcionais da prévia, inclusive `password`. `categories` contém somente pares `componentId`/`categoryId`; valores, descrições, datas, sinais e parcelas são sempre reconstruídos pelo backend a partir do PDF reenviado.

O backend recalcula o SHA-256, desbloqueia e interpreta novamente o documento, exige o parser especializado `ItauMastercard`, `reconciliation.status = Reconciled`, fatura de destino validada e igualdade com o `documentId` revisado. Componentes financeiros `NeedsReview`, `Conflict` ou `Unsupported` bloqueiam o lote. `Informational` é apenas registrado na rastreabilidade; pagamentos exibidos pelo emissor nunca criam pagamento ou transação bancária.

Compras integrais `Create` exigem categoria de despesa ativa da mesma família e reutilizam `FinancialCreditCardPurchase`, distribuição e calendário existentes. Parcelas intermediárias continuam bloqueadas enquanto não houver decisão explícita pelo fluxo próprio. `Fee`, `Credit` e `Refund` criam `FinancialCreditCardInvoiceAdjustment`, com identidade `pdf:{hash}:p{page}:r{row}`. `AlreadyExists` não cria nova entidade.

A infraestrutura existente de `FinancialCreditCardImportBatch` foi ampliada com origem `Csv`/`Pdf`, hash do documento, parser, competência, hash das decisões e total oficial. Os itens registram componente, resultado e entidade vinculada. O lote inteiro usa transação `SERIALIZABLE`; após gravar, o backend recalcula o saldo devido persistido e exige diferença `0.00` contra o total oficial antes do commit.

O mesmo documento e decisões retorna o resultado anterior. Decisões diferentes retornam conflito. O PDF, senha, CPF e nome completo do titular não são armazenados. SHA-256 identifica bytes exatos: uma fatura regenerada pelo emissor pode ter outro hash e continuará exigindo nova pré-conciliação; não existe deduplicação heurística por emissor, competência ou valor.

### Fluxo da interface PDF

Em **Cartões > Importações**, selecione PDF e siga as três ações explícitas: analisar o documento, comparar seus componentes com o Saldo360 e confirmar a importação. A tela mantém separados os componentes da fatura atual e as parcelas futuras/informativas, mostra as seis classificações da pré-conciliação e exige categoria de despesa apenas para compras `Create`.

A confirmação fica bloqueada quando a conferência interna não é `Reconciled`, quando há componente financeiro atual `NeedsReview`, `Conflict` ou `Unsupported`, ou quando falta categoria de uma compra nova. O navegador reenvia o arquivo original, `documentId` e somente as decisões `componentId`/`categoryId`; valores, descrições e datas não são enviados como decisões do cliente.

A senha opcional existe apenas no estado em memória da tela e é apagada ao trocar o arquivo, abandonar a análise ou concluir a confirmação. Um `409 Conflict` preserva a análise visível e oferece reanálise explícita. Reenvios idempotentes são apresentados como documento já importado, sem duplicidade.

`Valid`, `Warning` e `Invalid` descrevem somente a prévia. Não significam compras importadas.

## Limites e segurança

O limite inicial é de 5 MB e 10.000 linhas de dados. Extensão diferente de `.csv`, conteúdo vazio, UTF-8 inválido, CSV malformado, mapeamento inexistente e colunas obrigatórias ausentes são rejeitados. Linhas incompletas ou datas/valores inválidos aparecem individualmente com erro.

O endpoint exige autenticação, participação na família, cartão ativo e CSRF. O conteúdo é tratado como texto não confiável; não é executado e não cria entidades financeiras.

## Limitações e evolução

Não há reconhecimento automático de banco, importação definitiva, deduplicação, estornos, pagamentos, categorias ou PDFs. A T10.3.2 poderá reutilizar o DTO normalizado após revisão e confirmação, mantendo uma etapa explícita antes de persistir qualquer obrigação.

## Confirmação definitiva (T10.3.2)

A confirmação é feita por `POST /api/families/{familyId}/credit-cards/{cardId}/imports/confirm` e exige `confirm: true`, um `importId` e a lista explícita de linhas selecionadas. Cada item informa data ISO, descrição, valor, categoria de despesa e, opcionalmente, total original, parcelas e identificador externo.

O lote é atômico: qualquer erro rejeita todos os itens e nenhuma compra é gravada. Compras aprovadas reutilizam `FinancialCreditCardPurchase.Create`, a distribuição de centavos e o calendário de faturas existentes. Nenhum lançamento bancário é criado.

O `importId` e um hash do conteúdo formam a chave de idempotência. Reenvio com o mesmo conteúdo devolve os mesmos `purchaseId`s; o mesmo identificador com conteúdo diferente retorna `409 Conflict`. A operação é protegida por transação serializável e as tabelas `financial_credit_card_import_batches` e `financial_credit_card_import_items` preservam a rastreabilidade. Identificadores externos únicos por família e cartão impedem repetição forte; sem identificador externo, operações independentes não são comparadas.

Parcelas intermediárias isoladas, como `2/6`, são rejeitadas. Um parcelamento só é aceito quando a primeira parcela e o total original estiverem explicitamente informados; o sistema não multiplica o valor da parcela nem inventa parcelas.

Exemplo de confirmação:

```json
{
  "importId": "00000000-0000-0000-0000-000000000001",
  "confirm": true,
  "items": [{
    "rowNumber": 2,
    "transactionDate": "2026-10-20",
    "description": "Compra Loja X",
    "amount": "123.45",
    "categoryId": "00000000-0000-0000-0000-000000000002",
    "installmentNumber": 1,
    "installmentCount": 1,
    "totalAmount": "123.45",
    "externalId": "statement-1"
  }]
}
```
