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

`Valid`, `Warning` e `Invalid` descrevem somente a prévia. Não significam compras importadas.

## Limites e segurança

O limite inicial é de 5 MB e 10.000 linhas de dados. Extensão diferente de `.csv`, conteúdo vazio, UTF-8 inválido, CSV malformado, mapeamento inexistente e colunas obrigatórias ausentes são rejeitados. Linhas incompletas ou datas/valores inválidos aparecem individualmente com erro.

O endpoint exige autenticação, participação na família, cartão ativo e CSRF. O conteúdo é tratado como texto não confiável; não é executado e não cria entidades financeiras.

## Limitações e evolução

Não há reconhecimento automático de banco, importação definitiva, deduplicação, estornos, pagamentos, categorias ou PDFs. A T10.3.2 poderá reutilizar o DTO normalizado após revisão e confirmação, mantendo uma etapa explícita antes de persistir qualquer obrigação.
