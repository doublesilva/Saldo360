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

O valor-alvo deve ser positivo e ter precisão decimal (PostgreSQL `numeric(18,2)`). A data-alvo é civil e opcional. O módulo não cria lançamentos, previsões, transferências, pagamentos, alterações de saldo, orçamento ou limite de cartão. Contribuições e progresso serão tratados em etapa posterior (T14.2).
