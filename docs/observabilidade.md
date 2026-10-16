# Observabilidade e diagnóstico operacional

## Visão geral

A observabilidade do Saldo360 reutiliza o `Activity.TraceId` usado pelo contrato de erros. Ela é diagnóstica: não altera transações, resultados financeiros nem regras de negócio. A implementação não depende de Grafana, Prometheus, Loki, Jaeger ou de um coletor externo.

## Logs de requisição

Cada requisição concluída gera um evento estruturado `RequestCompleted` com:

- `TraceId`;
- `RequestMethod`;
- `RoutePattern`;
- `StatusCode`;
- `ElapsedMilliseconds`;
- `ErrorCode`.

`RoutePattern` é o template normalizado da rota, como `/api/families/{familyId:guid}/debts/{debtId:guid}`. A URL concreta e a query string não são registradas. Corpos, cookies, tokens, cabeçalhos de autorização, valores financeiros e descrições também ficam fora do log.

Requisições normais usam `Information`. Validações e 404 comuns não são incidentes críticos. Autenticação, autorização e conflitos relevantes usam `Warning`. Respostas 5xx geram um resumo em `Warning`; a exceção inesperada é registrada uma única vez, em `Error`, pelo handler. Consultas bem-sucedidas e frequentes do Dashboard usam `Debug` para evitar volume excessivo.

## Operações financeiras

Pagamento de dívida, pagamento de fatura, aporte/retirada em objetivo e conciliações registram `FinancialOperationCompleted` com:

- `OperationType`;
- `OperationResult` (`Succeeded` ou `Rejected`);
- `ErrorCode`;
- `ElapsedMilliseconds`;
- `TraceId`.

Esses eventos não contêm IDs de família, usuário, conta ou recurso, nem valor, descrição ou payload. São logs técnicos, diferentes do histórico financeiro persistido pelas regras de negócio.

## Correlação e exceções

O `TraceId` vem de `Activity.Current.TraceId`; quando não existe `Activity`, usa-se `HttpContext.TraceIdentifier`. Identificadores enviados pelo cliente não substituem essa correlação.

O mesmo valor aparece no Problem Details e nos logs. Exceções inesperadas retornam `500/INTERNAL_ERROR`. Falhas cuja cadeia de exceções pertence ao provedor Npgsql retornam `503/DEPENDENCY_UNAVAILABLE`. O log inclui somente tipo da exceção, código, dependência, rota e correlação: mensagem interna, stack trace e objeto da exceção não são enviados ao provedor de log da aplicação.

## PostgreSQL

O EF Core e o Npgsql mantêm suas categorias nativas. Em configuração padrão, `Microsoft.EntityFrameworkCore.Database.Command` e `Npgsql` ficam em `Warning`; no desenvolvimento, comandos EF continuam disponíveis em `Information`. `EnableSensitiveDataLogging` não é habilitado.

O health check de readiness usa `Database.CanConnectAsync`, sem migrations ou consultas financeiras. Em falha, registra somente:

- `Dependency=PostgreSQL`;
- `DependencyResult=Unhealthy`;
- tipo técnico da falha.

Connection string, host, credenciais e mensagem da exceção não são incluídos.

## Health checks

| Endpoint | Dependências | Uso |
| --- | --- | --- |
| `/health/live` | Somente processo (`self`) | Liveness; permanece saudável mesmo sem PostgreSQL |
| `/health/ready` | PostgreSQL | Readiness para recebimento de tráfego |
| `/health` | PostgreSQL | Compatibilidade exata com o readiness anterior |

As respostas públicas incluem apenas nome do check, estado e duração. Exceções e descrições internas não são serializadas. O timeout de readiness é configurável por `HealthChecks:ReadinessTimeout` e vale três segundos por padrão.

## Métricas

As métricas nativas do ASP.NET Core continuam habilitadas pelo runtime, incluindo contagem e duração de requisições e dimensões HTTP normalizadas. O serviço de health checks também mantém sua instrumentação nativa. Nenhuma métrica usa usuário, família, conta, descrição ou valor financeiro como label.

Não há exportador ou coletor configurado nesta etapa. Portanto, essas métricas estão disponíveis somente aos mecanismos locais do runtime e não representam monitoramento contínuo externo.

## Configuração por ambiente

Os níveis podem ser alterados pelas chaves usuais de `Logging:LogLevel`. A configuração base evita SQL detalhado em produção. `appsettings.Development.json` preserva diagnóstico adicional do EF Core para desenvolvimento, ainda sem valores sensíveis.

Exemplo seguro:

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.EntityFrameworkCore.Database.Command": "Warning",
      "Npgsql": "Warning"
    }
  },
  "HealthChecks": {
    "ReadinessTimeout": "00:00:03"
  }
}
```

## Limitações e evolução

- Não existe armazenamento central de logs, exportador de métricas, tracing distribuído ou alerta externo.
- A classificação `DEPENDENCY_UNAVAILABLE` depende da cadeia de exceções do provedor Npgsql; erros de integridade tratados pelos serviços continuam conflitos de negócio.
- A observabilidade é incremental. Outros fluxos podem adotar os mesmos eventos estruturados sem alterar o contrato financeiro.
- Uma evolução futura pode adicionar OpenTelemetry e um coletor externo, mantendo os nomes de propriedades e a política de privacidade aqui definidos.

## Auditoria T16.5

A validação final confirmou a correlação por `Activity.TraceId` e, na ausência de Activity, por `HttpContext.TraceIdentifier`. Valores de correlação fornecidos arbitrariamente pelo cliente não substituem o identificador do servidor. As regressões também confirmaram rota normalizada, ausência de query string e credenciais nos eventos, health checks sem migrations ou gravações e auditoria NuGet sem vulnerabilidades conhecidas.
