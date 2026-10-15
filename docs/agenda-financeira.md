# Agenda Financeira

A Agenda Financeira reúne, em uma visão mensal, os lançamentos realizados e as previsões que ainda
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

## Detalhes e ações dos compromissos

**Ver detalhes** abre um painel lateral com descrição, valor, tipo, data, conta, categoria quando disponível, origem e
situação. O foco vai para o painel e retorna ao botão que o abriu ao fechar; `Esc` também fecha. Contas e categorias
inativas permanecem identificadas. Identificador de parcela/recorrência e observações só serão mostrados quando esses
campos existirem no contrato — a Agenda não cria propriedades ausentes.

As ações reutilizam os mesmos endpoints e formulários das seções de Lançamentos e Previsões:

- previsão pendente ou vencida: **Realizar**, **Editar** e **Cancelar previsão**;
- lançamento realizado avulso: **Editar** e **Cancelar lançamento**;
- lançamento que realiza uma previsão: somente **Cancelar lançamento**, pois esse lançamento não é editável no fluxo
  existente; o cancelamento reabre a previsão, que pode então ser realizada novamente.

Na realização, o valor efetivo e a data efetiva podem diferir do previsto. A previsão conserva seus dados originais e
é substituída na Agenda pelo lançamento efetivo, sem duplicidade. Cancelamentos exigem confirmação com descrição,
valor e consequência: o registro é preservado como cancelado e deixa de participar do saldo/projeção; no lançamento
vinculado, a confirmação informa explicitamente que a previsão voltará a ficar pendente.

Depois de uma operação confirmada pelo backend, a Agenda consulta novamente os acontecimentos, os seis totais do
resumo, a evolução diária e os indicadores de risco, preservando mês, conta, tipo e dia selecionado. Não há atualização
financeira otimista. Respostas de consultas anteriores são descartadas; se uma ação falhar, o painel e os dados já
exibidos permanecem disponíveis com uma mensagem de erro.

Transferências não aparecem como acontecimentos individuais no contrato atual da Agenda e, por isso, não recebem
ações nesse painel.

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

## Planejamento dos próximos seis meses

A opção **Próximos meses** mantém a Agenda mensal disponível e apresenta, inicialmente, seis competências consecutivas
a partir do mês atual em `America/Sao_Paulo`. O filtro de conta é compartilhado. Selecionar uma competência retorna à
visão mensal e abre seu calendário.

```text
GET /api/families/{familyId}/financial-calendar/monthly-projection
    ?from=yyyy-MM
    &months=6
    [&accountId=...]
```

`from` é obrigatório e estritamente `yyyy-MM`. `months` assume `6` quando omitido e aceita de 1 a 12. Sem `accountId`,
o resultado consolida todas as contas da família. Cada item de `months` contém `month`, receitas e despesas realizadas,
pendentes e vencidas, `openingBalance`, `closingBalance`, `minimumProjectedBalance`,
`firstNegativeProjectedDate` e `unavailableReason`. Dinheiro continua sendo texto decimal exato ou `null` quando o
saldo não pode ser calculado.

Os totais de acontecimentos pertencem exclusivamente à competência. Vencidas são subconjuntos das pendentes. Uma
pendência anterior a `from` não aparece artificialmente no primeiro mês, mas influencia uma projeção atual/futura uma
única vez. Previsões realizadas aparecem pelo lançamento efetivo; cancelá-lo reabre a previsão. Transferências não são
receita/despesa: afetam a conta individual e são neutras no consolidado familiar.

Competências inteiramente passadas mostram fechamento realizado e não recebem indicador de projeção futura. No mês
atual e nos futuros, fechamento, menor saldo e primeira data negativa vêm do mesmo cálculo diário da seção anterior.
Consequentemente, o fechamento mensal coincide com o último dia retornado por `/balances` para o mesmo filtro e estado
dos dados. Se qualquer conta necessária estiver indisponível, os saldos da competência são `null`; nenhum consolidado
parcial é apresentado como completo.

Contas, fluxos para saldo e totais do intervalo são consultados de uma vez em um único snapshot `REPEATABLE READ`
somente leitura. Há agregação diária no PostgreSQL e divisão por competência em memória, sem consultas N+1, sem uma
consulta por mês/dia e sem persistência de projeções. O React somente formata e destaca os valores recebidos.
## Situação do orçamento

A visão mensal consulta o orçamento ativo da mesma competência pelo endpoint de execução (`GET /api/families/{familyId}/budgets/{budgetId}/execution`). Planejado, realizado, comprometido, consumo projetado, disponibilidade, excedente, status e contagens de alertas são apresentados exatamente como retornados pela API; a Agenda não recalcula valores financeiros.

O orçamento é familiar e independente dos filtros de conta e tipo da Agenda. A lista compacta mostra somente metas `ApproachingLimit`, `LimitReached` e `Exceeded`, em ordem de severidade e nome. Sem orçamento, a Agenda informa `Nenhum orçamento definido para esta competência.` e oferece navegação para Orçamentos sem criar registros. A falha dessa consulta permanece isolada da agenda e do fluxo de caixa.

As ações financeiras da Agenda incrementam a sincronização da família: acontecimentos, resumo, evolução do saldo, risco e execução orçamentária são consultados novamente, sem atualizações otimistas. A navegação para Orçamentos e o retorno para a Agenda preservam família e competência.
# Obrigações de cartões (T09.5)

Faturas podem ser consultadas como obrigações consolidadas, com total, valor pago, saldo devido e situação financeira. A Agenda identifica lançamentos efetivos de pagamento pela origem `CreditCardInvoicePayment`; parcelas não são duplicadas como saídas bancárias.

Na evolução diária e mensal, o saldo devido é aplicado no vencimento da fatura. Os contratos expõem `creditCardProjectionComplete` e `creditCardOutstanding` para indicar cobertura e obrigações não alocadas.

A auditoria T09.7 confirmou que pagamentos aparecem somente como lançamentos efetivos de origem própria; parcelas e faturas não são adicionadas como saídas efetivas na Agenda. Obrigações sem conta preferencial permanecem sinalizadas como não alocadas. Detalhes e cenários estão em [docs/T09.7-auditoria-integracao.md](T09.7-auditoria-integracao.md).
