# T16.1 — Diagnóstico e correção do schema OpenAI

## Escopo e diagnóstico

**Correção concluída localmente em 24/10/2026**, sem chamadas reais à OpenAI. Não foram lidos nem armazenados neste relatório
PDFs, respostas brutas do provider, senhas, CPF ou tokens. O modelo permanece `gpt-4o-mini`.

Incompatibilidades comprovadas pela inspeção:

1. O prompt solicitava `header: null` para documento sem evidência, mas o schema HTTP exigia um objeto
   e o backend recusava header nulo. Agora o prompt pede objeto com campos nulos e itens vazios.
   Essa representação não se torna uma fatura válida: o evaluator continua rejeitando ausência de evidência.
2. O schema permitia strings arbitrárias para dinheiro, datas e últimos dígitos, além de não declarar
   limites de texto/quantidade já exigidos em C#. Agora declara padrões monetários canônicos, datas,
   competência, quatro dígitos, limites textuais, mínimo de páginas/parcelas e máximo de 2000 itens.
3. O validador dependia de valores padrão de desserialização para alguns campos ausentes. Agora verifica
   presença dos campos básicos antes de desserializar, inclusive o booleano `includedInCurrentTotal`.
   Também rejeita chaves duplicadas e propriedades inesperadas, sem ecoar seus nomes.
4. A mensagem única escondia se a rejeição vinha da estrutura ou das regras de domínio do contrato.
   Agora retorna código específico e caminho fixo permitido.

Não foi possível determinar retrospectivamente qual campo causou a rejeição da chamada real relatada:
não há resposta bruta inspecionada nem diagnóstico granular anterior. Esta entrega corrige divergências
reproduzidas e permite identificar a próxima rejeição sem registrar o conteúdo sensível. Não comprova
sucesso de uma nova extração real.

## Contrato final

O provider usa Responses API, `strict=true`, `additionalProperties=false`, campos obrigatórios e união
com `null` para fatos opcionais, conforme a [documentação oficial OpenAI de Structured Outputs](https://developers.openai.com/api/docs/guides/structured-outputs).
A skill OpenAI Docs orientou essa conferência. Não houve troca de SDK, modelo ou integração.

O provider solicita versão `2.0`; C# continua aceitando também `1.0` para o adaptador tradicional e
compatibilidade existente. Os quatro campos de categorização são obrigatórios na versão 2.0, mas seus
valores podem ser nulos. Os campos adicionais de identidade podem ser omitidos nos DTOs internos antigos;
no schema OpenAI são obrigatórios e anuláveis, sem exigir evidência inexistente.

Restrições dependentes do documento continuam exclusivamente validadas em C#: calendário real,
limite da página pelo PDF, correspondência dos números de parcela, citações literais, identidade do
cartão, total oficial e conciliação. Structured Outputs não substitui esses guards.

## Diagnóstico seguro

`TryValidate` mantém a assinatura existente; `error` agora contém `Código:Caminho`:

| Código | Causa |
| --- | --- |
| SchemaInvalidJson | JSON sintaticamente inválido |
| SchemaIncompatibleType | Estrutura raiz/header/itens ou versão com tipo incompatível |
| SchemaDeserializationFailed | Tipos incompatíveis com os DTOs C# |
| SchemaVersionInvalid | Versão não suportada |
| SchemaRequiredFieldMissing | Campo obrigatório ausente |
| SchemaUnexpectedProperty / SchemaDuplicateProperty | Propriedade extra ou repetida |
| SchemaInvalidEnum | Natureza, tipo, seção ou confiança fora do contrato |
| SchemaInvalidMoney / SchemaInvalidDate | Dinheiro canônico ou data/competência inválida |
| SchemaInvalidIdentity / SchemaInvalidEvidence | Identificação, evidência ou página inválida |
| SchemaInvalidText / SchemaInvalidInstallment / SchemaItemLimitExceeded | Limites e cronograma inválidos |

Exemplo seguro: `SchemaInvalidMoney:$.items[].amount`. Os caminhos vêm de constantes/nomes permitidos,
nunca de `JsonException.Path`, mensagens de exceção ou valores do provider. O diagnóstico propaga pelo
`AgentError` existente até o relatório controlado; não cria endpoint nem grava resposta bruta.

Falha de schema mantém o agente rejeitado e a interpretação principal nula. O parser continua somente
`parserShadow` no modo AgentPrimary, nunca substitui silenciosamente o agente. Divergência financeira
continua `NeedsReview` e evidência inventada continua bloqueada. Não houve alteração dos cálculos,
normalização financeira, pagamentos ou regras de conciliação.

## Arquivos

- `CreditCardStatementAgentDtos.cs`: prompt versionado e validação granular/estrutural.
- `OpenAiCreditCardStatementAgentProvider.cs`: restrições declaradas no schema enviado.
- `CreditCardStatementAgentSchemaTests.cs`: casos válidos, inválidos, nulos e diagnósticos seguros.
- `OpenAiCreditCardStatementAgentProviderTests.cs`: HTTP simulado, paridade entre propriedades dos DTOs,
  required/schema, enums e resposta válida aceita pelo backend.
- `CreditCardStatementExtractionOrchestratorTests.cs`: código granular preservado sem fallback para parser.

## Verificação e limites

- Backend dirigido: **123/123**, zero falhas e zero ignorados, Release, em **1 min 57 s**.
  Artefato local: `.artifacts/t16-schema/t16-schema-directed.trx`.
- Builds Release da API e `Saldo360.StatementDiagnostic`: **zero avisos e zero erros**.
- `git diff --check` aprovado; somente avisos de conversão LF/CRLF do Git no Windows.
- Sem regressão completa de backend ou testes frontend: nenhum componente frontend foi alterado.

Comando de teste executado:

```powershell
dotnet test tests/Saldo360.Tests/Saldo360.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~CreditCardStatementAgentSchemaTests|FullyQualifiedName~OpenAiCreditCardStatementAgentProviderTests|FullyQualifiedName~CreditCardStatementAgentPreviewTests|FullyQualifiedName~CreditCardStatementExtractionOrchestratorTests|FullyQualifiedName~CreditCardStatementAgentShadow|FullyQualifiedName~StatementCategorizationTests|FullyQualifiedName~InboxStatementAnalysisTests|FullyQualifiedName~OfficialStatementConsolidationTests" --logger "trx;LogFileName=t16-schema-directed.trx" --results-directory .artifacts/t16-schema
```

Todos os documentos e respostas usados nos testes são sintéticos; o transporte HTTP do provider é simulado.
Não houve mudança de frontend, autenticação, desbloqueio por CPF/T18, infraestrutura, flags, dados
financeiros ou migrations. Sem push, deploy ou habilitação do agente em produção.
