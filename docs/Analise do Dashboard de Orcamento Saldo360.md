# Análise do Dashboard de Orçamento Saldo360

Parecer financeiro e de experiência do usuário

O dashboard apresenta bem a posição bancária, o uso do cheque especial e o fechamento projetado. A principal oportunidade é transformar esses dados em orientação de decisão: quanto ainda pode ser gasto sem comprometer a recuperação, se os compromissos cabem no caixa da conta selecionada e quais fatores mais pressionam o orçamento.

**Recomendação central:** reorganizar a Visão Geral em uma sequência de contexto, posição atual, compromissos, margem segura e causas. O usuário deve entender a situação em poucos segundos e aprofundar os cálculos apenas quando necessário.

## Diagnóstico executivo

O dashboard é confiável como painel de acompanhamento e projeção, mas ainda é parcial como instrumento de decisão diária. Ele responde com clareza onde o usuário está e para onde tende a ir; responde com menos clareza o que ele pode fazer agora.

- Pontos fortes: competência e conta visíveis, saldo atual, uso do limite, fechamento projetado, compromissos e avisos de qualidade dos dados.

- Lacunas críticas: valor seguro para gastar, cobertura consolidada dos compromissos e explicação dos principais desvios do orçamento.

- Risco de interpretação: valores realizados, comprometidos, previstos e desconhecidos aparecem em áreas diferentes e exigem que o usuário faça a consolidação mentalmente.

## Matriz das perguntas essenciais

| Pergunta do usuário | Situação | Diagnóstico | Sugestão |
| --- | --- | --- | --- |
| Qual é a competência atual | Atende | Mês e estado da competência aparecem no topo. | Manter competência próxima da conta e tornar o contexto fixo durante a rolagem. |
| Qual é a conta atual | Atende | Seletor diferencia conta principal, contas individuais e consolidado. | Exibir sempre o tipo de visão: conta individual ou todas as contas. |
| Quanto tenho de saldo bancário hoje | Atende | Saldo atual inclui data da posição e última atualização bancária. | Usar o rótulo Saldo bancário observado e destacar a data de atualização. |
| Quanto do cheque especial estou utilizando | Atende | Saldo negativo e utilização do limite são coerentes e aparecem na projeção. | Levar Cheque especial usado para a primeira linha de indicadores da Visão Geral. |
| Quanto ainda tenho para pagar neste mês | Parcial | Agenda e faturas apresentam valores, mas o total consolidado exige soma mental. | Criar Compromissos restantes com separação entre faturas, despesas fixas e outros pagamentos, sem duplicidade. |
| Quanto já gastei do meu orçamento | Parcial | Há despesas por competência e metas, mas falta um consumo consolidado contra o orçamento disponível. | Mostrar Realizado, Comprometido, Previsto e Orçamento total na mesma régua. |
| Quanto ainda posso gastar sem comprometer minha recuperação | Não atende | Não existe uma resposta direta e acionável. | Criar Margem segura para gastar, com fórmula explicada, data de referência e nível de confiança. |
| Meu saldo atual será suficiente para cobrir os compromissos dessa conta | Parcial | O fechamento projetado ajuda, mas não mostra cobertura e insuficiência antes dos vencimentos. | Criar Cobertura dos compromissos com valor disponível, entradas esperadas, pagamentos restantes e eventual déficit. |
| Vou fechar o mês positivo ou negativo | Atende | Fechamento projetado responde, com avisos sobre cobertura parcial. | Manter o indicador e apresentar intervalo ou confiança quando houver dados incompletos. |
| Qual é a meta de recuperação inicial | Atende | A meta, seu estado e a data de fixação aparecem na Visão Geral. | Exibir meta, recuperação prevista e diferença para a meta no mesmo componente. |
| O que está prejudicando meu orçamento | Não atende | Os dados existem, mas não há síntese causal ou ranking de impactos. | Criar Principais pressões com variação contra plano, peso no orçamento e explicação do impacto no saldo. |

## Arquitetura recomendada para a Visão Geral

A ordem recomendada acompanha o raciocínio financeiro do usuário. Primeiro ele confirma o contexto. Depois entende a posição atual, verifica o que ainda precisa pagar, descobre a margem segura e identifica o que exige ação.

**1 Contexto fixo**  Competência, conta ou consolidado, data do saldo bancário e data da última sincronização.

**2 Posição atual**  Saldo bancário observado, cheque especial usado, limite disponível e saldo livre fora do limite.

**3 Obrigações do mês**  Total restante, próximos vencimentos e divisão entre faturas, despesas fixas e outros compromissos.

**4 Projeção de fechamento**  Saldo projetado, recuperação prevista, meta de recuperação e diferença para a meta.

**5 Margem segura**  Valor que ainda pode ser gasto sem reduzir a recuperação abaixo da meta ou da reserva mínima.

**6 Orçamento**  Realizado, comprometido, previsto e disponível por categoria e no total.

**7 Principais pressões**  Maiores desvios, categorias acima do esperado, faturas relevantes, entradas incertas e dados incompletos.

**8 Próximas ações**  Atalhos para revisar classificação, ajustar orçamento, resolver ambiguidades e consultar a projeção detalhada.

## Indicadores prioritários

| Indicador | Definição | Apresentação |
| --- | --- | --- |
| Saldo bancário hoje | Saldo observado na conta selecionada | Valor, data e origem bancária |
| Cheque especial usado | Máximo de zero e o inverso do saldo quando negativo | Usado, contratado e disponível |
| Compromissos restantes | Obrigações ainda não liquidadas na competência | Total e composição sem duplicidade |
| Cobertura dos compromissos | Saldo disponível mais entradas confiáveis menos compromissos | Suficiente ou déficit e data crítica |
| Fechamento projetado | Resultado financeiro consolidado calculado pela API | Positivo ou negativo e avisos de cobertura |
| Margem segura para gastar | Excedente sobre o piso de fechamento necessário | Valor, premissas e confiança |
| Consumo do orçamento | Realizado mais comprometido, sem contar pagamento de fatura duas vezes | Percentual, valor e restante |
| Pressões do orçamento | Itens com maior impacto ou desvio contra o plano | Ranking com causa e ação recomendada |

## Regra para a margem segura

A margem segura não deve ser o saldo bancário nem o limite disponível. Ela deve usar o fechamento projetado e preservar o piso definido pela meta de recuperação.

**Margem segura = máximo de zero entre fechamento projetado antes de novos gastos e piso de fechamento**

- O piso de fechamento deve refletir a meta de recuperação já fixada e uma reserva mínima configurada, quando existir.

- Entradas inferidas, faturas provisórias e compromissos ambíguos reduzem a confiança do número e precisam aparecer como premissas.

- Quando a cobertura for insuficiente, mostrar zero como margem segura e o déficit separadamente; nunca apresentar limite bancário como dinheiro disponível.

- O navegador deve apenas exibir o valor retornado pela API; o cálculo financeiro permanece centralizado no servidor.

## Como explicar o que prejudica o orçamento

A seção deve priorizar impacto financeiro e possibilidade de ação. Uma lista extensa de lançamentos não substitui uma explicação causal.

1. Exibir as três a cinco maiores pressões do mês, ordenadas pelo impacto no fechamento projetado.

1. Separar aumento real de gasto, compromisso extraordinário, fatura elevada, receita ausente e dado ainda desconhecido.

1. Comparar cada pressão com orçamento, mês anterior ou média recente, conforme a referência disponível.

1. Oferecer uma ação coerente: revisar classificação, abrir fatura, ajustar teto, resolver ambiguidade ou consultar lançamentos.

**Exemplo de linguagem**  Faturas de cartão concentram R$ X dos pagamentos restantes e reduzem o fechamento projetado em R$ Y. Parte do valor ainda é provisória; abra Cartões para revisar.

## Princípios de apresentação financeira

- Separar saldo bancário, orçamento por competência e projeção. São medidas diferentes e não devem ser combinadas em um único rótulo.

- Distinguir realizado, comprometido, previsto, estimado e desconhecido por texto e não apenas por cor.

- Manter transferência interna neutra no consolidado e evitar contar compra do cartão e pagamento da fatura como duas despesas.

- Nunca transformar valor ausente em zero confirmado. Usar Indisponível, Não informado ou Estimado conforme o caso.

- Mostrar data de referência e origem ao lado de saldos e projeções para reduzir decisões baseadas em dados defasados.

- Usar vermelho para insuficiência ou deterioração, azul para informação e verde somente quando houver melhora financeira comprovada.

## Backlog priorizado

| Prioridade | Entrega | Objetivo | Critério de aceite |
| --- | --- | --- | --- |
| P0 | Margem segura para gastar | Responder diretamente quanto pode ser gasto sem comprometer a recuperação. | Valor calculado pela API, premissas visíveis e zero quando houver déficit. |
| P0 | Compromissos restantes consolidados | Eliminar soma mental entre agenda, faturas e despesas fixas. | Total sem duplicidade e detalhamento por tipo e vencimento. |
| P0 | Cobertura da conta | Informar se saldo e entradas confiáveis cobrem os compromissos. | Suficiente ou déficit, data crítica e conta correspondente. |
| P1 | Consumo consolidado do orçamento | Unificar realizado, comprometido, previsto e disponível. | Régua mensal com valores e percentuais consistentes. |
| P1 | Principais pressões | Explicar o que mais reduz a margem e o fechamento. | Ranking com impacto, referência, confiança e ação. |
| P1 | Contexto fixo | Evitar perda de competência, conta e atualização durante a rolagem. | Barra compacta e responsiva no topo da Visão Geral. |
| P2 | Comparação histórica | Dar contexto para variações relevantes. | Comparar com mês anterior e média recente sem tratar orçamento como economia realizada. |
| P2 | Simulação controlada | Permitir avaliar um gasto antes de registrá-lo. | Simulação local ou de API sem criar lançamento financeiro. |

## Proposta de leitura da primeira dobra

Na primeira tela, antes de qualquer rolagem, o usuário deve ver a competência, a conta e cinco respostas: saldo hoje, limite usado, compromissos restantes, fechamento projetado e margem segura.

| Faixa | Conteúdo |
| --- | --- |
| Contexto | Novembro de 2026  \|  Itaú PF  \|  saldo atualizado em 03/11 às 19:36 |
| Posição | Saldo bancário  \|  Cheque especial usado  \|  Limite disponível |
| Mês | Compromissos restantes  \|  Fechamento projetado  \|  Meta de recuperação |
| Decisão | Margem segura para gastar  \|  Cobertura suficiente ou déficit |
| Explicação | Principais pressões  \|  Avisos de qualidade  \|  Próximas ações |

## Conclusão

O dashboard já oferece uma base financeira consistente e preserva distinções importantes entre saldo, orçamento, faturas e projeção. A evolução recomendada não exige aumentar a quantidade de informação, mas consolidá-la em respostas orientadas à decisão.

A prioridade deve ser responder, de forma explícita e auditável, quanto ainda falta pagar, se a conta cobre os compromissos, quanto pode ser gasto com segurança e quais fatores explicam a pressão sobre o orçamento. Com essas respostas na primeira dobra, a Visão Geral passa de painel de acompanhamento para instrumento diário de gestão financeira.
