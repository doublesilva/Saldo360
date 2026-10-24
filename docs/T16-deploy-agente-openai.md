# T16 — Configuração segura do agente no deploy

24/10/2026 — **Preparação local concluída. Agente desabilitado por padrão. Nenhum deploy executado.**

## Configuração final

Somente `api` recebe o bind individual read-only:
`/etc/jarvis/secrets/openai-api-key:/run/secrets/openai-api-key:ro`.
PostgreSQL, web e migrate não recebem a chave. `group_add: ["1654"]`, mounts e serviços anteriores
foram preservados. A chave não é valor de variável, argumento de build, arquivo do repositório ou imagem.

| Configuração da API | Valor inicial |
| --- | --- |
| CreditCardStatementAgent__Enabled | false |
| CreditCardStatementAgent__Mode | Disabled |
| CreditCardStatementAgent__Provider | OpenAI |
| CreditCardStatementAgent__Model | gpt-4o-mini |
| CreditCardStatementAgent__ApiKeyFile | /run/secrets/openai-api-key |

Enabled/Mode/Model podem ser alterados pelas variáveis não secretas `CREDIT_CARD_STATEMENT_AGENT_*`.
Ativação futura exige `ENABLED=true` **e** `MODE=AgentPrimary`; nada no CI ou script habilita essas flags.
O exemplo `.env.production.example` declara explicitamente os valores desabilitados e não contém chave.
As flags de classificação documental e extração de contas de serviço são independentes.

## Preflight e arquitetura

`deploy/server/deploy-saldo360` verifica o arquivo regular e tamanho não zero antes do build.
Após construir a imagem, valida leitura com o **usuário efetivo da imagem da API**, exigindo UID/GID
1654:1654 e os mounts/grupos do Compose. Usa `/bin/sh` como entrypoint, sem iniciar aplicação,
agente, coletor ou chamada de rede ao provider. Falha interrompe o deploy antes de backup, migrations
ou substituição de containers ativos. Não imprime/lê conteúdo em variáveis nem copia o arquivo.
Não executa reparo, chmod ou chown no segredo do Jarvis.

A dependência do arquivo é obrigatória mesmo com IA desligada, pois o bind é incondicional.
Arquivo ausente não pode ser convertido implicitamente em diretório por uma montagem Docker.
O provider existente prioriza o arquivo configurado e falha se faltar/estiver vazio, sem fallback inline.

Não foram alterados protocolo/assinatura do release, wrapper privilegiado, backup, migrations,
isolamento/health dos candidatos, cutover, smoke, registro de SHA ou recuperação existente.
O novo probe usa a imagem do SHA que seria publicado, não o container ativo `saldo360-api-1`.

## Verificações locais

- `test-statement-agent-deploy.sh`: aprovado. Resolve `docker compose config --format json` com
  variáveis fictícias e arquivo de ambiente vazio; verifica defaults, modelo configurável, montagem
  exata/read-only/somente API, ausência de chave inline, flags independentes e ordem do preflight.
  Exercita funções reais extraídas do deploy, em cópia com caminho sintético: arquivo ausente,
  vazio, leitor recusado e sucesso, sem conteúdo no diagnóstico. Nenhum deploy é invocado.
- `test-statement-agent-secret-reader.sh`: aprovado com Docker local, rede desabilitada e volume
  descartável. O comando exato do probe lê um arquivo **sintético** `0:1654 0440` como `1654:1654`
  em montagem read-only; recusa `0400`. Confirma que a leitura não altera owner/permissões.
  O chmod do teste modifica exclusivamente esse fixture efêmero, nunca `/etc/jarvis`.
  Containers são `--rm` e o volume é removido pelo teste. Não utiliza chave real.
- `test-clean-install-compose.sh`: aprovado, incluindo overlay dos seis perfis desabilitados e
  encaminhamento dos argumentos de migration. Nenhuma migration foi executada.
- `test-deploy-saldo360-wrapper.sh`: todos os testes aprovados, com releases/assinaturas sintéticas.
- Sintaxe Bash dos scripts alterados e novos validada por `bash -n`.
- Os dois novos testes foram incluídos no job de infraestrutura do CI; o workflow remoto não foi executado.

Essas verificações comprovam a configuração e os guards, **não** disponibilidade/permissões do segredo
real no `home-server`. Metadados reais são os fornecidos pelo responsável; nenhuma conexão ao servidor
foi feita. A verificação efetiva da imagem real será obrigatória num deploy futuro autorizado.

## Arquivos alterados

- `docker-compose.production.yml`: cinco configurações e um mount somente em API.
- `.env.production.example`: defaults não secretos/desabilitados.
- `deploy/server/deploy-saldo360`: preflight fail-closed, antes de operações sobre serviços ativos.
- `scripts/ci/test-statement-agent-deploy.sh` e `test-statement-agent-secret-reader.sh`: testes direcionados.
- `.github/workflows/ci.yml`: execução desses testes no job existente de infraestrutura.
- `docs/deploy-producao.md`: procedimento futuro de ativação e desativação controladas.
- Este relatório.

## Operação futura e limites

Seguir o [procedimento controlado](deploy-producao.md#agente-de-faturas-openai-configuração-não-é-ativação):
somente após autorização, configurar `CREDIT_CARD_STATEMENT_AGENT_ENABLED=true` e
`CREDIT_CARD_STATEMENT_AGENT_MODE=AgentPrimary`, publicar/aplicar pelo fluxo assinado e conferir saúde.
Não mostrar o segredo, executar `printenv` completo, copiar credencial ou alterar permissões do Jarvis.
Desativar restaurando `false`/`Disabled` e aplicar apenas em operação autorizada.

Sem push, deploy, SSH, leitura da chave real, chamada OpenAI, mudanças no Jarvis, alterações financeiras,
importações ou migrations. Os 89 parcelamentos não foram acessados nem modificados.

Backlog não essencial (somente registro): avaliar chave dedicada por aplicação e política de rotação
coordenada se futuramente desejado; não implementadas nem abertas novas tarefas.
