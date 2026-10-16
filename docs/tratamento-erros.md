# Tratamento de erros da API

## Contrato

A API usa `application/problem+json`, compatível com RFC 9457. A T16.1 centraliza o preenchimento e a sanitização do contrato sem alterar respostas de sucesso:

```json
{
  "type": "https://saldo360.local/problems/validation-error",
  "title": "Dados inválidos.",
  "status": 400,
  "detail": "Revise os dados e tente novamente.",
  "instance": "/api/families/.../payments",
  "code": "VALIDATION_ERROR",
  "traceId": "0af7651916cd43dd8448eb211c80319c",
  "errors": {
    "amount": ["Valor decimal inválido."]
  }
}
```

`errors` existe somente quando há validação por campo. `code` é estável para decisões do cliente; textos podem evoluir. `traceId` vem da `Activity` do servidor (ou do `HttpContext.TraceIdentifier`) e serve apenas para correlação com logs. Um identificador enviado pelo cliente não substitui esse valor.

## Status e códigos

| Status | Código padrão | Uso |
| --- | --- | --- |
| 400 | `VALIDATION_ERROR` ou `INVALID_REQUEST` | Payload ou campos inválidos |
| 401 | `AUTHENTICATION_REQUIRED` | Sessão ausente ou inválida |
| 403 | `ACCESS_DENIED` | Operação autenticada sem permissão, quando revelar a existência do recurso é seguro |
| 404 | `RESOURCE_NOT_FOUND` | Recurso inexistente ou deliberadamente oculto |
| 409 | `OPERATION_CONFLICT` | Estado, concorrência ou idempotência incompatível |
| 422 | `BUSINESS_RULE_VIOLATION` | Regra de negócio inviável, quando o endpoint adotar esse status |
| 500 | `INTERNAL_ERROR` | Falha inesperada sanitizada |
| 503 | `DEPENDENCY_UNAVAILABLE` | Dependência comprovadamente indisponível |

Os fluxos integrados nesta etapa também usam `DUPLICATE_OPERATION` quando um `operationId` é reapresentado com conteúdo diferente e `INSUFFICIENT_FUNDS` quando o serviço retorna o conflito específico de saldo não alocado. Recursos de outra família continuam sendo respondidos como 404, evitando enumeração de dados privados.

## Segurança e correlação

Exceções não tratadas são registradas internamente com caminho e `traceId`, e retornam `500/INTERNAL_ERROR` com texto genérico. A resposta não inclui stack trace, SQL, connection string, token, segredo nem payload financeiro. Os logs não devem registrar corpos financeiros completos; o `traceId` é o elo de diagnóstico.

## Cliente React

`client/src/api/apiErrors.ts` oferece um único normalizador para:

- validação, com mensagens por campo;
- regra de negócio e conflito;
- autenticação e autorização;
- dependência indisponível;
- falha de rede;
- timeout;
- cancelamento intencional (`AbortError`);
- falha inesperada, sem reutilizar texto técnico arbitrário.

O erro normalizado preserva `status`, `code`, `traceId`, `fieldErrors` e `resultUncertain`. Cancelamentos causados por troca de família não são exibidos como falhas financeiras.

## Escritas, timeout e idempotência

Pagamentos de fatura, pagamentos de parcela de dívida e aporte/retirada em objetivo usam timeout de cliente de 15 segundos. Conciliações financeiras seguem a mesma política de resultado incerto. Não há repetição automática de POST ou DELETE.

O `operationId` é criado antes do envio e permanece igual após timeout ou falha de rede. Um novo identificador só é criado depois de sucesso confirmado. Pagamentos de fatura, dívida e alocações possuem constraint única por família e comparam o payload da intenção; reutilizar o identificador com conteúdo diferente retorna conflito.

Após uma resposta perdida, o cliente faz uma leitura segura do histórico. A confirmação usa somente `operationId` ou a identidade estrutural explícita da conciliação. Se a leitura também falhar ou não localizar a operação, a interface mantém o resultado incerto e conserva a identidade para um eventual reenvio manual idempotente. Nunca deduz sucesso por descrição, valor ou data.

Um bloqueio síncrono impede dois envios simultâneos no componente. Cancelamentos e desvinculações são lógicos e convergentes: repetir o mesmo comando não aplica a reversão novamente e não apaga o histórico.

Consultas canceladas por mudança de contexto continuam silenciosas. Em falhas reais, a interface não deve substituir valor desconhecido por zero nem apresentar dados antigos como atuais sem indicação.

## Adoção incremental e limitações

Na T16.1, o backend central passou a enriquecer Problem Details, respostas vazias de status e exceções inesperadas. Na T16.3, os fluxos financeiros prioritários passaram a reconciliar reenvios e concorrência por identidade persistida ou estrutural. Os demais módulos continuam compatíveis e poderão substituir gradualmente objetos de erro legados pelo helper comum na T16.4.

Não foram alteradas regras financeiras, autenticação ou cálculos. A única mudança de banco da T16.3 é a identidade idempotente necessária para pagamentos de fatura. Não há retry automático, telemetria distribuída completa nem notificação externa.
