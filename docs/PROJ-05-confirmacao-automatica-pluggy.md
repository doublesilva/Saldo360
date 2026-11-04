# PROJ-05 — Confirmação automática do valor real pelo banco (Pluggy)

Aplica a [Diretriz de automação](DIRETRIZ-AUTOMACAO.md): o valor que o banco informa para uma despesa fixa vira o valor confirmado da competência **sem ação do usuário**, quando a associação é segura. Reaproveita a PROJ-04 (exceção de competência + `ConfirmedAmount`) e o conciliador de despesas fixas; não há outro motor de cálculo.

## Fluxo

1. Sincronização Pluggy importa e aplica as movimentações (`PluggyPostingEngine`).
2. **`PendingBankConfirmationProcessor`** (novo passo, antes do conciliador): classifica os débitos pendentes contra as obrigações de despesas fixas e grava `ConfirmedAmount` na previsão da competência com origem e transação bancária.
3. `FixedObligationReconciler`: quando a transação de referência é efetivada, quita a obrigação **por vínculo** (evidência mais forte que descrição + valor). Sem despesa nova: a movimentação já existe, só a linha de vínculo é gravada.

Nada disso roda em produção até a próxima sincronização que o usuário autorizar (o código só entra no deploy).

## Regras de associação (`PendingBankMatcher`)

Sempre par **único** nos dois sentidos, na mesma conta.

| Regra | Evidência | Exemplo |
|---|---|---|
| `SafeMatcher` | A correspondência segura já existente (`FixedObligationMatcher`: descrição compatível, janela de datas, valor esperado ou estimado) | "Conta de luz" estimada × "CONTA DE LUZ" |
| `RecurrenceHistory` | A descrição bancária repete a de uma competência anterior **já paga da mesma recorrência**; até 3 dias da data esperada; valor até 5% de diferença; nenhuma outra obrigação plausível disputa o débito; nenhum outro débito com a mesma evidência | Crediário Solar × "SAIDA CREDIARIO AUTOM" |

O matcher existente **não foi flexibilizado**: valor igual ou descrição genérica isolados continuam sem associar. A regra histórica é o aprendizado das exceções: o que o banco escreveu quando a obrigação foi paga é a evidência. Sem histórico, nada é associado.

## Origem, rastreabilidade e proteção

- `financial_planned_transactions`: `amount_confirmation_source` (`Manual`, `PluggyAuto`, `ManualLink`; nulo com valor confirmado = `Manual` da PROJ-04), `amount_source_transaction_id` (referência à transação bancária, sem FK de propósito para o reset do workspace) e `amount_confirmed_by_user_id` nulo na confirmação automática.
- `financial_amount_confirmation_events` (somente acréscimo): `Confirmed`, `Updated` (valor corrigido, valores anterior e novo), `SourceReplaced`, `Reverted` e `Ambiguous` (diagnóstico).
- Confirmação **manual** e **vínculo manual** nunca são sobrescritos nem revertidos por sincronização. O valor-base da recorrência e as outras competências nunca mudam.
- Idempotência: mesmo valor, origem e transação = nenhuma escrita nem evento. Passadas concorrentes se serializam por lock consultivo do workspace + lock da recorrência.

## Ciclo da fonte bancária

| Situação da transação de referência | Efeito |
|---|---|
| Pendente com valor alterado | Atualiza a confirmação automática e registra `Updated` |
| Pendente → efetivada (mesmo id) | Preserva o valor confirmado; o conciliador quita por vínculo |
| Ausente em uma coleta | Nada (ausência temporária) |
| Ausente em ≥ 3 coletas e há ≥ 48 h | Fonte definitivamente invalidada: reverte ao projetado (`Reverted`) |
| Invalidada e há uma efetivada equivalente única (mesma conta, ±3 dias, ±5% do valor) | Troca a referência bancária (`SourceReplaced`; `Updated` se o valor mudou) |

Uma movimentação pendente **informa o valor, não comprova pagamento**: a obrigação segue pendente até o pagamento efetivado.

## Ambiguidade e vínculo manual (exceção)

Débito com candidatos mas sem par seguro **não é associado** (e continua fora do saldo pelo PendingBank). O processador registra um `Ambiguous` (uma vez por situação) com o impacto, os candidatos e o que falta (`NoHistoryEvidence`, `MultipleCandidates`, `DescriptionIncompatible`), para evoluir as regras.

Como último recurso, o usuário pode vincular na Agenda ("Vincular agendamento do banco"): `GET …/recurrences/{id}/competences/{yyyy-MM}/bank-candidates` e `POST …/bank-link` `{ "transactionId" }`. O valor do banco passa a valer na competência **sem segunda confirmação** (`ManualLink`). "Confirmar valor real" fica só para competências sem fonte bancária.

## Fonte única dos cálculos

O valor persistido é lido por todos os consumidores da PROJ-04 (Agenda, orçamento, planejamento de seis meses, saldo projetado, recuperação do limite). `PendingBankCoverage` trata o débito referenciado por uma obrigação como `Covered`: não soma de novo.

## Migration `AddPluggyAmountConfirmation`

Aditiva: duas colunas nulas, tabela `financial_amount_confirmation_events` e dois índices. Recria `ck_financial_planned_transactions_confirmed_triple` (relaxada: a confirmação automática não tem usuário). Nenhuma linha existente é alterada.

## Limitações

- A regra histórica exige uma competência anterior **paga e conciliada** da mesma recorrência. Crediário Solar e ASTTI só serão associados automaticamente se o histórico de outubro tiver pagamento conciliado com a descrição bancária; senão ficam `Ambiguous` (diagnóstico registrado) ou entram pelo vínculo manual. Não foi validado com dados reais de produção.
- Horizonte de 3 meses a partir do mês corrente; recorrências de cartão e as detectadas pelo Pluggy ficam de fora.
- A confirmação automática cria a exceção de competência quando ela não existe; o criador registrado é o dono do workspace (o responsável pela confirmação fica nulo, e a origem `PluggyAuto` é o rastro).
- Não há tela para consultar os diagnósticos `Ambiguous` nem desvincular um vínculo manual (corrige-se vinculando outra transação).
- Os limites de ausência definitiva (3 coletas e 48 h) são conservadores e precisam de ajuste com o comportamento real da Pluggy.
