# Entrega 3 — Interface da projeção financeira

Consome `GET monthly-plan/projection` (Entrega 2) sem novas regras no backend e sem cálculo financeiro no React. Tipos em `client/src/api/projection.ts`, espelhando `ProjectionModels.cs`.

## O que mudou na interface

- **Planejamento → Projeção financeira** (nova aba principal): competência inicial, horizonte (6, 13, 24, 36), cenário (Atual, Tetos ou comparação) e mês em detalhe. Padrão: competência corrente, 6 meses, comparação.
- A visão anterior continua em **Seis meses (detalhado)** e os cenários manuais em **Cenários configuráveis**, com aviso de que não são os cenários automáticos Atual e Tetos. O editor de previsão de receita PJ também aparece na nova aba.
- Indicadores por cenário, gráfico de saldo PF (`pf.closing`, linha de zero, meses sem saldo ficam sem ponto), gráfico de uso do cheque especial (`overdraft.usedEnd`, ponto vazado quando a utilização aumenta), tabela de todos os meses e detalhamento do mês (Receita PJ, Orçamento PF, Fluxo de caixa, Cheque especial).
- Qualidade dos dados: códigos traduzidos, agrupados por aviso, com as competências e o cenário em que ocorrem. "Projeção parcial" só aparece para avisos que tornam a projeção parcial.
- **Provisão de alimentação**: valor padrão, categorias por identificador, ajuste por competência e restauração. Nada é gravado ao carregar; depois de salvar a projeção é recarregada.
- **Visão Geral**: card "Recuperação do limite projetada" (abertura de referência, utilização e recuperação acumulada por cenário no fim do horizonte) com atalho para a aba.

## Limitações conhecidas

- A API devolve a provisão de alimentação já somada ao complemento de consumo dos cartões (`cardConsumptionComplement`); no cenário Atual esse campo é só a provisão, nos Tetos inclui o complemento até o teto. A interface rotula assim e não separa os dois.
- Compras de cartão pendentes sem categoria não contam como alimentação reconhecida (`FoodKnownFromCategorizedPurchasesOnly`).

## Backlog (fora do escopo desta entrega)

- Separar provisão de alimentação e complemento de teto no contrato da API.
- Seletor de conta/escopo na projeção (hoje é a conta PF do orçamento).
- Navegação do detalhe de um mês para a Agenda.
