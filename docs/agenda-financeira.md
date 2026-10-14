# Agenda Financeira

A Agenda Financeira reúne, em uma visão mensal somente leitura, os lançamentos realizados e as previsões que ainda
estão pendentes. Ela fica disponível na opção **Agenda** da navegação de cada núcleo familiar.

## Navegação e filtros

- O mês atual é aberto inicialmente segundo o dia civil de `America/Sao_Paulo`.
- As setas avançam ou retornam um mês, inclusive entre anos; **Mês atual** volta ao mês e ao dia de hoje.
- **Conta** limita a agenda a uma conta financeira. Contas inativas continuam disponíveis para que o histórico
  permaneça consultável.
- **Tipo** permite mostrar todas as movimentações, somente receitas ou somente despesas.

Cada alteração de mês ou filtro consulta novamente:

```text
GET /api/families/{familyId}/financial-calendar
    ?from=primeiro-dia-do-mês
    &through=último-dia-do-mês
    [&accountId=...]
    [&type=Income|Expense]
```

Uma requisição em andamento é cancelada quando os parâmetros mudam. A tela também associa cada resposta à consulta
que a originou, impedindo que uma resposta antiga substitua o mês ou a família atual.

## Calendário e indicadores

O calendário usa sete colunas, de domingo a sábado. O dia atual recebe um círculo preenchido e o dia selecionado uma
borda destacada. Todos os dias do mês são botões acessíveis por teclado, inclusive os que não têm acontecimentos.

Os indicadores combinam texto/símbolo, contorno e cor, de modo que a informação não dependa somente de cor:

- `✓↑` / `✓↓`: receita ou despesa realizada;
- `○↑` / `○↓`: receita ou despesa prevista e pendente;
- `!↑` / `!↓`: receita ou despesa prevista, pendente e vencida.

Ao selecionar um dia, a lista mostra descrição, conta, categoria quando disponível, valor em BRL, tipo e situação.
A Agenda não oferece edição, realização ou cancelamento.

## Resumo mensal e precisão monetária

O resumo apresenta separadamente receitas e despesas realizadas, pendentes e vencidas. Esses seis totais vêm no
bloco obrigatório `summary` da resposta da API; o React não soma itens nem calcula saldo ou projeção:

```json
{
  "from": "2026-10-01",
  "through": "2026-10-31",
  "items": [],
  "summary": {
    "realizedIncome": "0.00",
    "realizedExpense": "0.00",
    "pendingIncome": "0.00",
    "pendingExpense": "0.00",
    "overdueIncome": "0.00",
    "overdueExpense": "0.00"
  }
}
```

O resumo respeita exatamente o período inclusivo e os filtros de conta e tipo da consulta. Pendências anteriores a
`from` não são incluídas. `overdueIncome` e `overdueExpense` são subconjuntos de `pendingIncome` e `pendingExpense`,
respectivamente, e não valores adicionais. Uma previsão é vencida somente quando sua data é anterior ao dia civil
atual em `America/Sao_Paulo`; vencimento hoje permanece apenas pendente.

Todos os valores chegam como strings decimais, por exemplo `"9999999999999.99"`, e continuam como texto até a
formatação em BRL. Períodos sem acontecimentos retornam `"0.00"` nos seis campos. Isso evita perda de centavos pela
conversão para `number`.

## Estados da tela

A Agenda representa carregamento, falha de consulta, mês vazio, dia sem acontecimentos e falha ao carregar as contas.
Ao trocar de núcleo familiar, a instância anterior é desmontada, seus pedidos são cancelados e filtros/mês voltam ao
estado inicial da nova família.

## Evolução diária do saldo

A seção **Evolução do saldo** consulta o mesmo mês da Agenda:

```text
GET /api/families/{familyId}/financial-calendar/balances
    ?from=primeiro-dia-do-mês
    &through=último-dia-do-mês
    [&accountId=...]
```

Sem `accountId`, a resposta consolida todas as contas da família. Transferências internas não alteram esse total. Com
uma conta selecionada, entradas e saídas de transferências aparecem separadas e participam do fechamento da conta;
elas nunca são classificadas como receita ou despesa operacional.

`openingBalance` é o saldo no início de `from`, antes dos acontecimentos desse dia. Cada item de `days` contém `date`,
`kind` (`Realized` ou `Projected`), `income`, `expense`, `transferIn`, `transferOut`, `closingBalance`,
`hasOverduePending`, `isNegative`, `chartPosition` e `unavailableReason`. Valores monetários são strings decimais
exatas; `chartPosition` é somente uma coordenada visual de `0` a `1000`, calculada no backend.

A virada de regra ocorre no dia civil atual de `America/Sao_Paulo`:

- antes de hoje, o fechamento é exclusivamente realizado: saldo inicial + lançamentos efetivos + transferências;
- hoje, parte-se do fechamento efetivo de hoje e aplicam-se previsões pendentes de hoje e todas as pendências vencidas;
- depois de hoje, o saldo anterior é acumulado com previsões pendentes da respectiva data;
- as vencidas anteriores a hoje entram uma única vez na base projetada. Elas ativam `hasOverduePending`, mas não são
  atribuídas artificialmente a `income` ou `expense` de hoje;
- previsão realizada aparece somente pelo lançamento efetivo; cancelar esse lançamento reabre a previsão.

O bloco `risk` é calculado pela API e traz `minimumProjectedBalance`, `firstNegativeProjectedDate` e
`endingProjectedBalance`. O React apenas formata esses valores. O gráfico usa traço contínuo no histórico e tracejado
na projeção, destaca pontos negativos e o dia selecionado, e é acompanhado por uma tabela com todos os valores.

Se uma conta necessária não tiver saldo inicial, ou se a base realizada estiver antes da referência, os campos
monetários afetados vêm `null` com `OpeningBalanceNotConfigured` ou `BeforeOpeningBalanceDate`. No consolidado, nenhum
total parcial é apresentado como completo.

Contas, movimentos e previsões são lidos em uma transação `REPEATABLE READ` somente leitura. Os movimentos são
agregados por data no PostgreSQL; não há uma chamada de projeção por dia nem persistência de saldos calculados.
