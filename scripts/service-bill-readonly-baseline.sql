-- psql -v family_id=<familia-do-profile> -f service-bill-readonly-baseline.sql
-- Somente contagens e fingerprints; nenhum conteúdo financeiro é retornado.
\set ON_ERROR_STOP on
BEGIN TRANSACTION ISOLATION LEVEL REPEATABLE READ READ ONLY;
SET LOCAL statement_timeout = '15s';
SELECT format(
  'SELECT %L AS entity, count(*) AS rows, md5(COALESCE(string_agg(md5(to_jsonb(t)::text), %L ORDER BY md5(to_jsonb(t)::text)), %L)) AS fingerprint FROM public.%I t WHERE family_id = %L::uuid;',
  tablename, '', '', tablename, :'family_id')
FROM pg_tables AS tables
WHERE schemaname = 'public'
  AND (tablename LIKE 'financial\_%' ESCAPE '\' OR tablename = 'service_account_bindings')
  AND tablename NOT IN ('financial_document_inbox_items', 'financial_email_collector_checkpoints')
  AND EXISTS (
    SELECT 1 FROM information_schema.columns AS columns
    WHERE columns.table_schema = 'public' AND columns.table_name = tables.tablename
      AND columns.column_name = 'family_id')
ORDER BY tablename
\gexec

SELECT 'inbox_service_confirmations' AS entity,
       count(*) FILTER (WHERE service_bill_confirmed_amount IS NOT NULL) AS confirmed,
       count(*) FILTER (WHERE service_bill_confirmed_at IS NOT NULL) AS confirmed_at,
       count(*) FILTER (WHERE service_bill_occurrence_id IS NOT NULL) AS linked_occurrences
FROM financial_document_inbox_items
WHERE family_id = :'family_id'::uuid;
COMMIT;
