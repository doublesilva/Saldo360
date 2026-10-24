# T16 — Diagnóstico controlado de fatura armazenada

Atualização T16.2: a separação atual entre extração, alertas de identidade, contexto selecionado e
consolidação está em [refatoração da extração](T16.2-refatoracao-extracao-faturas.md).
As evidências abaixo descrevem a implementação original do diagnóstico, não uma nova chamada real.

24/10/2026 — **Mecanismo concluído e validado localmente. Nenhuma chamada real foi executada.**

## Escopo e isolamento

Console administrativo separado: `src/Saldo360.StatementDiagnostic`. Não é endpoint nem serviço
iniciado pela API, deploy ou scheduler. Sem `--allow-openai`, o comando recusa a execução.
Reutiliza `OpenAiCreditCardStatementAgentProvider`, preview, extração textual/desbloqueio, prompt,
schema, orquestrador, categorização e resolução documental existentes. Não há outro cliente OpenAI.
Não chama Gmail, análise persistente da Inbox ou consolidação financeira.

O processo exige workspace, ApplicationUser.Id persistido, cartão candidato e documento explícitos.
`CardEmailSearchAuthorization` valida o Personal exclusivo ativo e seu proprietário. O comando
administrativo requer acesso autorizado ao host/BD; o parâmetro de usuário **não é um login OIDC**
nem substitui o controle de acesso operacional. Não existe acesso HTTP anônimo ou temporário.

Antes de consultar documento/storage/provider, inicia RepeatableRead com `SET TRANSACTION READ ONLY`
e verifica `transaction_read_only=on`. O storage reutiliza a validação existente de caminhos, sem
criar diretórios; Save/Delete são recusados. Verifica SHA-256 do PDF contra a Inbox antes de enviá-lo.
Nenhum StartAnalysis, SetStatementAnalysis, SaveChanges, commit ou writer financeiro é chamado.
O resultado não modifica o documento, independentemente de sucesso, rejeição ou senha ausente.

Somente este processo recebe `Enabled=true`, `Mode=AgentPrimary`, `Provider=OpenAI`, modelo
`gpt-4o-mini` e endpoint HTTPS oficial fixo. A configuração da API/Compose/servidor permanece intacta.
Um modelo diferente configurado no ambiente é recusado. Credencial deve vir de `ApiKeyFile`, nunca
argumento/valor inline; arquivo ausente não usa fallback para outra chave.

Há **uma única tentativa** do provider, sem retry de correção de totais. Grounding e conciliação C#
continuam ativos. A comparação com o parser é informativa e não transforma falha da IA em sucesso
do parser. A fonte primária é `OpenAI`, `Parser` ou `None`; ausência de saída estruturada nunca é
apresentada como interpretação bem-sucedida da OpenAI.

## Relatório e falhas

O JSON privado inclui modelo/provider/fonte, readonly confirmado, identificação documental e resolução
contra o cadastro, competência, vencimento, total oficial, compras/créditos/parcelas/ajustes, sugestões
de categoria, conciliação, avaliação, comparação com o parser e motivo de revisão. Inclui somente
o conteúdo estruturado necessário à comparação, não o PDF bruto ou senha. É dado financeiro sensível:
não anexar integralmente a logs, Git, CI ou chamados públicos.

O arquivo é CreateNew (não sobrescreve); em Linux é criado `0600`. No Windows, usar diretório privado
com ACL restrita ao responsável. A saída do comando contém apenas status/fonte/modelo/readonly/código
e indicação de ausência de mutação. Loggers do processo e do HttpClient do provider ficam desativados.

Códigos sanitizados distinguem `ProviderAuthenticationFailed`, `ProviderPermissionDenied`,
`ProviderQuotaExceeded`, `ProviderRateLimited`, `ProviderModelUnavailable`, `ProviderInvalidRequest`,
`ProviderHttpFailure`, `ProviderTimeout`, `ProviderNetworkFailure`, credencial ausente/vazia/ilegível,
resposta inválida e ausência de saída estruturada. HTTP 404 sem `model_not_found` é
`ProviderResourceNotFound`; não se inventa diagnóstico de modelo. Somente error.code limitado e
whitelisted é usado, nunca a mensagem livre do provider. Interpretação inválida/divergente permanece
visível na avaliação/conciliador, sem gravar ou ajustar valores para fechar total.

Referências consultadas pela skill OpenAI Docs: [modelo gpt-4o-mini](https://developers.openai.com/api/docs/models/gpt-4o-mini)
e [códigos de erro](https://developers.openai.com/api/docs/guides/error-codes).

## Procedimento exato — somente após autorização da chamada real

É necessário autorizar o envio do **texto extraído dessa fatura** à OpenAI e seu custo. Escolher um
único DocumentId já recebido; não buscar e-mail novamente. Obter o ApplicationUser.Id real do owner
e CardId, sem selecionar primeiro workspace/cartão ou usar subject OIDC como ID.

1. Compilar localmente (não publica API ou altera produção):

```powershell
dotnet build src/Saldo360.StatementDiagnostic/Saldo360.StatementDiagnostic.csproj -c Release
dotnet src/Saldo360.StatementDiagnostic/bin/Release/net10.0/Saldo360.StatementDiagnostic.dll --help
```

2. Em terminal administrativo privado, configurar **somente o ambiente do processo local**. Usar
conexão autorizada preferencialmente com role PostgreSQL somente leitura, storage existente acessível
em modo somente leitura e arquivo de segredo já disponibilizado de forma segura. Não copiar a chave
compartilhada do Jarvis para o repositório, não imprimir variáveis ou conteúdo dos arquivos.

```powershell
$diagConnection = Read-Host 'Conexão PostgreSQL autorizada (não será exibida)' -AsSecureString
$env:ConnectionStrings__Saldo360 = [System.Net.NetworkCredential]::new('', $diagConnection).Password
$env:FinancialDocuments__StoragePath = '<DIRETORIO_EXISTENTE_DO_STORAGE_AUTORIZADO>'
$env:CreditCardStatementAgent__ApiKeyFile = '<ARQUIVO_DE_SEGREDO_EXISTENTE_ACESSIVEL_COM_SEGURANCA>'
$env:CreditCardStatementAgent__Model = 'gpt-4o-mini'
```

Esses caminhos devem existir no host onde o processo é executado. `/etc/jarvis/secrets/openai-api-key`
é caminho do servidor, não automaticamente um arquivo local Windows. Se os recursos só estiverem no
servidor, **parar** e solicitar autorização operacional específica para disponibilizar o diagnóstico
temporário isolado, conexão/storage/segredo read-only. Este procedimento não autoriza SSH, cópia do
segredo, instalação remota ou deploy. Não tentar contornar a ausência dos recursos.

3. Usar diretório privado existente; o arquivo de saída deve ser novo:

```powershell
try {
  dotnet src/Saldo360.StatementDiagnostic/bin/Release/net10.0/Saldo360.StatementDiagnostic.dll `
    --allow-openai `
    --workspace-id 01a163b9-4b06-74f8-ac02-05bdc7cfb14e `
    --user-id '<APPLICATION_USER_ID_DO_OWNER>' `
    --card-id '<CARD_ID_CANDIDATO>' `
    --document-id '<DOCUMENT_ID_EXISTENTE>' `
    --output '.artifacts/t16-diagnostic/diagnostico-real-001.json'
} finally {
  Remove-Item Env:ConnectionStrings__Saldo360 -ErrorAction SilentlyContinue
  Remove-Item Env:FinancialDocuments__StoragePath -ErrorAction SilentlyContinue
  Remove-Item Env:CreditCardStatementAgent__ApiKeyFile -ErrorAction SilentlyContinue
  Remove-Item Env:CreditCardStatementAgent__Model -ErrorAction SilentlyContinue
  $diagConnection.Dispose()
}
```

Se `PasswordRequired`/`PasswordRejected`, preparar senha em arquivo privado fora do Git (nunca no
argumento) e repetir somente com autorização, acrescentando `--password-file '<ARQUIVO_PRIVADO>'` e
outro output. Tentativa sem senha não chama o provider quando o PDF não pode ser aberto. Não usar
o endpoint `statement-processing`, pois ele pode consolidar e está fora deste diagnóstico.

4. Conferir no JSON: `databaseReadOnly=true`, `source=OpenAI`, `extraction.primarySource=Agent`,
tentativas=1, identificação, itens e total em comparação manual com o PDF e parser. Revisão/rejeição
é um resultado diagnóstico, não liberação financeira. Exit code 0 = Reliable, 2 = relatório exige
revisão/rejeição, 1 = argumento/configuração/infraestrutura indisponível ou timeout do comando.

Ao encerrar o processo, a autorização de chamada termina: nenhum agente/endpoint fica ativo. Remover
artefatos temporários apenas conforme política de retenção aprovada; o relatório é confidencial.

## Evidências locais

- Testes direcionados: **59/59**, sem ignorados, Release, 1 min 13 s;
  `.artifacts/t16-diagnostic/t16-diagnostic-directed.trx`.
- Build final do console e dependências: aprovado, zero avisos/erros, 7,20 s.
- Smoke do CLI: `--help` aprovado; execução sem `--allow-openai` recusada com exit 1,
  sem criar relatório e antes de consultar configuração/BD/storage/provider.
- Casos novos: diagnóstico confiável, divergência sem retry, conflito parser/agente com interpretação
  preservada no relatório, senha ausente, JSON inválido, usuário não autorizado, autenticação,
  permissão, modelo indisponível, quota/rate limit e segredo ausente sem fallback inline.
- Integração PostgreSQL confirma readonly efetivo, snapshot completo da Inbox inalterado,
  ausência de mudanças rastreadas, zero fatos financeiros criados e categorias preservadas.
- Diff/whitespace conferidos. Nenhum teste de frontend, regressão completa, CI remota, push ou deploy.

Testes utilizam HTTP/provider/PDF sintéticos e PostgreSQL descartável, nunca documento/chave reais.
Logo, não comprovam sucesso de autenticação/quota/modelo na conta real: essa evidência depende da
execução explicitamente autorizada com um documento selecionado e os recursos disponibilizados.

## Arquivos

- Novo console `Saldo360.StatementDiagnostic` e entrada na solução.
- `StoredStatementDiagnostic` e adaptador de storage somente leitura na Infrastructure.
- Preview/orquestrador/contratos: opção diagnóstica de tentativa única preservando grounding.
- Provider existente: classificação sanitizada das falhas; DI: HttpClient sem loggers.
- Testes dirigidos do provider e testes PostgreSQL da análise/diagnóstico.
- Este relatório. Nenhuma migration ou configuração de deploy modificada nesta tarefa.
