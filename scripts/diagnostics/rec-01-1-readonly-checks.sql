-- REC-01.1 — consultas SOMENTE LEITURA: (1) alterações das coletas Pluggy, (2) linhas financeiras tocadas, (3) hash apenas das colunas financeiras,
-- (4) composição das despesas pendentes de nov/dez-2026. Parâmetros: -v since="'2026-11-02T09:18:57-03'" (instante da captura de referência).
-- Uso: psql -X -v ON_ERROR_STOP=1 -v since="'2026-11-02T09:18:57-03'" -f rec-01-1-readonly-checks.sql (ou, via stdin, iniciar com: \set since '''2026-11-02T09:18:57-03''')
BEGIN TRANSACTION ISOLATION LEVEL REPEATABLE READ READ ONLY;

\echo == 1. campos alterados nas coletas posteriores a :since
SELECT changed_fields, count(*) FROM pluggy_transaction_changes WHERE observed_at >= :since GROUP BY changed_fields ORDER BY 2 DESC;

\echo == 2. linhas tocadas desde :since
SELECT 'financial_transactions' AS tabela, count(*) AS tocadas FROM financial_transactions WHERE updated_at >= :since
UNION ALL SELECT 'pluggy_postings', count(*) FROM pluggy_postings WHERE updated_at >= :since
UNION ALL SELECT 'financial_transactions criadas', count(*) FROM financial_transactions WHERE created_at >= :since;

\echo == 3. hash das colunas financeiras (exclui carimbos de auditoria)
SELECT md5(string_agg(concat_ws('|', id, account_id, type, amount, transaction_date, description, is_cancelled), E'\n' ORDER BY id)) AS hash_financeiro, count(*), sum(amount)
FROM financial_transactions;

\echo == 4. previstos, recorrências ativas e dívidas que alimentam nov/dez-2026
SELECT to_char(due_date, 'YYYY-MM') AS mes, type, description, amount, is_cancelled
FROM financial_planned_transactions WHERE due_date >= '2026-11-01' AND due_date < '2027-01-01' ORDER BY due_date, amount DESC;
SELECT description, type, amount, is_estimated, start_date, end_date, status FROM financial_recurrences
WHERE status = 'Active' AND archived_at IS NULL AND (end_date IS NULL OR end_date >= '2026-11-01') ORDER BY amount DESC;
SELECT count(*) AS parcelas_divida FROM financial_debt_installments WHERE due_date >= '2026-11-01' AND due_date < '2027-01-01' AND cancelled_at IS NULL;
SELECT count(*) AS transferencias_planejadas FROM financial_planned_transfers WHERE due_date >= '2026-11-01' AND due_date < '2027-01-01' AND NOT is_cancelled;

COMMIT;
