# Extração de faturas por agente

A rota experimental e somente leitura `POST /api/families/{familyId}/credit-cards/{cardId}/imports/pdf/agent-preview` recebe um PDF em `multipart/form-data`. Ela preserva autorização familiar, CSRF, limites de arquivo, desbloqueio efêmero e ausência de persistência do preview já adotados pelo fluxo PDF.

## Contrato normalizado

O agente devolve JSON com `schemaVersion: "1.0"`, `header` e `items`. O cabeçalho contém emissor, final do cartão, competência, fechamento, vencimento e total oficial. Cada item contém descrição original, valor decimal positivo com duas casas, natureza `Debit` ou `Credit`, tipo financeiro, parcela opcional, seção semântica, participação no total atual, página, evidência curta e confiança `High`, `Medium` ou `Low`.

A resposta é dado não confiável. O backend rejeita campos desconhecidos, enums fora do contrato, dinheiro não canônico, datas inválidas, páginas inexistentes, evidência excessiva e parcelas inconsistentes. Confiança baixa é normalizada para `Unknown`. `Payment` e as seções `Payments` e `FutureInstallments` nunca participam do total atual, mesmo que a resposta solicite isso.

## Reconciliação determinística

O C# calcula `componentsTotal` apenas com `includedInCurrentTotal=true`: débitos somam e créditos subtraem. `difference = officialTotal - componentsTotal`. O status é `Reconciled` somente com diferença `0.00`, total oficial válido e nenhuma linha corrente desconhecida; caso contrário é `Divergent` ou `NeedsReview`.

Parcelas intermediárias permanecem parcelas pelo valor informado. Pagamentos são informativos e parcelas futuras não alteram a composição atual. O agente não cria compras, ajustes, pagamentos, previsões ou qualquer outra entidade financeira.

## Implementação inicial e limitações

`ICreditCardStatementExtractorAgent` isola o mecanismo semântico. Como o projeto ainda não possui provedor/modelo de IA configurado, a implementação inicial adapta os parsers locais validados para o contrato do agente. Itaú Mastercard e Nubank permanecem inalterados, e a rota histórica continua usando seu comportamento original. A fronteira permite substituir o adaptador por um agente externo sem deslocar validação financeira, autorização ou reconciliação para o modelo.
