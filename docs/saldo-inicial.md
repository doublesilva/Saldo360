# Saldo inicial das contas

Previsões originadas de recorrências só influenciam a projeção depois de materializadas. A definição e as ocorrências virtuais não alteram o saldo inicial, o saldo realizado ou a projeção; o lançamento efetivo continua sendo o único evento que altera o saldo realizado.

Obrigações de cartão, inclusive parcelas históricas importadas, não alteram o saldo realizado da conta. Afetam apenas a projeção quando a fatura possui conta preferencial válida; o pagamento da fatura é o evento que movimenta o saldo bancário.

Cada conta financeira pode ter um **saldo inicial** opcional, formado por dois campos que existem juntos ou não existem:

| Campo | Tipo | Regra |
| --- | --- | --- |
| `OpeningBalance` (`opening_balance`) | `decimal` / `numeric` | Centavos exatos (no máximo 2 casas decimais). Pode ser negativo. \|valor\| < 10¹³. |
| `OpeningBalanceDate` (`opening_balance_date`) | `DateOnly` / `date` | Data de referência, sem horário nem fuso (`yyyy-MM-dd`). |

Conta sem saldo inicial tem os dois campos nulos. Isso significa **"não informado"**, não zero. As contas cadastradas antes desta configuração continuam assim.

## Regra da data de referência

> O saldo inicial é o saldo da conta **no início do dia** `OpeningBalanceDate`, **antes de qualquer movimentação desse dia**.

Consequências:

- Movimentações com data **anterior** a `OpeningBalanceDate` **já estão incluídas** no saldo inicial. Elas não devem ser importadas, ou devem ficar fora de qualquer soma.
- Movimentações com data **igual ou posterior** a `OpeningBalanceDate` **não estão incluídas**. Elas devem ser importadas e somadas.
- O saldo ao fim de um dia `X ≥ OpeningBalanceDate` é:

  ```text
  saldo(fim de X) = OpeningBalance + Σ movimentações com OpeningBalanceDate ≤ data ≤ X
  ```

### Exemplo

A planilha está conciliada até 30/09/2026, com saldo final de R$ 1.234,56 nesse dia.

- Cadastre `OpeningBalance = 1234.56` e `OpeningBalanceDate = 2026-10-01`.
- Na importação, **descarte** as linhas até 30/09/2026, porque já estão no saldo.
- **Importe** as linhas a partir de 01/10/2026, inclusive as do próprio dia 01/10.

Erros comuns a evitar:

- Usar 30/09 como data de referência com o saldo **final** de 30/09. Pela regra, esse valor seria o saldo do *início* de 30/09, e as movimentações de 30/09 seriam contadas duas vezes.
- Usar 01/10 com o saldo **depois** das movimentações de 01/10. Nesse caso, as movimentações de 01/10 também seriam contadas duas vezes.

Em extratos bancários, o valor certo costuma ser o "saldo anterior" do dia de referência, que é o mesmo que o saldo final do dia anterior.

## Lançamentos anteriores ao saldo inicial

Desde a T05.2, os lançamentos realizados (`financial_transactions`) **podem** ter `transaction_date` anterior a `OpeningBalanceDate`. Isso serve para preservar o histórico, por exemplo ao registrar movimentações antigas da planilha para consulta.

Esses lançamentos **já estão dentro do saldo inicial**. Por isso, o cálculo do saldo realizado (abaixo) os ignora:

```text
saldo(fim de X) = OpeningBalance
                + Σ receitas   não canceladas da conta com OpeningBalanceDate ≤ data ≤ X
                − Σ despesas   não canceladas da conta com OpeningBalanceDate ≤ data ≤ X
```

- `amount` é sempre positivo. O sentido vem de `type`: `Income` soma e `Expense` subtrai.
- Lançamentos cancelados (`is_cancelled = true`) nunca entram na soma.
- Conta sem saldo inicial: o saldo é **indisponível**, nunca zero (ver [Saldo realizado](#saldo-realizado)).
- A tela de lançamentos aceita a data antiga e avisa que ela não entra de novo no saldo.

## Saldo realizado

O saldo realizado de uma conta é o saldo no **fechamento** da data consultada (`asOf`). Ele é calculado a cada consulta e não fica gravado no banco.

```text
saldo(asOf) = OpeningBalance
            + Σ receitas                (lançamentos Income)
            − Σ despesas                (lançamentos Expense)
            + Σ transferências recebidas (a conta é o destino)
            − Σ transferências enviadas  (a conta é a origem)

considerando só movimentações não canceladas da própria conta e família com
OpeningBalanceDate ≤ data da movimentação ≤ asOf
```

- O saldo inicial vale para o **início** de `OpeningBalanceDate`, e o resultado vale para o **fim** de `asOf`. Por isso, as movimentações do próprio dia de referência entram na soma.
- Movimentações anteriores à referência continuam no histórico, mas não entram na soma, porque já fazem parte do saldo inicial. Movimentações posteriores a `asOf` também ficam fora, de modo que uma consulta passada mostra o saldo daquele dia.
- Uma transferência é **um único registro** com origem e destino. Ela não gera receita nem despesa e não tem categoria. Para corrigir, ela é cancelada e cadastrada de novo; não há edição.
- **Cada conta usa o próprio período.** Uma transferência pode estar antes da referência de uma das contas e depois da referência da outra. Nesse caso, ela já está no saldo inicial da primeira e não é somada de novo, mas ainda entra no cálculo da segunda.

  Exemplo: PJ com referência em 01/09 e PF com referência em 10/09. Uma transferência PJ → PF de 500,00 em 05/09 é subtraída do PJ, mas não é somada ao PF, porque o saldo inicial do PF em 10/09 já a inclui.
- A origem pode ficar negativa: a transferência não é bloqueada por saldo insuficiente.
- **Previsões pendentes não entram no saldo realizado.** Receitas e despesas previstas (`financial_planned_transactions`) não alteram nenhum total desta resposta e, vencidas, continuam pendentes. Uma previsão só passa a contar quando é **realizada**: aí entra o lançamento efetivo vinculado (com o valor e a data efetivos), como qualquer lançamento. Cancelar esse lançamento tira o valor do saldo e reabre a previsão ([previsões e realização](previsoes.md)).
- As somas são feitas no PostgreSQL, sobre `numeric`, em duas consultas agregadas:
  - lançamentos: `SUM` condicional por tipo;
  - transferências: `SUM` condicional pelo papel da conta, destino ou origem.

  Nenhuma movimentação é carregada em memória. A aplicação só combina os totais em `decimal`. Nada passa por ponto flutuante, e somas acima do limite de uma movimentação individual (10¹³) não são truncadas.
- Saldo negativo significa valor devido, por exemplo cheque especial em uso. Não é crédito disponível; limite bancário está fora do escopo.

### API

`GET /api/families/{familyId}/accounts/{accountId}/balance?asOf=yyyy-MM-dd`

- `asOf` é opcional. O padrão é hoje em America/Sao_Paulo. Uma data futura, ou fora do formato `yyyy-MM-dd`, retorna `400`. Por exemplo, `01/09/2026` é recusado em vez de ser lido como 9 de janeiro.
- Conta inativa pode ser consultada. Conta inexistente, de outra família ou consulta de quem não participa da família retorna `404`.
- Os valores monetários vêm como **texto decimal exato** com duas casas (`"1300.00"`, `"-0.75"`), nunca como número JSON. O frontend só formata esse texto e não soma nada.

Resposta disponível:

```json
{
  "accountId": "…", "asOf": "2026-10-10",
  "openingBalance": "1000.00", "openingBalanceDate": "2026-10-01",
  "totalIncome": "500.00", "totalExpense": "200.00",
  "totalTransferIn": "500.00", "totalTransferOut": "0.00",
  "balance": "1800.00",
  "unavailableReason": null
}
```

`totalTransferIn` e `totalTransferOut` vêm separados de `totalIncome` e `totalExpense`: transferência não é receita nem despesa.

### Retornos indisponíveis

Quando o saldo não pode ser calculado, a resposta é `200` com `unavailableReason` preenchido e `totalIncome`, `totalExpense`, `totalTransferIn`, `totalTransferOut` e `balance` iguais a **`null`**. Indisponível nunca é zero.

| `unavailableReason` | Quando | `openingBalance` / `openingBalanceDate` | Interface |
| --- | --- | --- | --- |
| `OpeningBalanceNotConfigured` | A conta não tem saldo inicial | `null` / `null` | "Saldo realizado indisponível. Configure o saldo inicial para calcular o saldo." |
| `BeforeOpeningBalanceDate` | `asOf` < `OpeningBalanceDate` | valores configurados | "Saldo realizado indisponível: a data de referência do saldo inicial (DD/MM/AAAA) é posterior a DD/MM/AAAA." |

## Saldo projetado

O saldo projetado responde à pergunta "quanto terei até a data X se as previsões pendentes se confirmarem?". Ele parte do saldo realizado de hoje e soma **somente previsões pendentes**. Também é calculado a cada consulta e nunca fica gravado.

```text
data-base  = hoje em America/Sao_Paulo (capturada uma vez por consulta), no fechamento do dia
data-alvo  = through (obrigatória; hoje ou futura)

projectedBalance = realizedBalance                 (saldo realizado no fechamento da data-base)
                 + overdueIncome  − overdueExpense (pendentes com dueDate <  data-base)
                 + plannedIncome  − plannedExpense (pendentes com data-base ≤ dueDate ≤ data-alvo)
```

### Quais previsões entram

- **Pendentes**, isto é, não canceladas e sem lançamento efetivo ativo ([previsões e realização](previsoes.md)), da própria conta e família, com `dueDate ≤ data-alvo`.
- **Vencidas entram, separadas.** Uma previsão pendente com `dueDate` anterior a hoje é um compromisso que ainda não aconteceu. Ela entra em `overdueIncome`/`overdueExpense`, e não em `plannedIncome`/`plannedExpense`. A previsão que vence **hoje** fica nas do período.
- **A data do saldo inicial não filtra previsões.** Uma pendente com data anterior à referência da conta continua contando: previsão não é movimentação realizada, então não está "dentro" do saldo inicial.
- **Conta ou categoria inativa não esconde o compromisso.** A conta inativa também pode ser consultada.
- **Ficam de fora:**
  - previsões canceladas;
  - previsões **realizadas**: o valor efetivo já está no saldo realizado, pelo lançamento vinculado, e o previsto não é somado de novo;
  - previsões com `dueDate` depois da data-alvo.
- **Cancelar o lançamento efetivo** reabre a previsão, que volta a entrar na projeção.
- Nada é presumido: não há gastos estimados nem previsões geradas automaticamente.

Exemplo, com a data-alvo no fim do mês:

| Situação | Realizado | Vencidas (desp.) | Previstas (rec. / desp.) | Projetado |
| --- | --- | --- | --- | --- |
| Saldo 1.000,00, receita prevista 500,00, despesa prevista 200,00 | 1.000,00 | 0,00 | 500,00 / 200,00 | **1.300,00** |
| + despesa vencida pendente de 100,00 | 1.000,00 | 100,00 | 500,00 / 200,00 | **1.200,00** |
| Despesa de 200,00 realizada por 210,00 | 790,00 | 100,00 | 500,00 / 0,00 | **1.190,00** |
| Lançamento efetivo de 210,00 cancelado | 1.000,00 | 100,00 | 500,00 / 200,00 | **1.200,00** |

### Leitura consistente

O saldo realizado (saldo inicial, lançamentos e transferências) e as previsões pendentes são lidos numa **única transação somente leitura `REPEATABLE READ`**. No PostgreSQL, isso significa que todas as consultas usam o **mesmo snapshot**.

Como "realizada" é derivado do próprio lançamento vinculado, uma realização ou um cancelamento concorrente aparece inteiro ou não aparece:

- se aparece, o lançamento efetivo é somado e a previsão sai;
- se não aparece, a previsão é somada e o lançamento ainda não existe.

A projeção nunca conta os dois, nem deixa de contar ambos. O endpoint de saldo realizado usa a mesma leitura consistente.

As somas são agregadas no banco (`SUM` condicional por tipo e vencimento, com `NOT EXISTS` para excluir as realizadas), sobre `numeric`, sem carregar movimentações nem previsões em memória e sem arredondar.

### API da projeção

`GET /api/families/{familyId}/accounts/{accountId}/projection?through=yyyy-MM-dd`

O planejamento semestral da Agenda reutiliza esta mesma fórmula por dia e toma o último dia de cada competência como
fechamento. Ele não mantém uma fórmula paralela; veja [Planejamento dos próximos seis meses](agenda-financeira.md#planejamento-dos-próximos-seis-meses).

- `through` é obrigatório, só no formato `yyyy-MM-dd`, e deve ser hoje ou futuro. Ausente, inválido ou passado retorna `400`.
- Conta inexistente, de outra família ou consulta de quem não participa da família retorna `404`. Conta inativa pode ser consultada.
- Resposta: `{ accountId, baseDate, through, realizedBalance, overdueIncome, overdueExpense, plannedIncome, plannedExpense, projectedBalance, unavailableReason }`, com valores em **texto decimal exato**.
- **Indisponível.** Se o saldo realizado de hoje estiver indisponível (`OpeningBalanceNotConfigured`, ou `BeforeOpeningBalanceDate` quando a referência é futura), `unavailableReason` traz o mesmo motivo e **todos** os valores monetários vêm `null`. Indisponível nunca é zero.

### Evolução diária na Agenda

O endpoint `GET /api/families/{familyId}/financial-calendar/balances?from=...&through=...[&accountId=...]`
aplica a mesma fórmula da projeção sem consultar a T06.3 repetidamente. O histórico anterior a hoje contém somente
saldo realizado. No ponto de hoje, o fechamento realizado recebe uma única vez as pendências vencidas e as previsões
de hoje; cada dia futuro acumula apenas as previsões pendentes da própria data.

Por isso, para uma conta e uma data futura `D`, o `closingBalance` de `D` coincide com o `projectedBalance` da T06.3
para `through=D`, quando as consultas enxergam o mesmo estado dos dados. A fórmula compartilhada fica no backend e
opera somente com `decimal`.

No consolidado familiar, todas as contas precisam ter saldo calculável. Os saldos iniciais e lançamentos válidos são
somados, enquanto transferências entre essas contas são eliminadas: elas movem dinheiro dentro do conjunto, sem criar
receita, despesa ou variação líquida. Se qualquer conta necessária estiver indisponível, o consolidado não retorna um
total parcial.

## Orientações para a futura importação

1. **Compare por data de calendário, não por instante.** A data de uma movimentação é a data local do lançamento (fuso do usuário, `America/Sao_Paulo`), não um `timestamp` UTC. Uma compra às 22h de 30/09 em Brasília é de 30/09, mesmo que em UTC já seja 01/10.
2. **Filtre com `data >= OpeningBalanceDate`.** O limite é inclusivo do lado das movimentações: o próprio dia de referência entra.
3. **Conta sem saldo inicial:** não presuma zero. A importação deve pedir que o saldo inicial seja configurado, ou tratar a conta como "saldo não calculável" até isso acontecer.
4. **Alteração posterior do saldo inicial:** mudar a data de referência muda quais movimentações estão "dentro" do saldo. Se a data for adiantada, as movimentações entre a data antiga e a nova passam a estar incluídas no saldo e deixam de entrar na soma. Se for atrasada, o inverso acontece. O cálculo do saldo atual deve sempre derivar dessa regra, e não guardar uma soma acumulada que fique desatualizada.
5. **Saldo negativo** representa limite já utilizado (por exemplo, cheque especial). Ele **não** é limite de crédito disponível, que não é modelado nesta etapa.

## Validação e garantias

- **API** (`POST`/`PUT /api/families/{id}/accounts`): `openingBalance` é enviado como texto decimal (`"-1234.56"`, recomendado) ou número JSON, e `openingBalanceDate` como string `yyyy-MM-dd`. Nas respostas (criação, listagem, consulta e edição), `openingBalance` vem **sempre** como texto decimal exato com duas casas ou `null`, nunca como número JSON; o frontend só formata esse texto. Data com horário ou data inexistente gera `400`. A API devolve `400` com `errors.openingBalance` ou `errors.openingBalanceDate` quando só um dos dois é informado, quando há mais de duas casas decimais (inclusive zeros à direita, como `1.500`) ou quando o valor está fora do limite. **Nada é arredondado.** O `PUT` substitui o cadastro inteiro: omitir os dois campos (ou enviá-los nulos) remove a configuração.
- **Banco** (migration `AddOpeningBalanceToFinancialAccounts`):
  - `ck_financial_accounts_opening_balance_pair`: valor e data nulos juntos ou preenchidos juntos.
  - `ck_financial_accounts_opening_balance_scale`: `scale(opening_balance) <= 2`. A coluna é `numeric` sem escala fixa de propósito, porque `numeric(15,2)` arredondaria `10.005` para `10.01` em silêncio.
  - `ck_financial_accounts_opening_balance_range`: `abs(opening_balance) < 10^13`. Assim o valor tem no máximo 15 dígitos significativos e o frontend o representa sem perda.
- **Interface:** mostra "Saldo inicial não informado" ou "Saldo inicial em DD/MM/AAAA: R$ …", com a observação "no início do dia, antes das movimentações dessa data. Não é o saldo atual."
# Projeção com faturas de cartão (T09.5)

O saldo projetado considera faturas devidas somente quando há conta de pagamento válida e explícita no cartão. Sem essa alocação, a obrigação permanece disponível na projeção de faturas, mas não é atribuída silenciosamente a uma conta.

O componente de cartão é aplicado após o saldo realizado e as previsões existentes, preservando a fórmula: `projectedBalance = existingProjectedBalance - outstandingCreditCardObligations`.

Quando uma previsão está explicitamente conciliada a uma fatura, ela é removida apenas do componente de previsões pendentes; a obrigação da fatura continua sendo descontada. Consulte [conciliação de faturas](conciliacao-faturas.md).
