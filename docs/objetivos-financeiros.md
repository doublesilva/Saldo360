# Objetivos financeiros

T14.1 adiciona o cadastro de objetivos financeiros por família. Um objetivo é uma definição de intenção (por exemplo, reserva de emergência, viagem ou quitação de dívida); ele não é uma transação, previsão, orçamento ou aporte.

## Contrato

As rotas são:

- `GET/POST /api/families/{familyId}/financial-goals`
- `GET/PUT /api/families/{familyId}/financial-goals/{id}`
- `POST .../{id}/pause`, `.../{id}/resume` e `.../{id}/complete`
- `DELETE .../{id}` (arquivamento lógico)

O corpo de criação/edição usa `name`, `description`, `goalType`, `targetAmount`, `targetDate` (opcional, `yyyy-MM-dd`) e `priority`. `targetAmount` é string decimal com duas casas, por exemplo `"12500.00"`. As respostas também devolvem o valor como string.

Tipos: `EmergencyFund`, `Purchase`, `Travel`, `Education`, `DebtRepayment` e `Other`. Prioridades: `Low`, `Medium` e `High`.

## Estados

Objetivos iniciam `Active`. As transições explícitas são Active→Paused, Paused→Active, Active/Paused→Completed e Active/Paused/Completed→Archived. Não há reabertura implícita: objetivos concluídos ou arquivados não podem ser retomados, e objetivos arquivados não podem ser editados.

O arquivamento preserva o registro e o histórico. A operação exige autenticação, participação na família e proteção CSRF nas escritas.

## Limites e efeitos

O valor-alvo deve ser positivo e ter precisão decimal (PostgreSQL `numeric(18,2)`). A data-alvo é civil e opcional. O módulo não cria lançamentos, previsões, transferências, pagamentos, alterações de saldo, orçamento ou limite de cartão.

## Aportes e retiradas (T14.2)

Uma alocação designa recursos já existentes de uma conta a um objetivo; ela não é transferência, despesa, receita ou previsão. Os endpoints são `GET/POST .../{goalId}/allocations`, `POST .../{goalId}/contributions`, `POST .../{goalId}/withdrawals`, `POST .../{goalId}/allocations/{allocationId}/cancel`, `GET .../{goalId}/progress` e `GET /api/families/{familyId}/financial-goals/account-availability`.

Cada operação exige `operationId`, conta, valor string decimal positivo e `effectiveDate` igual ao dia corrente em `America/Sao_Paulo`. O mesmo `operationId` com o mesmo conteúdo é idempotente; conteúdo diferente resulta em conflito. A soma de aportes menos retiradas é calculada por objetivo e conta, sem saldo agregado persistido.

Aportes usam o saldo realizado configurado da conta e são rejeitados quando o saldo não alocado ficaria negativo, quando a conta está inativa ou quando ultrapassam o valor-alvo. Retiradas são limitadas ao valor alocado naquela combinação de objetivo e conta. Operações concorrentes usam transação PostgreSQL `Serializable`; cancelamento é lógico e preserva o histórico. O saldo bancário, previsões, orçamentos e transferências permanecem inalterados.
