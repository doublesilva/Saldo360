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
