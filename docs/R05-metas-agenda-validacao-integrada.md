# R05 — Metas, Integração com a Agenda e Validação do Ciclo Completo

Escopo local: nada foi publicado, implantado ou aplicado ao laboratório. O documento só traz contagens e nomes; nenhum valor, identificador, token ou número de conta reais.

**Referência "Folio":** o material do Folio não está no repositório nem foi fornecido na sessão; a apresentação dos objetivos seguiu os requisitos desta etapa (valores junto do percentual, estados em texto, sem conclusões sem regra). Se houver capturas do Folio, a aderência visual pode ser revista como ajuste não bloqueante.

## 1. Mudanças realizadas

### Objetivos financeiros (Metas)

- Cada objetivo vira um cartão com **nome, estado em texto** (Ativo, Pausado, Concluído), **alvo, alocado, restante, percentual com barra** e **prazo**; o detalhe abre pelo nome ou por "Ver detalhe". Percentual sempre vem acompanhado dos valores (`aria-valuetext`: "25%: R$ 250,00 de R$ 1.000,00").
- **Estados tratados**: sem prazo ("Sem prazo definido"), prazo vencido (só quando o objetivo está aberto e o alvo não foi atingido; "Prazo vencido em dd/mm/aaaa (há N dias)"), concluído (prazo passado não é atraso), pausado (novas alocações bloqueadas, valores já alocados continuam contando) e **alocado acima do alvo** ("Alocado acima do alvo", percentual real como 120%; a barra desenha até 100%, o texto não). Progresso que não carregou aparece como "indisponível", nunca como zero. Nada depende só de cor.
- **Não há "em dia" nem "em risco"**: não existe regra que sustente isso. Também não há rentabilidade presumida, previsão de conclusão nem aporte sugerido (backlog).
- O **detalhe lista as alocações que explicam o progresso** (aporte/retirada, conta pelo nome, data, ativo ou cancelado) e diz: Alocado = aportes ativos − retiradas ativas. Alocações canceladas ficam visíveis, fora da conta.
- Textos fixos de domínio: objetivo é **acumulação**, diferente do limite do Orçamento; alocar **não cria receita, despesa nem transferência, não altera o saldo bancário** e o valor alocado está **dentro** do saldo realizado (alocado + não alocado = saldo realizado). Cobertura incompleta do saldo é dita, não lida como zero. O resumo mostra o nome da conta (antes mostrava o identificador).
- Formulário de criação ganhou o **prazo opcional** (a API já aceitava `targetDate`).
- **Visão Geral → Metas**: mostra alocado de alvo, **quanto falta** (novo campo `remaining` do servidor, nada calculado no navegador), prazo vencido, "Sem prazo definido" e "Alocado acima do alvo"; clicar no objetivo abre **aquele objetivo** na página de Metas (contexto preservado).

### Agenda como calendário único e compromissos de cartão

- A Agenda e o saldo projetado já tratavam a fatura como obrigação de caixa pelo vencimento (uma por fatura, pagamento reduz o restante). Faltava **mostrar** isso: a Agenda ganhou o bloco **"Faturas de cartão do mês"** (mesma projeção de Cartões, sem novo cálculo): cartão, vencimento, **falta pagar**, **confirmada pelo banco × estimada (provisória) × aguardando a fatura**, pago de obrigação quando houve pagamento parcial, destaque do dia selecionado, filtro pela conta pagadora e aviso de cobertura parcial.
- A Visão Geral → Próximos compromissos ganhou o grupo **"Faturas de cartão"** (nova propriedade `commitments.cardPayments`, calculada a partir de `invoice-overview`): próxima fatura de cada cartão ativo, restante a pagar, status e dias para o vencimento, mais o total já **vencido e não pago**. A nota explica que as compras individuais **não** são saídas bancárias.
- Preservado: **Orçamento = consumo das compras/parcelas; Agenda e saldo = obrigação e liquidação da fatura.** Nenhum segundo calendário ou motor financeiro foi criado.
- Navegação: "Ver conexões" e "Revisar classificação" (R04) levam à Automação; faturas levam a Cartões; objetivos, às Metas; a Agenda pode abrir Cartões.

### Classificação de despesas de cartão (verificação essencial)

A capacidade **já existia completa** e agora está provada de ponta a ponta (jornada C/D): compra importada pode ser marcada **Fixa/Variável** (`PUT .../purchases/{id}/expense-nature`) e ter a **categoria** corrigida (`.../category`, R04); as duas escolhas **sobrevivem à nova sincronização**, entram na meta de **despesas fixas** e na meta da categoria, e o alcance é dito na tela ("Esta compra (as parcelas herdam o tipo)"). Nada é inferido por ser parcelada ou de certa categoria. Encargos e estornos continuam **explicitamente sem tipo** (`unclassifiableCount`), nunca com valor arbitrário.

### Ajuste de API

- Pedir categorização com IA quando o agente está indisponível nunca foi erro do servidor (a etapa local roda e a resposta diz `aiStoppedBecause: AiUnavailable`); o caminho em que a categorização falha por indisponibilidade da IA agora devolve **409** com código `AiUnavailable`, não 500.

## 2. Fonte dos valores e regras entre módulos

| O quê | Fonte | Regra |
| --- | --- | --- |
| Alocado / restante / % do objetivo | `financial_goal_allocations` ativas: aportes − retiradas | Restante nunca negativo; % sem teto; aporte que passe do alvo é recusado (409); reduzir o alvo depois pode deixar o objetivo acima dele |
| Saldo não alocado | saldo realizado da conta − alocado | Alocar não gera lançamento, transferência nem muda o saldo |
| Fatura como caixa | `CardProjectionService` (única fonte de Cartões, Agenda, saldo projetado e Visão Geral) | Obrigação por vencimento; pagamento reduz o restante; fatura fechada = total do banco, aberta/futura = projeção provisória |
| Consumo do cartão no orçamento | `BudgetFactReader` (parcelas pela competência = mês do vencimento da fatura) | Pagamento de fatura não é consumo; uma despesa = um fato, vista por categoria, fixas e todos os cartões, sem soma global |
| Previsão realizada | `planned_transactions` + `financial_transactions` | O realizado substitui o pendente (nunca os dois ativos); realizar de novo é idempotente |
| Pendências de classificação | `pluggy_postings` (categoria) e fatos do orçamento (tipo) | O atalho e a lista de revisão usam a mesma fonte, com a mesma contagem |

## 3. Jornadas executadas (simulado, com aplicação e persistência reais)

Provedor Pluggy e IA simulados; API real sobre PostgreSQL real; dados sintéticos. Testes: `R05IntegratedCycleTests` (2) e `PluggyFinancialIntegrationTests.R05Journey` (3).

| Cenário | Resultado |
| --- | --- |
| **A** sincronizar receita, despesa e transferência; repetir | 3 lançamentos e 1 transferência, 5 importados (uma vez), sem duplicidade em duas repetições; receitas 3000,00 e despesas 135,50: a transferência interna não infla nada |
| **B** pendência, correção manual, nova sincronização | 1 pendência com a mesma contagem na Visão Geral e na lista de revisão; categoria e tipo corrigidos sobrevivem a sincronização (com valor alterado no banco) e a categorização forçada; pendência vai a 0 |
| **C** despesa fixa no cartão | mesma despesa: 150,00 nas fixas e na categoria, uma só linha em "Todos os cartões", uma só compra no banco; resumo conjunto considera só categorias (não soma perspectivas) |
| **D** parcelada, fatura e pagamento | só a parcela da competência no consumo; fatura 500,00 vira pago 200,00 / restante 300,00; **consumo do orçamento inalterado**; dashboard mostra 200,00 a menos a pagar; um só pagamento e uma só saída bancária após sincronizar duas vezes |
| **E** previsão e recorrência | ocorrência pendente na Agenda e no "previsto adicional" (380,00); realizada por 400,00: pendente some, consumido 400,00, disponível 100,00, saldo muda, repetir é idempotente |
| **F** objetivo | aporte 600 e retirada 100: progresso 500,00 (50%) igual na página e na Visão Geral; nenhuma movimentação nem transferência criada; saldo realizado e Agenda inalterados; aporte além do saldo não alocado é 409; cancelar a retirada recalcula (600,00); reduzir o alvo deixa acima dele (120%, restante 0,00) |
| **G** falhas e incompletos | sincronização com falha parcial aparece como "Concluída com pendências"; IA indisponível: etapa local e motivo dito; fixas com despesa sem tipo ficam em 0,00 consumido e sinalizadas; saldo indisponível vem sem valor e com motivo |

## 4. Migrations e configuração

- A suíte aplica **toda a sequência de migrações** em PostgreSQL descartável e a aplicação inicia e opera sobre ela (todos os testes de API). Nada foi aplicado ao laboratório.
- **`Down` de `AddPluggyAutomationCenter` exercitado** em banco descartável (`PluggyAutomationCenterMigrationTests`): remove só `pluggy_automation_settings` e `pluggy_categorization_runs` (os dados dessas duas tabelas são descartados; nada anterior é tocado), `Up` as recria; as restrições valem (consentimento ligado à preferência de IA, intervalo 6/12/24, uma categorização em andamento por orçamento). Não se testou preservar dados antigos, como pedido.
- **Configuração `AiAgent`**: sem dependência da seção `CreditCardStatementAgent` (apenas o script de CI exige sua ausência e documentos históricos a citam). **Nenhuma automação nasce habilitada**: agendador desligado por padrão, preferência de categorização após a sincronização desligada e sem consentimento, IA desligada.

## 5. Evidências visuais

Script `pw/shots_r05.mjs`, Chromium próprio, API interceptada com **fixtures sintéticas** (nenhum dado real). Percurso Visão Geral → Lançamentos → Cartões → Orçamento → Agenda → Metas → Automação em 1440×900, 768×1024, 390×844 e 360×800.

- Sem rolagem horizontal da página em nenhuma tela e resolução; nada fora da viewport além das tabelas dentro de contêiner rolável (já existentes); sem texto cortado (nomes e valores extremos quebram linha).
- Menu móvel: o foco entra no menu ao abrir (768, 390 e 360); fechar com Esc e devolver o foco são cobertos pelos testes do `AppShell`.
- Contexto: da Visão Geral abre o detalhe do objetivo escolhido nas quatro resoluções; Tab segue para o formulário de alocação.
- Estados vazio/erro/carregamento: Lançamentos, Cartões e Orçamento revisados em estado vazio; carregamento e erro cobertos por testes unitários.
- Limitação: lançamentos, cartões e orçamento foram revistos vazios nesta passada; seus estados ricos foram revistos nas etapas R01–R03.

## 6. Testes

- Backend: R05 — 5 jornadas + 1 de migração (`Down`/`Up` e restrições); regressão proporcional filtrada (Dashboard, Visão Geral, Agenda, Metas, Cartões/projeção, Pluggy, Orçamento, Plano mensal, Reset, previsões, recorrências, implantação) — **730 testes passaram, 0 falhas** (11 min 50 s). Suíte completa não repetida: só foram tocados o serviço da Visão Geral (novo campo e novo grupo), um endpoint e arquivos de teste.
- Frontend: 472 testes verdes (novos: apresentação de objetivos 7, metas e faturas na Visão Geral 2, nav; mocks da Agenda ajustados à nova leitura), `tsc -b`, build e `oxlint` sem novos avisos (um aviso de dependência de efeito em `FinancialGoals`, do mesmo padrão do código anterior).
- Distinção: **provedor simulado com aplicação e persistência reais** (jornadas e migração); **revisão visual com fixtures** (Playwright); **integração externa real: nenhuma**.

## 7. Pendências de homologação real (separadas)

Passos operacionais, com escopo explícito e autorização, no laboratório `saldo360_pluggy_lab`:

1. Backup e aplicação das migrações pendentes (R02, R03, R04) ao laboratório, uma a uma, conferindo o esquema.
2. Conexão Pluggy real: sincronização manual e agendada (se ligada), conferindo faturas confirmadas × estimadas e a nova lista de faturas na Agenda e na Visão Geral com dados reais.
3. Revisão visual com dados reais (login real no navegador do laboratório).
4. Uso pago de IA: só com a variável do agente habilitada no laboratório e confirmação explícita na tela; validar a categorização após a sincronização (consentimento) e os limites por execução.
5. Renomear as variáveis provisionadas do agente (`AI_AGENT_ENABLED/MODEL`) antes de qualquer implantação.

## 8. Backlog não bloqueante

- Previsão de conclusão, aporte sugerido e rentabilidade dos objetivos (fora do escopo por decisão).
- Editar nome, alvo e prazo de um objetivo existente na interface (a API existe).
- Regra explícita de "em dia/em risco" para objetivos, se definida.
- Faturas de cartão como itens clicáveis dentro do dia da Agenda (hoje listadas no bloco do mês).
- Revisão visual com estados ricos de Lançamentos, Cartões e Orçamento na passada integrada.
- Sincronizar todas as conexões de uma vez; pausa por conexão (R04).
