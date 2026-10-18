# Extração de faturas por agente

A rota experimental e somente leitura `POST /api/families/{familyId}/credit-cards/{cardId}/imports/pdf/agent-preview` recebe um PDF em `multipart/form-data`. Ela preserva autorização familiar, CSRF, limites de arquivo, desbloqueio efêmero e ausência de persistência do preview já adotados pelo fluxo PDF.

## Contrato normalizado

O agente devolve JSON com `schemaVersion: "1.0"`, `header` e `items`. O cabeçalho contém emissor, final do cartão, competência, fechamento, vencimento e total oficial. Cada item contém descrição original, valor decimal positivo com duas casas, natureza `Debit` ou `Credit`, tipo financeiro, parcela opcional, seção semântica, participação no total atual, página, evidência curta e confiança `High`, `Medium` ou `Low`.

A resposta é dado não confiável. O backend rejeita campos desconhecidos, enums fora do contrato, dinheiro não canônico, datas inválidas, páginas inexistentes, evidência excessiva e parcelas inconsistentes. Confiança baixa é normalizada para `Unknown`. `Payment` e as seções `Payments` e `FutureInstallments` nunca participam do total atual, mesmo que a resposta solicite isso.

## Reconciliação determinística

O C# calcula `componentsTotal` apenas com `includedInCurrentTotal=true`: débitos somam e créditos subtraem. `difference = officialTotal - componentsTotal`. O status é `Reconciled` somente com diferença `0.00`, total oficial válido e nenhuma linha corrente desconhecida; caso contrário é `Divergent` ou `NeedsReview`.

Parcelas intermediárias permanecem parcelas pelo valor informado. Pagamentos são informativos e parcelas futuras não alteram a composição atual. O agente não cria compras, ajustes, pagamentos, previsões ou qualquer outra entidade financeira.

## Provider real e modo shadow

`ICreditCardStatementExtractorAgent` isola o mecanismo semântico e `ICreditCardStatementAgentProvider` isola o fornecedor. A implementação real usa a OpenAI Responses API via HTTP e Structured Outputs com JSON Schema estrito; nenhum SDK específico alcança a camada Application. O prompt versionado em `CreditCardStatementAgentPrompt` proíbe invenção de dados, associação a cadastros, multiplicação de parcelas intermediárias e inclusão de pagamentos ou parcelas futuras no total corrente.

O provider é desabilitado por padrão. A configuração `CreditCardStatementAgent` aceita `Enabled`, `Provider`, `Model`, `Endpoint`, `ApiKeyFile` e `TimeoutSeconds`. Em produção, a chave deve vir de arquivo secreto; indisponibilidade do provider não participa do readiness nem afeta os endpoints financeiros existentes.

`POST /api/families/{familyId}/credit-cards/{cardId}/imports/pdf/agent-shadow-preview` executa em paralelo lógico o adaptador dos parsers e o agente real sobre o mesmo texto extraído. Retorna ambos os previews e diferenças exatas de cabeçalho, total oficial, totais reconstruídos, reconciliação, itens, classificação e `includedInCurrentTotal`. Não existe seleção automática de vencedor nem correspondência por similaridade textual.

Itaú Mastercard e Nubank permanecem inalterados, e a rota histórica continua usando seu comportamento original. O agente recebe somente texto paginado e competência opcional; IDs de família/cartão, saldos, orçamento e histórico financeiro não são enviados.

## Limitações

- A versão atual envia texto extraído, não imagens das páginas; PDFs sem camada textual falham com segurança.
- A comparação usa descrição normalizada e fatos financeiros exatos. O contrato T15.4.1 não possui data por item, portanto esse campo ainda não participa da chave comparativa.
- O provider não foi habilitado nem chamado com documentos reais nesta entrega porque nenhuma credencial foi fornecida. As amostras reais permaneceram fora do repositório.
