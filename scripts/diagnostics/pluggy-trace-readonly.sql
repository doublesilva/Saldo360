-- Rastreio SOMENTE LEITURA de movimentos Pluggy -> posting -> registro financeiro -> tela.
-- Uso (psql, com autorização para o banco-alvo; a transação é READ ONLY e sempre desfeita):
--   psql "<conexão autorizada>" -v workspace=<uuid-do-orçamento> -v amount=55.99 -v from=2026-10-01 -v to=2026-10-31 -f pluggy-trace-readonly.sql
-- Busca por valor é LOCALIZADOR, não identificador: a seção 2 mostra TODAS as ocorrências candidatas (mesmo valor), e não só uma.
-- Dados pessoais mascarados: descrição truncada (14 caracteres), ids externos truncados (8), nenhum número de conta/cartão, nenhum segredo.
-- O aviso de falha de login (OIDC, "?login=failed&ref=...") é OUTRA investigação: procure o código ref no log da API
-- ("Login OIDC não concluído. Motivo ... Correlação ..."); nada neste script depende dele.

BEGIN READ ONLY;

\echo '== 1. Contas integradas: vínculo, ImportFrom e cobertura da importação =='
SELECT l.id AS link_id, l.display_name, l.kind, l.status, l.import_from,
       l.last_synced_at,
       min(t.date)::date AS first_imported_date, max(t.date)::date AS last_imported_date, count(t.id) AS imported_rows,
       count(t.id) FILTER (WHERE t.missing_sync_count > 0) AS rows_missing_in_last_syncs
FROM pluggy_account_links l
LEFT JOIN pluggy_imported_transactions t ON t.workspace_id = l.workspace_id AND t.connection_id = l.connection_id AND t.pluggy_account_id = l.pluggy_account_id
WHERE l.workspace_id = :'workspace'
GROUP BY l.id ORDER BY l.display_name;

\echo '== 1b. Últimas coletas por conexão (erros de truncamento/paginação aparecem em error_summary) =='
SELECT r.connection_id, r.started_at, r.status, r.accounts_total, r.accounts_synced, r.seen, r.created, r.updated, r.errors, left(r.error_summary, 120) AS error_summary
FROM (SELECT *, row_number() OVER (PARTITION BY connection_id ORDER BY started_at DESC) AS n FROM pluggy_sync_runs WHERE workspace_id = :'workspace') r
WHERE r.n <= 5 ORDER BY r.connection_id, r.started_at DESC;

\echo '== 2. Ocorrências candidatas pelo valor (todas), com a cadeia até o efeito =='
SELECT t.id AS imported_id, left(t.pluggy_transaction_id, 8) AS external_id, l.display_name AS link, t.date::date AS bank_date,
       t.amount, t.type, t.operation_type, t.status, t.classification, t.classification_reason, t.category AS pluggy_category,
       left(t.description, 14) || '…' AS description_masked,
       p.id AS posting_id, p.state AS posting_state, p.kind AS posting_kind, p.reason AS posting_reason, p.link_review_reason, p.resolution, p.link_origin,
       p.applied_type, p.applied_amount, p.applied_date,
       ft.id AS financial_tx_id, ft.type AS fin_type, ft.origin AS fin_origin, ft.is_cancelled AS fin_cancelled,
       tr.id AS transfer_id, tr.is_cancelled AS transfer_cancelled,
       CASE
         WHEN l.import_from IS NOT NULL AND t.date::date < l.import_from THEN 'FORA_DO_PERIODO_DE_IMPORTACAO (sem posting, sem efeito)'
         WHEN p.id IS NULL THEN 'SEM_POSTING (conta não processada ou fora do período)'
         WHEN p.state = 'WaitingPosted' THEN 'AGUARDANDO (pendente ou data futura): não entra no realizado'
         WHEN p.state = 'NeedsReview' THEN 'EM_REVISAO: fora de receitas/despesas/saldo até decisão (motivo em posting_reason)'
         WHEN p.state = 'Ignored' THEN 'IGNORADO ou cancelado pelo usuário'
         WHEN p.state = 'Covered' THEN 'EVIDÊNCIA de outro registro (pagamento de fatura)'
         WHEN p.kind = 'Transfer' THEN 'TRANSFERÊNCIA: aparece em Transferências, NÃO em receitas/despesas'
         WHEN p.kind = 'BankRefund' THEN 'DEVOLUÇÃO: entrada de caixa, fora das receitas, abate a despesa'
         WHEN ft.is_cancelled THEN 'LANÇAMENTO CANCELADO'
         ELSE 'APLICADO como ' || coalesce(p.applied_type, '?') || ' (aparece em Lançamentos/Agenda)'
       END AS explanation
FROM pluggy_imported_transactions t
JOIN pluggy_account_links l ON l.workspace_id = t.workspace_id AND l.connection_id = t.connection_id AND l.pluggy_account_id = t.pluggy_account_id
LEFT JOIN pluggy_postings p ON p.imported_transaction_id = t.id
LEFT JOIN financial_transactions ft ON ft.id = p.financial_transaction_id
LEFT JOIN financial_transfers tr ON tr.id = p.financial_transfer_id
WHERE t.workspace_id = :'workspace'
  AND (abs(t.amount) = :'amount'::numeric OR :'amount'::numeric = 0)
  AND t.date::date BETWEEN :'from'::date AND :'to'::date
ORDER BY t.date, l.display_name, t.amount;

\echo '== 3. Contrapartes possíveis (mesmo valor, sentido oposto, +-1 dia, outra conta): explicam pareamento ou TransferDateMismatch =='
SELECT a.id AS imported_a, la.display_name AS link_a, a.date::date AS date_a, a.type AS type_a, a.operation_type AS op_a,
       b.id AS imported_b, lb.display_name AS link_b, b.date::date AS date_b, b.type AS type_b, b.operation_type AS op_b,
       (a.date::date = b.date::date) AS same_day, pa.state AS state_a, pb.state AS state_b, pa.kind AS kind_a, pb.kind AS kind_b
FROM pluggy_imported_transactions a
JOIN pluggy_account_links la ON la.workspace_id = a.workspace_id AND la.connection_id = a.connection_id AND la.pluggy_account_id = a.pluggy_account_id
JOIN pluggy_imported_transactions b ON b.workspace_id = a.workspace_id AND abs(b.amount) = abs(a.amount) AND b.type <> a.type AND abs(b.date::date - a.date::date) <= 1 AND b.id > a.id
JOIN pluggy_account_links lb ON lb.workspace_id = b.workspace_id AND lb.connection_id = b.connection_id AND lb.pluggy_account_id = b.pluggy_account_id AND lb.id <> la.id
LEFT JOIN pluggy_postings pa ON pa.imported_transaction_id = a.id
LEFT JOIN pluggy_postings pb ON pb.imported_transaction_id = b.id
WHERE a.workspace_id = :'workspace' AND abs(a.amount) = :'amount'::numeric AND a.date::date BETWEEN :'from'::date AND :'to'::date
ORDER BY a.date;

\echo '== 4. Totais por conta (o que a tela soma): receitas e despesas aplicadas, transferências e devoluções =='
SELECT a.name AS account,
       coalesce(sum(t.amount) FILTER (WHERE t.type = 'Income' AND t.origin <> 'PluggyRefund' AND NOT t.is_cancelled), 0) AS revenue,
       coalesce(sum(t.amount) FILTER (WHERE t.type = 'Expense' AND NOT t.is_cancelled), 0) AS gross_expense,
       coalesce(sum(t.amount) FILTER (WHERE t.origin = 'PluggyRefund' AND NOT t.is_cancelled), 0) AS refunds
FROM financial_accounts a LEFT JOIN financial_transactions t ON t.account_id = a.id AND t.transaction_date BETWEEN :'from'::date AND :'to'::date
WHERE a.workspace_id = :'workspace' GROUP BY a.id, a.name ORDER BY a.name;

ROLLBACK;
