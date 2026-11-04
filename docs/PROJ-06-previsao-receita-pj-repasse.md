# PROJ-06 — Receita PJ inferida e repasse PJ → PF projetado

Aplica a [Diretriz de automação](DIRETRIZ-AUTOMACAO.md) sobre a [PROJ-01](PROJ-01-projecao-integrada.md): a projeção de seis meses funciona sem configuração manual e o repasse PJ → PF entra sozinho no saldo da PF e na recuperação do limite. **É só planejamento**: nenhuma transferência bancária, lançamento ou previsão é criado, e nada é gravado em leitura.

## O que já existia (PROJ-01) e foi reaproveitado

Receita por horas (valor/hora × horas/dia × dias úteis de São Paulo menos ausências), competência trabalhada × recebimento (deslocamento e dia configuráveis), reserva de tributos (%), reserva operacional, substituição pela receita realizada (≥ 50% do esperado), transferências previstas (neutras no consolidado), seis meses mensais, evolução diária e indicadores de cobertura. Os parâmetros (por exemplo R$ 100/h, 8 h, dia 10 do mês seguinte, 11%) continuam vindo da **configuração existente** (`PUT /monthly-plan/income-forecast`): nada foi fixado no código.

## Lacuna 1 — Receita inferida (sem configuração)

`IncomeInference` + `IncomeForecastLoader`. Só quando **não há configuração** (com configuração, ela prevalece, inclusive ajustes por competência):

- **Base:** mediana do faturamento operacional mensal da conta PJ nos últimos seis meses completos; exige ao menos **3 meses válidos** (senão não infere nada). Dia de recebimento = mediana do dia do maior recebimento de cada mês.
- **Excluídos:** rendimentos de aplicação, resgates, estornos, devoluções, juros, cashback, IOF e reembolsos (por descrição), devoluções bancárias confirmadas (origem), e entradas que espelham uma **saída de mesmo valor, até 3 dias, em outra conta** do mesmo orçamento (transferência interna).
- **Sinalização:** `integrated.incomeInferred`, `income.inferred`, hora/valor-hora nulos (nunca inventados) e os motivos de cobertura `IncomeForecastInferred` e `TaxReserveNotConfigured` (tributo não é inferido: reserva 0%), mantendo o mês **parcial**. Receita inferida nunca é apresentada como garantida.
- A receita realizada continua substituindo a projetada (mesma regra e mesma competência), sem duplicar.

## Lacuna 2 — Repasse PJ → PF projetado (`PjTransferPlanner`, `PjTransferProjection`)

Em **hoje** e em cada **data de recebimento previsto**, projeta **100%** do que a PJ pode ceder: o menor saldo projetado da PJ daqui até o fim do horizonte (mês atual + 5), menos a reserva de tributos (do mês e do anterior, quando não há obrigação tributária cadastrada) e a reserva operacional, em centavos inteiros (truncados).

- O saldo da PJ já reflete recebimentos, despesas pendentes, repasses **previstos** e os repasses projetados anteriores: nada é subtraído duas vezes, os compromissos até o próximo recebimento (e depois dele) estão cobertos, e **o repasse nunca consome reserva nem deixa a PJ negativa**.
- Um repasse **previsto** (ou já realizado) reduz o automático na mesma medida: a mesma disponibilidade nunca vira duas transferências.
- Saída na PJ e entrada na PF vêm do mesmo plano para o saldo diário e o mensal da Agenda, o planejamento de seis meses e a recuperação do limite; no consolidado o repasse é neutro e nunca é receita nem despesa.
- Horizonte fixo, independente do período consultado: a mesma data tem o mesmo repasse em qualquer tela.
- Sem saldo conhecido em algum dia (por exemplo, sem saldo inicial), não projeta repasse.
- `transfers.autoProjectedPjToPf` e `transfers.dependsOnInferredIncome` por mês; `TransferDependsOnInferredIncome` na cobertura. O "repasse sugerido" da PROJ-01 passa a ser o saldo residual depois do repasse projetado.

## Efeitos em testes e comportamento

Os testes da PROJ-01 que fixavam o saldo da PF com um repasse previsto, a reserva de tributos e a identidade do saldo da PJ foram atualizados: agora o repasse previsto substitui parte do automático (saldos inalterados) e o saldo antes do repasse é que independe da reserva. Mesma intenção, nova regra aprovada.

## Limitações

- A inferência depende de histórico de ao menos 3 meses e de descrições previsíveis; entradas não operacionais com descrição incomum podem entrar na mediana (ex.: a espelhada só é excluída se a saída existir no próprio orçamento).
- Sem configuração, o tributo não é reservado: a disponibilidade da PJ é otimista até o usuário configurar o percentual (sinalizado).
- A PF usada é a conta PF ativa primária (ou a primeira); a PJ, a conta de recebimento configurada ou a PJ com mais recebimentos operacionais.
- O repasse de "hoje" assume que a PJ cederia seu excedente imediatamente: é uma suposição de planejamento, não uma transferência.
- Em produção hoje não há configuração nem repasses previstos: a primeira leitura passa a mostrar receita e repasse inferidos, todos sinalizados. A configuração do usuário (R$ 100/h, 8 h, dia 10, 11%) só é aplicada por ele ou com autorização.
