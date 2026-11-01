# Equalização dos testes e meta de 5 minutos no step de testes

Data da medição: 31/10/2026, máquina local (Windows, Docker/Testcontainers), build Debug. Os números do runner do GitHub vêm da API da CI (`gh run list/view`).

## 1. Causa dos cancelamentos por timeout (hang × lentidão)

**Conclusão: lentidão, não travamento.** Evidências:

- Execução COMPLETA local de `7c691c0` com `--blame-hang --blame-hang-timeout 300s` e TRX: **1.511 passaram, 0 falhas, 23 min 38 s**. O detector de travamento não disparou; o teste mais lento levou 11,4 s.
- As classes novas/alteradas pelas etapas 2 e 3 (planejadas, extrato, perspectiva) custam 3 a 6 s cada, como as demais de integração. Um conjunto de 127 testes de Visão Geral/Agenda/Contas levou 2 min 31 s em `323135a` (baseline que passou na CI) e 2 min 35 s em `7c691c0`: não houve regressão de desempenho nos testes existentes.
- Histórico da CI no mesmo dia (mesmo job `backend`): 20 min 19 s de teste em `323135a`; execuções completas totais entre ~20 e ~29 min em outros commits. A suíte já operava com variância de 20–29 min contra o limite de 30; as duas execuções de `7c691c0` passaram de 29 min por pouco (passo de teste 22:57:06 → 23:26:08, cancelado pelo limite do job).
- Conclusão: o FULL serial estava no limite; ~10 testes novos (≈40 s) mais a variância do runner o empurraram além de 30 min. Nenhum teste novo trava (todos passam isoladamente e dentro da suíte).

## 2. Medição (FULL, `7c691c0`)

| Item | Valor |
| --- | --- |
| Testes (métodos Fact/Theory) | 1.085 métodos → 1.511 casos expandidos |
| Soma das durações dos casos | 1.733 s (28,9 min) |
| Tempo de parede local | 23 min 38 s |
| Contêiner PostgreSQL | já compartilhado (um por coleção `PostgresCollection`, banco por teste clonado de um template migrado uma vez) |
| Custo fixo por teste de integração | mediana ≈ 3 s (host da API + banco clonado + login/orçamento + sincronização simulada) |
| Execução da coleção PostgreSQL | **serial**: todas as classes estão na mesma coleção do xUnit, portanto não rodam em paralelo entre si |

Maiores classes (soma de segundos, casos, s/caso): `PluggyFinancialIntegrationTests` 502/134/3,7; `PluggyCategorizationIntegrationTests` 84/20; `PluggySyncEndpointsTests` 82/26; `MonthlyPlanTests` 75/44; `PluggyAutoSyncTests` 62/18; `PluggyAutomationCenterTests` 59/17; `FinancialAccountEndpointsTests` 53/49; `FinancialTransactionEndpointsTests` 50/37; `PlanActualDashboardTests` 43/21.

## 3. Decisão: fatiar o FULL, sem remover testes

Meta do usuário: o step de testes do backend em no máximo 5 minutos. Remover testes até chegar lá exigiria apagar ~75% da suíte (23,6 → 5 min), o que viola a regra de proteger as regras de negócio financeiras. A ordem aprovada (infraestrutura sem perda → consolidação → remoção) foi aplicada assim:

1. **Infraestrutura sem perda (aplicada):** o FULL deixou de ser um único processo serial e passou a rodar em **8 fatias paralelas** (matriz do job `backend`), balanceadas pela duração medida. Cada fatia executa um subconjunto disjunto do MESMO conjunto completo; juntas executam os 1.507 testes únicos (1.511 casos expandidos).
2. **Consolidação/remoção: nenhuma neste momento.** Após o fatiamento nenhuma fatia passa de ~3,6 min locais. Sem excesso a eliminar, não há justificativa para tirar proteção: uma auditoria anterior (T-CI.1) já removeu 42 casos redundantes. Candidatos de baixo valor identificados para uma próxima rodada, se o limite voltar a apertar: testes de contagem de inventário de reset (`FinancialResetEndpointsTests` conta de tabelas), repetições de migrations em banco vazio, e Facts de endpoint que repetem regra provada em teste de serviço. Todos ficam como `REVISÃO MANUAL`.

### Como funciona

- `scripts/ci/make-test-shards.py <trx> [N]` gera `scripts/ci/test-shards.json` (balanceamento guloso por duração; a classe gigante `PluggyFinancialIntegrationTests` é dividida por método).
- `scripts/ci/test-shard-filter.py N` imprime o filtro do `dotnet test --filter` da fatia N. A última fatia roda as suas unidades **e** a negação de tudo que está listado: **teste novo ou renomeado nunca fica sem fatia**.
- `ci.yml`: job `backend` com `strategy.matrix.shard` (8 fatias no FULL; 1 fatia fora do FULL, que roda o perfil FAST como antes), `fail-fast: false`, timeout do job 15 min e do step de teste 8 min (teto de segurança; a meta é 5 min), TRX e artefato por fatia. `ci-success` e `ci-full-success` continuam exigindo `backend` com sucesso (resultado agregado das fatias): **a suíte completa e a CI continuam requisito antes do deploy**.
- `CiShardContractTests` verifica que as fatias são disjuntas, que a última recolhe o não listado e que a matriz do workflow tem o número de fatias do arquivo.

### Medição por fatia (local)

| Fatia | Soma prevista (s) | Medido |
| --- | --- | --- |
| 1 a 8 | 217 cada | fatia 8: **218 casos em 3 min 18 s** (inclui subir o contêiner) |

Fator de segurança: a CI executou o FULL serial em 20 min 19 s contra 23 min 38 s locais (o runner foi ~14% mais rápido) e variou até ~29 min. Considerando variância de até 1,4×, uma fatia de ~3,3 min locais fica em ~4,6 min no pior caso: dentro dos 5 min. O teto do step (8 min) cobre degradação extra sem mascarar travamentos. Se uma fatia estourar 5 min de forma recorrente, regenerar o arquivo com um TRX novo ou subir para 10 fatias.

### Custo e riscos residuais

- Mais jobs paralelos (8 runners) e ~2 min de restore/build repetidos por fatia: o relógio da parede cai de ~20–29 min para ~6 min (3,3 min de teste + ~2,5 min de preparação), com mais minutos de runner.
- O nome do check fica `backend (1)` … `backend (8)`; a proteção de branch deve exigir `ci-success` (que já agrega o resultado). Se a proteção exige o check `backend`, é preciso atualizá-la.
- O balanceamento envelhece; a última fatia absorve testes novos, então ela cresce primeiro. Regerar o arquivo periodicamente.
- O step de testes do **cliente** (vitest, 512 testes) já roda em poucos segundos a ~1 min e não precisa de ajuste.

## 4. Antes × depois (contagem)

| | Antes | Depois |
| --- | --- | --- |
| Métodos Fact/Theory | 1.085 | 1.086 (+1 contrato de fatias; nenhum removido) |
| Casos expandidos | 1.511 | 1.512 |
| Testes removidos/fundidos | n/a | 0 (justificado acima) |
| Tempo do step de teste na CI | ~20–29 min (serial) | ≈3,3 min por fatia, 8 em paralelo (estimado; medir na primeira execução da CI) |
