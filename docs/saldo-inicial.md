# Saldo inicial das contas

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

  ```
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

## Orientações para a futura importação

1. **Compare por data de calendário, não por instante.** A data de uma movimentação é a data local do lançamento (fuso do usuário, `America/Sao_Paulo`), não um `timestamp` UTC. Uma compra às 22h de 30/09 em Brasília é de 30/09, mesmo que em UTC já seja 01/10.
2. **Filtre com `data >= OpeningBalanceDate`.** O limite é inclusivo do lado das movimentações: o próprio dia de referência entra.
3. **Conta sem saldo inicial:** não presuma zero. A importação deve pedir que o saldo inicial seja configurado, ou tratar a conta como "saldo não calculável" até isso acontecer.
4. **Alteração posterior do saldo inicial:** mudar a data de referência muda quais movimentações estão "dentro" do saldo. Se a data for adiantada, as movimentações entre a data antiga e a nova passam a estar incluídas no saldo e deixam de entrar na soma. Se for atrasada, o inverso acontece. O cálculo do saldo atual deve sempre derivar dessa regra, e não guardar uma soma acumulada que fique desatualizada.
5. **Saldo negativo** representa limite já utilizado (por exemplo, cheque especial). Ele **não** é limite de crédito disponível, que não é modelado nesta etapa.

## Validação e garantias

- **API** (`POST`/`PUT /api/families/{id}/accounts`): `openingBalance` é um número JSON e `openingBalanceDate` é uma string `yyyy-MM-dd`. Data com horário ou data inexistente gera `400`. A API devolve `400` com `errors.openingBalance` ou `errors.openingBalanceDate` quando só um dos dois é informado, quando há mais de duas casas decimais (inclusive zeros à direita, como `1.500`) ou quando o valor está fora do limite. **Nada é arredondado.** O `PUT` substitui o cadastro inteiro: omitir os dois campos (ou enviá-los nulos) remove a configuração.
- **Banco** (migration `AddOpeningBalanceToFinancialAccounts`):
  - `ck_financial_accounts_opening_balance_pair`: valor e data nulos juntos ou preenchidos juntos.
  - `ck_financial_accounts_opening_balance_scale`: `scale(opening_balance) <= 2`. A coluna é `numeric` sem escala fixa de propósito, porque `numeric(15,2)` arredondaria `10.005` para `10.01` em silêncio.
  - `ck_financial_accounts_opening_balance_range`: `abs(opening_balance) < 10^13`. Assim o valor tem no máximo 15 dígitos significativos e o frontend o representa sem perda.
- **Interface:** mostra "Saldo inicial não informado" ou "Saldo inicial em DD/MM/AAAA: R$ …", com a observação "no início do dia, antes das movimentações dessa data. Não é o saldo atual."
