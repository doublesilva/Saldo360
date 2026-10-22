-- psql only, with -X -qAt -v ON_ERROR_STOP=1 -v workspace_id=<explicit UUID>.
-- NDJSON: one identity/schema record followed by scoped table counts. No workbook contents or credentials.
\set ON_ERROR_STOP on
\if :{?workspace_id}
\else
\echo Explicit workspace_id required
\quit 1
\endif
BEGIN ISOLATION LEVEL REPEATABLE READ READ ONLY;
SELECT json_build_object('schemaVersion',1,'workspaceId',:'workspace_id'::uuid,
 'readAtUtc',clock_timestamp(),'databaseReadOnly',current_setting('transaction_read_only')='on',
 'workspaceType',(SELECT type FROM financial_workspaces WHERE id=:'workspace_id'::uuid),
 'isActive',(SELECT is_active FROM financial_workspaces WHERE id=:'workspace_id'::uuid),
 'ownerUserId',(SELECT owner_user_id FROM financial_workspaces WHERE id=:'workspace_id'::uuid),
 'ownerExists',EXISTS(SELECT 1 FROM financial_workspaces w JOIN application_users u ON u.id=w.owner_user_id WHERE w.id=:'workspace_id'::uuid),
 'appliedMigrations',(SELECT json_agg(migration_id ORDER BY migration_id) FROM "__EFMigrationsHistory"));
SELECT format('SELECT json_build_object(''table'', %L, ''count'', count(*)) FROM %I.%I WHERE workspace_id=%L',table_name,table_schema,table_name,:'workspace_id'::uuid)
FROM information_schema.columns WHERE table_schema='public' AND column_name='workspace_id' ORDER BY table_name
\gexec
COMMIT;
