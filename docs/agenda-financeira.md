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

O resumo apresenta receitas e despesas realizadas, receitas e despesas previstas pendentes e, quando existirem,
pendências vencidas. Esses totais vêm no bloco `summary` da resposta da API; o React não soma itens nem calcula saldo
ou projeção.

Todos os valores chegam como strings decimais, por exemplo `"9999999999999.99"`, e continuam como texto até a
formatação em BRL. Isso evita perda de centavos pela conversão para `number`.

Enquanto a API não fornecer `summary`, o calendário e os compromissos continuam disponíveis e a tela informa que o
resumo mensal está indisponível. Nenhum total é inventado no navegador.

## Estados da tela

A Agenda representa carregamento, falha de consulta, mês vazio, dia sem acontecimentos e falha ao carregar as contas.
Ao trocar de núcleo familiar, a instância anterior é desmontada, seus pedidos são cancelados e filtros/mês voltam ao
estado inicial da nova família.
