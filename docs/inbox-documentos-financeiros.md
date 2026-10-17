# Caixa de Entrada de Documentos Financeiros

A Inbox recebe documentos antes da importação financeira. Upload manual, futuro agente de e-mail e outras integrações compartilham a mesma identidade, armazenamento e análise. Receber ou analisar um documento não cria compras, ajustes, pagamentos, transações, previsões ou alterações orçamentárias.

## Modelo e estados

`FinancialDocumentInboxItem` guarda família, origem (`Manual`/`Email`), identificador externo opcional, nome original sanitizado, SHA-256, tipo de conteúdo, referência opaca do storage, datas, parser, competência e cartão opcional. O PDF e o texto extraído não são gravados na tabela.

Os estados são `Received`, `Analyzing`, `ReadyForReview`, `ReadyToConfirm`, `NeedsReview`, `Conflict`, `Imported`, `Failed` e `Ignored`. Falhas persistem somente códigos sanitizados, como `PasswordRequired`, `TextUnavailable` ou `StorageObjectMissing`.

- parser especializado, conferência `Reconciled` e cartão inequívoco: `ReadyToConfirm`;
- parser genérico, divergência, cartão ausente ou múltiplos finais: `NeedsReview`;
- falha técnica: `Failed`;
- confirmação concluída pelo pipeline PDF existente: `Imported`.

## Storage privado

`IFinancialDocumentStorage` desacopla o domínio do meio físico. A implementação local usa filesystem e referências no formato `{familyId}/{documentId}.pdf`. O diretório vem de `FinancialDocuments:StoragePath`; na ausência da configuração usa `data/financial-documents` sob o diretório da aplicação. Ele não deve ser montado como conteúdo estático nem servido diretamente pelo proxy. Em contêiner, configure um volume privado persistente.

As gravações usam arquivo temporário e movimentação atômica. Referências são validadas contra travessia de diretório. Downloads futuros deverão sempre passar pela autorização da aplicação.

## Identidade e idempotência

O SHA-256 é calculado antes da persistência. Existem constraints únicas para:

- `FamilyId + ContentHash`;
- `FamilyId + Source + ExternalSourceId`, quando informado;
- referência opaca do storage.

Mesmo identificador externo e mesmo hash retorna o item existente. Mesmo identificador com conteúdo diferente retorna conflito. O mesmo hash por outra origem também retorna o item existente. Nome, competência, descrição e total não comprovam identidade. Violações concorrentes são absorvidas pela constraint PostgreSQL e convertidas no mesmo resultado lógico; o objeto excedente é removido do storage.

## API

- `GET /api/families/{familyId}/financial-documents/inbox`
- `POST /api/families/{familyId}/financial-documents/inbox` — `multipart/form-data` com `file`, `externalSourceId`, `creditCardId` e `password` opcionais.
- `POST /api/families/{familyId}/financial-documents/inbox/{id}/reanalyze` — cartão e senha efêmera opcionais.
- `POST /api/families/{familyId}/financial-documents/inbox/{id}/ignore`.
- `POST /api/families/{familyId}/financial-documents/inbox/{id}/confirm` — reusa integralmente a confirmação T15.3.5, com `creditCardId`, `confirm`, `documentId`, `categories` e senha efêmera.

Escritas exigem autenticação, participação na família e CSRF. Senha, CPF, titular e texto integral nunca são persistidos ou devolvidos. O endpoint não oferece acesso direto aos bytes armazenados.

## Processamento

O processador abre o objeto privado, reutiliza desbloqueio/prévia quando há cartão selecionado e usa o extrator, resolvedor de parser e resolvedor de cartão existentes quando ainda não há destino. Itaú Mastercard e Nubank seguem seus parsers especializados; `Generic` permanece conservador. A confirmação abre novamente o mesmo objeto e chama `CreditCardPdfConfirmationService`, evitando um segundo motor financeiro.

## Limitações e retenção

Não há Gmail, IMAP, agendamento, OCR ou confirmação automática. Uma senha manual não é armazenada; documentos protegidos podem exigir nova senha ao reanalisar ou confirmar. Não há expurgo automático: prazo, base legal, quota, criptografia adicional em repouso e política de retenção deverão ser definidos antes de uma integração de e-mail em produção.
