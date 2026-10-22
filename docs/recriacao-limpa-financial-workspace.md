# Recriação limpa do Saldo360 por FinancialWorkspace

22/10/2026 — **preparação em andamento; nenhuma exclusão remota executada**.

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

## Ordem operacional obrigatória

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

## Validação e gates pendentes

Testes sintéticos de configuração comprovam instalação sem destino/plano, seis perfis desligados quando
configurados, passagem do plano opcional como argumento único e recusa de conexão ausente.
Publicação em main/deploy assinado após CI verde autorizados explicitamente pelo responsável.
Intervenção administrativa confirmada como disponível, ainda não executada nesta etapa.

Ainda pendentes: CI do novo commit, preparação administrativa, recriação efetiva, login/bootstrap,
configuração do novo ID e smoke do reset. Não declarar o ambiente limpo antes dessas evidências.
