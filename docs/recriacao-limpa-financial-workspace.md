# Recriação limpa do Saldo360 por FinancialWorkspace

22/10/2026 — **concluído: PostgreSQL/storage recriados, login/Personal e coletores validados, reset vazio aprovado**.

O responsável declarou descartáveis a base PostgreSQL do Saldo360 e todo o conteúdo de
`/var/lib/saldo360/financial-documents`. Essa decisão substitui o cutover T08.4: não converter, preservar
como runtime, restaurar ou reimportar os cinco documentos antigos, nem recriar os dados de Vivo/CEEE/DMAE.
Não há autorização para alterar AccessGateway, Authelia, LLDAP, HomeAutomation, Jarvis ou outros serviços.

## Escopo confirmado em leitura

- Projeto Compose `saldo360`; containers `saldo360-api-1`, `saldo360-web-1`, `saldo360-postgres-1`.
- PostgreSQL: volume `saldo360_saldo360-pgdata`, labels de projeto `saldo360` e volume `saldo360-pgdata`;
  destino `/var/lib/postgresql/data`.
- Storage: bind exato `/var/lib/saldo360/financial-documents` no mesmo caminho dentro da API.
- Secrets OIDC/HMAC/Gmail e certificado existente são preservados; não pertencem à limpeza.

A remoção da base inteira também elimina usuários locais, sessões e DataProtectionKeys nela armazenados,
como solicitado ao recriar PostgreSQL do zero. Não apaga identidades no provedor OIDC. Login normal deverá
recriar exatamente um usuário local; não inserir usuário/workspace diretamente no banco.

## Correção da instalação limpa

O compose base não exige `FINANCIAL_WORKSPACE_ID` nem `FINANCIAL_WORKSPACE_CUTOVER_PLAN`.
Migrate recebe conexão explícita, sem Options de conversão quando não há plano. O guard existente da
migration continua recusando base familiar populada sem plano; nenhuma migration histórica foi alterada.

Com destino real configurado, o deploy acrescenta `docker-compose.collectors.yml`. Os seis perfis e o
agendador permanecem desligados, inclusive coleta manual. Sem destino, nenhum perfil é configurado;
não há Guid vazio/fictício como fallback. O Personal nasce somente pelo bootstrap autenticado.

`scripts/prepare-clean-saldo360-config.sh` é uma etapa administrativa separada: preserva um backup privado
apenas da configuração, remove o plano legado, limpa o destino (ou grava o ID real fornecido após bootstrap)
e desliga sete flags. Não para containers, não aplica migrations, não apaga base nem documentos.
Executar via sudo interativo; não enviar senha pelo chat. A sessão do agente não dispõe desse privilégio.

## Ordem operacional seguida

Registro da operação já concluída, não autorização para repeti-la. Não recriar novamente a base nem
reexecutar o smoke como consequência da leitura deste relatório.

1. Validar o commit final na CI, incluindo código/migrations e configuração limpa.
2. Preparar configuração administrativa sem destino/plano legado e com todas as flags desligadas.
3. Conferir novamente alvos, consumidores do volume, imagens disponíveis e ausência de serviços externos
   usando esses recursos. Parar API/web e quaisquer writers Saldo360 antes da limpeza.
4. Remover somente o container PostgreSQL e volume nomeado acima; limpar somente o conteúdo do storage
   aprovado, preservando sua raiz e permissões. Não usar `docker system prune`, remoção global ou `down -v`
   que amplie o escopo sem conferir os alvos.
5. Recriar PostgreSQL e aplicar todas as migrations em base vazia, sem plano; publicar API/web pelo fluxo
   assinado, com CI verde. Conferir healthchecks e flags efetivas.
6. Fazer login normal; executar `POST /api/me/financial-workspace` com CSRF e repetir ensure para provar
   idempotência. Conferir um Personal pertencente ao único usuário, sem Family/FamilyMember/convite.
7. Configurar coletores com o ID retornado pela API; recarregar mesma versão com perfis/agendador e demais
   automações desligados. Configuração não autoriza coleta.
8. Conferir todas as tabelas financeiras vazias e storage vazio. Executar preview e um reset confirmado
   `APAGAR ORÇAMENTO`: esperar `AlreadyEmpty`, zero remoções e nenhum `DocumentCutoverRequired`.
9. Revalidar contagens, saúde, flags e serviços externos inalterados. Reset vazio pode registrar auditoria
   e timestamp próprios; isso não constitui dado financeiro herdado nem deve ser apagado para simular vazio.

## Validação e gates atendidos

Testes sintéticos de configuração comprovam instalação sem destino/plano, seis perfis desligados quando
configurados, passagem do plano opcional como argumento único e recusa de conexão ausente. Ensaio com o
entrypoint real do compose e PostgreSQL local descartável aplicou todas as migrations sem plano, com zero
usuários/workspaces/famílias; repetição sem migrations pendentes. Regressão local direcionada: 17/17 testes
de migrations/bootstrap/isolamento/reset; contratos do deploy atualizado: 5/5.
Publicação em main/deploy assinado após CI verde autorizados explicitamente pelo responsável.
Intervenção administrativa executada pelo responsável: destino não configurado, plano temporário removido
e sete flags false. Backup apenas da configuração, root-only: `/srv/saldo360-deploy/.env.before-clean.UXJDer`.
Essa etapa não reiniciou containers nem excluiu dados.

A primeira CI (`39775558595`, commit `7cd6ab2`) aprovou infraestrutura/frontend e 858/859 testes backend;
o teste que exigia seis destinos no compose base foi atualizado para verificar o overlay opcional, destino
explícito sem fallback e os seis perfis desligados, além da ausência de gate permanente de conversão.
Nova CI do commit `bf98d97b5c1e91c9fc39a475c9ea114590b3a605`:
[39777626806](https://github.com/doublesilva/Saldo360/actions/runs/39777626806), **verde**.
Backend: **860/860**, em 10,71 minutos; build e smoke da imagem aprovados. Infraestrutura/frontend
permaneceram sem alterações desde a CI anterior aprovada nesses componentes.

## Execução remota concluída — antes do login

Janela iniciada em `2026-10-22T12:38:18Z`: API/web Saldo360 parados. As duas primeiras tentativas de deploy
pararam antes da limpeza: web candidato dependia de resolver a API ativa; em seguida foi identificada a
ausência de `--add-host` em `compose run`. Correção mínima: candidato web via `docker run --network none`
com alias local, somente para `/web-health`, como o teste da imagem na CI. Não muda o web publicado.
Wrapper/teste local aprovados, incluindo probe real sem rede/API. Nenhuma exclusão ocorreu nessas tentativas.

Versão final: `2d6bd5207c9f8163bb96f832939ec182e50432c2`.
[CI 39781238577](https://github.com/doublesilva/Saldo360/actions/runs/39781238577) e
[deploy assinado 39781387801](https://github.com/doublesilva/Saldo360/actions/runs/39781387801): **sucesso**.
Código da aplicação, frontend e migrations permanecem idênticos ao commit de backend 860/860;
as alterações posteriores são exclusivamente do probe de deploy e seu teste de infraestrutura.

Recriação destrutiva autorizada: `2026-10-22T12:50:55Z` a `2026-10-22T12:51:27Z`.

- Imagens/flags/projeto, consumidores do volume e raiz canônica do storage revalidados antes da primeira remoção.
- API/web parados; container PostgreSQL antigo removido e **volume `saldo360_saldo360-pgdata` removido**.
- **Todo o conteúdo de `/var/lib/saldo360/financial-documents` removido**, preservando raiz e permissões.
  Nenhuma cópia/conversão de documento foi feita.
- PostgreSQL novo, com novo volume do mesmo nome, criado pelo compose; **42 migrations aplicadas desde o início**,
  sem `workspace_cutover_plan`, sem restaurar dump e sem inserir workspace/owner manualmente.
- API/web da versão final reiniciados e healthy. Healthcheck PostgreSQL/API e web aprovados.
- O deploy normal mantém seu backup preexistente de pré-migração; nenhum backup antigo foi restaurado.
  Backups fora dos dois alvos autorizados não foram apagados nem usados para recriar dados.

Conferência por nova conexão, em transação `READ ONLY`, após a recriação e antes do login:

| Verificação | Resultado |
|---|---:|
| Migrations aplicadas | 42 |
| Usuários / workspaces / Personal | 0 / 0 / 0 |
| Famílias / membros / convites | 0 / 0 / 0 |
| Colunas FamilyId em tabelas financeiras/bindings | 0 |
| Contas, categorias, transações, transferências, previsões | 0 em cada tabela |
| Recorrências/materializações, cenários/parâmetros, budgets/targets | 0 em cada tabela |
| Cartões/compras/parcelas/faturas/pagamentos/ajustes/importações/conciliações | 0 em cada tabela |
| Dívidas/parcelas/pagamentos/conciliações, goals/alocações | 0 em cada tabela |
| Documentos / checkpoints / bindings | 0 / 0 / 0 |
| ServiceAccounts / identificadores pessoais | 0 / 0 |
| Vínculos de Itens do Laboratório Pluggy (T09.1) | 0 |
| Coletas, transações importadas e histórico de alterações Pluggy (T09.2) | 0 / 0 / 0 |
| Vínculos de contas, rastros de lançamento e faturas Pluggy (T09.3) | 0 / 0 / 0 |
| Auditorias de reset / intenções de cleanup | 0 / 0 |
| Conteúdo no storage financeiro | vazio |

Flags efetivas conferidas: e-mail/scheduler, classificação, extração, reconciliação, confirmação, binding e
agentes **false**. Nenhum perfil de coletor configurado antes de obter o ID real do Personal.
Estado dos **22 containers externos** comparado com a captura anterior à janela: IDs, estado, início e
contadores de restart inalterados. Nenhum serviço externo, segredo ou certificado foi modificado.

## Validações autenticadas e configuração final

O controle de navegador não dispõe de superfícies conectadas nesta sessão. Foi solicitada participação do
responsável no login normal e execução do check local de bootstrap (duas chamadas à API com CSRF, nenhuma
inserção direta no banco). Esse check também exige ausência de famílias e preview financeiro inteiramente vazio.

O responsável executou o fluxo normal após login e retornou `CleanPersonalBootstrap`: Personal ativo,
ensure idempotente, famílias zero, preview `Ready` com zero registros financeiros e reset não executado.
ID efetivamente criado pela API: `01a163b9-4b06-74f8-ac02-05bdc7cfb14e`.

Conferência independente por nova transação `READ ONLY` confirmou **1 usuário, 1 workspace/Personal ativo**,
owner correspondente ao único usuário local, **0 famílias/membros/convites**, 42 migrations e nenhuma coluna
FamilyId financeira. Todas as tabelas de fatos/cadastros financeiros, ServiceAccounts/identificadores,
auditorias de reset e cleanup continuam zeradas. `last_financial_reset_at` ainda nulo. Storage vazio e oito
flags de runtime (incluindo scheduler e dois agentes) false; API/web/PostgreSQL healthy.

Etapa administrativa confirmada pelo responsável: ID real registrado no `.env`, plano temporário removido
e sete flags false. Backup apenas da configuração, root-only: `/srv/saldo360-deploy/.env.before-clean.Yxohu2`.
O helper não reiniciou containers nem alterou dados. Reload da mesma versão `2d6bd52`, após nova conferência
de CI verde e SHA de main, concluído com sucesso pelo
[deploy assinado 39784707637](https://github.com/doublesilva/Saldo360/actions/runs/39784707637).

Após o reload, seis destinos de coletor conferidos no ambiente efetivo da API, todos iguais ao Personal
retornado pelo sistema e todos com `Enabled=false`. Scheduler/e-mail e demais sete flags consultadas também
false; inclusive classificação, extração, reconciliação, confirmação, binding e os dois agentes. Nenhuma
coleta executada. API/web/PostgreSQL healthy, inclusive acesso a `/health/ready` através do proxy web.
Nova conferência SQL read-only manteve um usuário/um Personal, famílias/membros/convites zero, 42 migrations,
zero fatos/cadastros financeiros, checkpoints/bindings zero, auditorias/cleanup zero e storage vazio.
Os 22 containers externos continuam idênticos à captura anterior à recriação.

O responsável executou uma única vez o smoke autenticado, que confere identidade/origem, exige o ID novo e
preview inteiramente vazio e envia a confirmação forte `APAGAR ORÇAMENTO`. Retorno informado:

```json
{
  "phase": "CleanWorkspaceEmptyReset",
  "workspaceId": "01a163b9-4b06-74f8-ac02-05bdc7cfb14e",
  "status": "AlreadyEmpty",
  "deletedRows": 0,
  "pendingFileCleanup": 0,
  "documentCutoverRequired": false
}
```

Conferência independente posterior, exclusivamente em leitura:

- **Uma única auditoria**, em `2026-10-22T13:18:05.981718Z` (10:18:05 em São Paulo), no Personal aprovado,
  com ator igual ao owner e `deleted_entities=0`.
- `last_financial_reset_at` registrado, Personal ativo e preservado. Continua **1 usuário / 1 Personal**.
- Todas as tabelas de cadastros/fatos financeiros, ServiceAccounts/identificadores, documentos, checkpoints
  e bindings continuam vazias; **zero intenções de cleanup** e **zero referências de storage legadas**.
- Famílias/membros/convites zero; 42 migrations; zero colunas FamilyId financeiras; storage vazio.
- API/web/PostgreSQL healthy, inclusive readiness via proxy web. Seis perfis apontam para o ID real e
  permanecem desligados; oito flags de automações/agents/scheduler false.
- Os mesmos **22 containers externos** continuam com IDs, estado, início e contadores de restart inalterados.

A auditoria e o timestamp são efeitos operacionais esperados do smoke, não dados financeiros herdados.
Não foram removidos para simular uma base sem evidência de reset. O agente não repetiu o POST: realizou
somente verificações em leitura após o resultado enviado pelo responsável.

## Parecer final

**Todos os critérios de aceite solicitados foram atendidos.** Ambiente ativo limpo exclusivamente no modelo
financeiro FinancialWorkspace, com Personal provisionado normalmente, sem Family como pré-requisito, sem
dados financeiros legados e com T08.1 respondendo `AlreadyEmpty`, sem `DocumentCutoverRequired`.
O plano de conversão não é requisito permanente do deploy limpo. Não houve cutover dos documentos antigos,
restauração ou bootstrap de Vivo/CEEE/DMAE. Automações permanecem desligadas e nenhum serviço externo foi alterado.

Relatório final publicado separadamente das alterações executáveis, sem novo deploy ou reset. Não repetir
a recriação, restaurar dados antigos ou habilitar integrações como consequência deste encerramento.
