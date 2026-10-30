# R04 — Central de Automação Pluggy + Agente de IA

Escopo local: nada foi publicado, implantado ou aplicado ao banco do laboratório. Este documento só traz contagens e nomes de tabelas/rotas; nenhum valor, identificador, token ou número de conta reais.

## 1. Estrutura da central

Item de menu **Automação** (grupo Finanças), exibido só quando a API habilita a Pluggy para o usuário. Substitui o antigo item "Laboratório Pluggy". Três abas:

| Aba | Conteúdo |
| --- | --- |
| **Conexões** | Instituição, estado informado pela Pluggy, contas e cartões vinculados, **cadastrada em** × **última atualização pela instituição** × **última sincronização concluída**, autorização necessária (texto), cadastro do Item, sincronização por conexão ("Sincronizar agora", últimas execuções) e, recolhido, o diagnóstico técnico da Pluggy. |
| **Automações** | Cartão **Sincronização bancária**, cartão **Categorização com IA** e a lista **Revisar classificação**. |
| **Histórico** | Execuções reais, paginadas, com filtros por automação, resultado e período. |

Decisões de UX:

- Só há ações que existem: cadastrar Item, sincronizar, pausar/retomar execuções agendadas, escolher intervalo, categorizar, ligar/desligar a categorização após a sincronização. **Não há "desconectar"**: o texto manda encerrar a conexão no Meu Pluggy.
- O diagnóstico do antigo laboratório continua disponível, secundário, dentro de "Diagnóstico técnico da Pluggy (avançado)" (carregado só ao abrir). O formulário de cadastro do Item existe uma vez só (`AddConnectionForm`; o laboratório embutido não o repete).
- Estados sempre em texto (nunca só cor): "Ativa", "Pausada", "Agendamento desligado no servidor", "Agente indisponível", "Concluída com pendências" etc.
- Carregamento, erro com nova tentativa, vazio e indisponível têm mensagem própria em cada aba. Depois de uma ação a tela anterior permanece enquanto os dados recarregam (mensagens não somem).
- Em **Contas → Integração bancária** ficaram os cartões integrados e a fila de movimentos para revisar; os painéis de sincronização e categorização foram removidos de lá (um aviso aponta para Automação).

## 2. Serviços, agendador e rotas reutilizados

Nenhum motor de sincronização, agente, agendador ou provedor novo foi criado.

- **Sincronização**: manual e agendada passam por `PluggyTransactionSyncService.RunConnectionAsync`. Continuam valendo o índice único de execução em andamento por conexão, a reserva `TryClaim` do agendador, `PluggySyncState` e a recuperação de execução abandonada (`SyncStaleMinutes`). Manual e agendada nunca rodam ao mesmo tempo na mesma conexão.
- **Agendador**: `PluggyAutoSyncRunner`/`PluggyAutoSyncHostedService` (config `Pluggy:AutoSync`, padrão desligado). **Ligar/desligar o agendador é configuração do servidor**; o usuário só **pausa** as execuções agendadas do seu orçamento e escolhe o intervalo (6, 12 ou 24 h — as únicas opções que o servidor executa). Pausar não interrompe uma sincronização já iniciada e não impede "Sincronizar agora".
- **Categorização**: o serviço existente (manual → regras do domínio → categoria da Pluggy → IA), agora chamado por `PluggyCategorizationJob`, que apenas registra o histórico. É o mesmo caminho do botão manual e da etapa após a sincronização.
- **Próxima execução**: só aparece quando o agendador real a calcula (ligado no servidor, não pausado, com conexão integrada). Caso contrário o cartão diz o motivo ("pausadas", "desligado no servidor", "sem conexão com conta integrada", "integração indisponível").
- Ler a central (`GET`) consulta apenas banco e configuração: nunca sincroniza, nunca chama a Pluggy nem a IA.

Rotas novas (todas em `/api/financial-workspaces/{w}/pluggy-integration`): `GET|PUT automation`, `GET automation/runs`, `GET automation/runs/{Sync|Categorization}/{id}`, `GET classification-review`; e `PUT /credit-cards/{c}/purchases/{p}/category`. Acesso apenas ao dono do orçamento (outro usuário recebe 404).

## 3. Estados e contagens

**Estados do histórico** (os quatro pedidos): *Em andamento*, *Concluída*, *Concluída com pendências*, *Falhou*. A sincronização "concluída com erros" aparece como "com pendências"; a categorização é "com pendências" quando sobraram lançamentos sem categoria segura ou a IA parou antes do fim (limite ou falha do provedor).

**Execução interrompida**: uma "em andamento" mais antiga que o limite aparece como **Falhou — interrompida**, sem horário de término inventado; é encerrada como falha na próxima passada do agendador ou no próximo início. Ler não altera nada.

**Contagens por operação, nunca somadas entre etapas**:

- Sincronização: lidas, novas, atualizadas, sem mudança, ausentes na origem, contas com erro de N.
- Categorização: elegíveis, classificadas sem IA, pela IA, pendentes, preservadas por escolha do usuário, enviadas à IA e chamadas.
- A categorização após a sincronização é **outra linha** no histórico, ligada à sincronização por `relatedRunId`; a sincronização continua "Concluída" mesmo se a etapa de IA falhar.

Só ficam no histórico contagens e códigos curtos (`Interrupted`, `AiUnavailable`, `RateLimited:…`). Nunca descrição de lançamento, valor, resposta do provedor, token ou payload. Testes verificam que o JSON do histórico não contém segredo, `itemId` nem descrição.

**Cartão de categorização**: elegíveis = o que uma execução comum processaria (sem escolha manual e sem categoria automática), contagem do **orçamento inteiro**; sem categoria segura; ainda não avaliados; última execução; limites e pendências explicados.

## 4. Configuração do provedor de IA

- Seção renomeada de `CreditCardStatementAgent` para **`AiAgent`** (sem `Mode`): variáveis `AI_AGENT_ENABLED` e `AI_AGENT_MODEL`. Consumidores, exemplos (`appsettings.Example.json`, `.env.production.example`, `docker-compose.production.yml`), scripts de CI, testes de contrato de implantação e docs foram atualizados. **Não há fallback permanente** para o nome antigo: o script de CI exige a ausência de `CreditCardStatementAgent__*` no ambiente.
- **Ajuste das variáveis provisionadas** (antes de qualquer implantação futura): renomear `CREDIT_CARD_STATEMENT_AGENT_ENABLED/MODE/MODEL` para `AI_AGENT_ENABLED/MODEL`; o antigo `MODE` deixa de existir. Registrado em `docs/T16-deploy-agente-openai.md` e `docs/deploy-producao.md`.
- Nenhum segredo de produção foi tocado e a IA **não foi habilitada** (`AI_AGENT_ENABLED` continua `false` nos exemplos). Documentos históricos T16.x mantêm o nome antigo como registro da época.
- Estado do agente na central: disponível (com o modelo), ou indisponível com o motivo ("não configurado neste ambiente", "desligada neste ambiente"); a categorização local continua funcionando sem IA.

## 5. Fluxo de classificação

Ordem na categorização: **escolha manual → regras do domínio → categoria da Pluggy (só se mapeada para uma categoria do orçamento) → IA**.

- Escolha manual de categoria e de tipo de despesa **nunca é substituída**, nem com "forçar".
- A IA só escolhe entre as categorias e o tipo válidos do orçamento; **sugestão de categoria nova não cria categoria** (R02); resposta inconclusiva deixa o lançamento pendente; a IA **não infere tipo de despesa**.
- **Após a sincronização** (opcional): preferência por orçamento, **padrão desligada**, só liga com consentimento explícito (guardado com data; um `CHECK` do banco liga a preferência ao consentimento; desligar apaga o consentimento e religar exige confirmar de novo). Roda só se o agente estiver disponível, só nos elegíveis; com a IA desligada ou indisponível a sincronização termina normalmente e nenhuma linha de categorização é criada. Falha da IA não é falha da conexão (estado e falhas consecutivas da conexão ficam intactos).
- Uma categorização por orçamento por vez (índice único parcial): clique repetido recebe 409 e não duplica execução.

**Classificação manual de despesas de cartão importadas**: a compra importada é uma cobrança do banco (uma parcela). A correção vale **só para a própria compra**; em compra parcelada as parcelas herdam o tipo. `PUT .../purchases/{id}/category` troca só a categoria (valor, parcelas, fatura, data e identificador externo não mudam) e registra a escolha como do usuário no vínculo Pluggy, de modo que sincronizações e categorizações não a desfazem; compra em fatura oficial é imutável (409); categoria inválida/indisponível, 400; outro usuário, 404. O tipo reaproveita `PUT .../expense-nature` (preservado pela sincronização). A mudança reflete nas metas por categoria e de despesas fixas (mesma lista de fatos do orçamento, R03).

**Lista "Revisar classificação"** (`classification-review`), em Automações:

- *Despesas sem tipo definido* (período, padrão o mês corrente): exatamente os registros que originam a contagem do orçamento, **inclusive compras de cartão**, mostrando a origem (conta ou cartão), "importado do banco", categoria e tipo atuais e o **alcance** da correção.
- *Importados sem categoria segura* (orçamento inteiro, e diz isso).
- Cobranças e estornos de fatura, parcelas históricas e dívidas não têm onde ser classificados: ficam **explicitamente sem tipo** (contagem `unclassifiableCount`, exibida na lista e no cartão da meta de fixas) e fora das fixas. Previsão é editada na tela de origem.
- É só edição de classificação; não reintroduz conferência nem conciliação.

**Avisos**: "Revisar classificação" no orçamento abre essa lista no mês da meta ("despesa(s) **do mês**"); o aviso da Visão Geral abre a lista por categoria e diz "todos os importados do banco, **não só deste mês**"; "Ver conexões" abre a aba Conexões.

## 6. Persistência do histórico e migração

- Sincronizações: tabela existente `pluggy_sync_runs`. Categorizações: nova `pluggy_categorization_runs` (gatilho Manual/AfterSync, `sync_run_id`, conexão, início/fim, estado, contagens, `ai_stopped_because`, `failure_code`, modelo; índice único parcial `ux_pluggy_categorization_runs_running`). Preferências: nova `pluggy_automation_settings` (uma linha por orçamento: pausa, intervalo 6/12/24, categorizar após sincronização, consentimento; `CHECK`s de valores e de consentimento).
- O histórico é a união das duas tabelas, ordenada do mais recente, com filtros e paginação no servidor. **Nenhuma execução passada foi inventada**: sem linha, a central diz que não há registro.
- Migração `20261030184418_AddPluggyAutomationCenter` (+ snapshot; `ReadOnlyBudgetDestinationSnapshot` passa a esperar as duas tabelas). Aplicada **apenas em bancos descartáveis dos testes**; **não foi aplicada ao laboratório**. O `Down` não foi exercitado nesta entrega.
- Retentativa: não há "tentar de novo" automático nem botão de retentativa; a ação segura e idempotente é rodar de novo ("Sincronizar agora" / "Categorizar"), que respeita o índice de execução única.

## 7. Testes e evidências

**Simulado** (Pluggy e provedor de IA simulados; API real sobre PostgreSQL real; nenhuma chamada paga, nenhuma sincronização real):

- Backend `PluggyAutomationCenterTests` (16): leitura sem efeito, padrões (preferência desligada, sem consentimento), IA indisponível, validação (intervalo, consentimento explícito e registrado, campo desconhecido, nada gravado em recusa), isolamento entre usuários/orçamentos, pausa × manual × agendada e retomada, intervalo escolhido, histórico (duas automações, contagens por operação, filtros, paginação, validações, detalhe, sem segredo/descrição), execução abandonada, 409 em categorização concorrente, categorização após a sincronização (consentimento + IA; sem IA; falha da IA não afeta a conexão), escolha manual preservada com "forçar", lista de revisão (parâmetros e escopo) e compra de cartão importada (categoria + tipo sobrevivem a nova sincronização e categorização forçada).
- Regressão filtrada (Pluggy, Budget, Deployment, RetiredFlows, CreditCard): 540 testes passaram antes dos novos testes. Suíte completa do backend (1348 testes, ~20 min): 1346 passaram; as 2 falhas eram o reset do orçamento, que passou a contar as duas tabelas novas (ajustados o fixture de reset e a contagem esperada, 46 → 48); os 9 testes de reset passam. Os 3 testes `RealOcrToolTests` já não existem nesta árvore (R00).
- Frontend: `AutomationCenter.test.tsx` (16) e teste de navegação em `AppShell.test.tsx`; `Overview.test.tsx` ajustado ao novo destino do aviso. Total do cliente: 462 testes verdes; `tsc -b` e `oxlint` sem avisos nos arquivos novos.
- Visual (Chromium próprio, fixtures **sintéticas**, script `pw/shots_r04.mjs`): abas Conexões/Automações/Histórico em base, estresse (nomes e valores enormes, IA indisponível, pausada), vazio, indisponível e falha, em 1440, 768, 390 e 360 px: sem rolagem horizontal, sem elementos fora da viewport, sem texto cortado. Ajustes feitos: formulário de cadastro em linha flexível, alvo de toque do marcador de consentimento.

**Real**: nenhuma validação com dados reais nesta entrega (o laboratório não recebeu a migração, não houve sincronização nem chamada à IA).

## 8. Limitações

- "Sincronizar agora" no cartão leva à aba Conexões (a sincronização é por conexão); não há sincronização de todas de uma vez.
- Pausa e intervalo valem para o orçamento inteiro, não por conexão. O intervalo novo vale a partir da próxima sincronização concluída.
- Se o agendador estiver desligado no servidor, as preferências ficam salvas mas não produzem execuções.
- O histórico começa na implantação da migração: sincronizações antigas aparecem (já eram registradas), categorizações antigas não existem.
- A lista por tipo de despesa considera um período (mês corrente por padrão); a por categoria considera o orçamento inteiro.
- Interface não atualiza sozinha enquanto uma execução está "em andamento" (há "Atualizar" no histórico).
- Melhorias não bloqueantes para o backlog: sincronizar todas as conexões; pausa por conexão; atualização automática do histórico; classificação em lote com alcance explícito; testar o `Down` da migração.

## 9. Preparação para implantação futura (não executada)

1. Renomear as variáveis do agente (`AI_AGENT_ENABLED/MODEL`) no ambiente provisionado; manter `AI_AGENT_ENABLED=false` até autorização.
2. Aplicar a migração `AddPluggyAutomationCenter` por backup + janela controlada (primeiro em cópia do banco real).
3. Decidir se liga `Pluggy:AutoSync:Enabled` no servidor; as preferências do usuário nascem com "não pausado", intervalo do servidor e categorização após a sincronização desligada.
4. Validar com dados reais no laboratório (aplicar a migração ao `saldo360_pluggy_lab` mediante autorização) e registrar à parte do que foi simulado.

## 10. Pendências

- Validação com dados reais (laboratório) e verificação do `Down` da migração.
