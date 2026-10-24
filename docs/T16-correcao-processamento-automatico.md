# T16 — Integração do processamento automático por agente

24/10/2026 — **Correção da integração T16.1 → T16.2 → T16.3 concluída localmente**, com testes e builds finais aprovados.

Esta decisão substitui, **na busca explícita de fatura pelo proprietário**, a parada após recepção
descrita na T16.1. Não habilita coleta agendada, flags, pagamentos ou outras automações globais.

## Fluxo e proteções

A busca continua determinística pela query cadastrada, com paginação limitada, ordenação por
internalDate, limite de anexos/tamanho, SHA-256 e identidade Gmail existentes. Não envia mensagens
ao agente para decidir a pesquisa. PDFs ambíguos na mesma mensagem exigem revisão.

Após receber/reutilizar o documento, `CardLatestEmailService` chama `InboxStatementProcessingService`.
Este coordenador reutiliza `InboxStatementAnalysisService` e `InboxStatementConsolidationService`;
não possui writer nem outro motor financeiro. `AlreadyReceived` também passa pelo coordenador.
Documento consolidado é verificado pelo journal existente, sem nova extração ou escrita financeira.

No processamento automático, o orquestrador usa o agente como fonte primária, desde que configurado
e habilitado. Não altera configuração nem habilita um provider desabilitado silenciosamente.
O parser continua como comparação/âncora financeira. Falta de identificação determinística não
impede a interpretação pelo agente. Contradição entre dígitos documentais impede consolidação,
mas só é avaliada depois da interpretação do agente.

O header estruturado acrescenta produto, nome do titular e citação literal/página de identificação.
Esses campos são opcionais para leitura de análises antigas; a resposta do provider exige as chaves
nullable. O prompt foi versionado, invalidando caches de análise anteriores não consolidados.
Campos novos ausentes não são serializados, preservando a forma antiga usada nos hashes do journal.
No caminho automático, C# exige que a citação exista na página indicada e sustente os atributos
extraídos. Grounding dos itens, centavos, tipos, conciliação, total oficial e comparação com o
parser permanecem sob validação C#, não sob decisão livre do agente.
O contrato estruturado segue a [documentação oficial OpenAI](https://developers.openai.com/api/docs/guides/structured-outputs);
conformidade ao schema não substitui essas validações financeiras.

O backend exige correspondência única de emissor/dígitos entre cartões ativos do Personal autorizado.
Não usa HolderUserId do cartão pesquisado para resolver uma ambiguidade documental. Quando existe
nome cadastrado do titular e nome documental, divergência exige revisão. Nome de exibição não é
chave de autorização. Produto é evidência preservada; nome livre do cadastro não é tratado como
produto bancário oficial. Ausência de dígitos cadastrados/identificação única continua bloqueante.
Correspondência com outro cartão também não consolida silenciosamente no cartão pesquisado.

PDF protegido passa primeiro pelos mecanismos existentes de senha. `PasswordRequired` e
`PasswordRejected` solicitam senha na própria busca. O envio retoma o PDF armazenado e executa
análise/consolidação sem pesquisar outra mensagem, fazer upload ou exigir seleção manual de cartão.
A senha não é persistida em análise, documento ou resultado.

Os locks de busca e de documento são distintos; análise e consolidação usam o mesmo lock documental
sequencialmente, sem aquisição recursiva. Permanecem autorização exclusiva do Personal
`01a163b9-4b06-74f8-ac02-05bdc7cfb14e`, owner persistido, CSRF, dedupe, revalidação no commit,
transação financeira, versões oficiais e bloqueios de pagamentos/reconciliações preexistentes.
Nenhuma migration nova.

## Interface e contratos

O resultado HTTP da busca mantém os campos existentes e acrescenta `processing`, com `analysis`
e `consolidation`. A interface distingue recepção, consolidação, processamento concorrente e motivo
de revisão; atualiza a tela após consolidação. O endpoint `statement-processing` reutiliza o mesmo
coordenador para retomada. A análise manual isolada continua disponível para revisão.

## Evidências

- Backend final: **144/144**, sem ignorados, Release, 2 min 31 s.
  `.artifacts/t16-functional/t16-functional-verified.trx`.
- Frontend completo final: **357/357 em 37 arquivos**, dois workers; 37,46 s.
- Teste do componente de busca: 13/13, incluindo retomada por senha sem nova pesquisa.
- Build frontend aprovado; aviso de bundle acima de 500 kB permanece não bloqueante.
- Lint sem erros, com nove avisos preexistentes.
- Build final Release de `Saldo360.slnx`: zero avisos e zero erros; 44,97 s.
- Diff/whitespace conferidos. Sem mudanças de modelo ou migration nova.

A rodada final inclui o encadeamento Gmail sintético → documento existente → agente sintético →
consolidação real em PostgreSQL descartável → repetição idempotente. Confirma uma compra, uma parcela,
uma versão e um documento, categorias sem duplicação, zero pagamentos/transações/checkpoints e apenas
uma chamada ao agente nas duas buscas. Cobre também parser sem identificação, identificação inventada,
contradição parser/agente, outro cartão cadastrado, ambiguidade, titular divergente, senha e retomada,
provider desabilitado, forma serializada legada, autorização/CSRF e regressão dirigida de PDF/Inbox.

Rodadas intermediárias passaram com 94/94 e 99/99; são sobrepostas à final, não testes únicos somáveis.
Não foi executada regressão completa de backend.

## Arquivos principais

- Application: CardLatestEmailService, InboxStatementProcessingService, InboxStatementAnalysisService,
  CreditCardStatementAgentDtos/PreviewService/ExtractionOrchestrator, CreditCardStatementResolver e DI.
- Infrastructure: OpenAiCreditCardStatementAgentProvider, FinancialCreditCardRepository e
  CreditCardPdfConfirmationRepository.Consolidation (revalidação da identidade).
- API: FinancialDocumentInboxEndpoints (processamento compartilhado).
- React: api/creditCards e CardLatestEmailSearch, com teste de retomada.
- Testes: CardLatestEmailServiceTests, CardLatestEmailEndpointsTests, CreditCardStatementResolverTests,
  InboxStatementAnalysisTests e OfficialStatementConsolidationTests.

## Limites operacionais

Gmail, extração textual e provider são sintéticos nos testes; persistência/locks/consolidação usam
PostgreSQL descartável. Nenhuma caixa, credencial, IA ou fatura real foi consultada/processada.
Sem importação real, alteração de dados do responsável, push, deploy ou migration em produção.
Não foi realizado ensaio visual no navegador nem teste operacional com OAuth real.
