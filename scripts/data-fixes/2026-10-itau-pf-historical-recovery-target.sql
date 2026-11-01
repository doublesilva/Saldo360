-- Meta histórica de recuperação: Itaú PF (escopo PF), outubro/2026.
-- Fonte (somente referência): planilha "Orçamento Diego 2026-2027", aba "Metas Mensais". Meta aprovada R$ 1.270,38 em 08/10/2026 (regra histórica: 85% da capacidade).
-- NÃO aplica o fator atual (80%): target_factor/target_capacity/fixed_at ficam NULOS => a tela mostra "meta histórica" e a data 08/10/2026.
-- Idempotente (índice único workspace/conta/competência + ON CONFLICT DO NOTHING). Não toca saldos, transferências, Pluggy nem outras competências.
-- Uso (após autorização):  psql -v ON_ERROR_STOP=1 -v opening_balance=-15284.39 -f <este arquivo>
-- opening_balance confirmado pelo usuário: -15284.39 em 01/10/2026 (-13826.92 é o saldo intermediário de 08/10, não o inicial).
\if :{?opening_balance} \else \echo 'Informe -v opening_balance=...'; \quit \endif
BEGIN;
WITH target_account AS (
    SELECT id, workspace_id FROM financial_accounts WHERE name = 'Itaú PF' AND scope = 'PF'
)
INSERT INTO overdraft_recovery_snapshots
    (id, workspace_id, account_id, competence, opening_balance, reference_date, planned_income, planned_expense, planned_closing_balance,
     recovery_target, captured_at, quality, composition, target_factor, target_capacity, fixed_at)
SELECT gen_random_uuid(), a.workspace_id, a.id, DATE '2026-10-01', :opening_balance::numeric, DATE '2026-10-01', 0, 0,
       :opening_balance::numeric + 1494.56, 1270.38, TIMESTAMPTZ '2026-10-08 12:00:00-03', 'Historical', 'SpreadsheetMetasMensais;Factor=0.85', NULL, NULL, NULL
FROM target_account a
WHERE (SELECT count(*) FROM target_account) = 1
ON CONFLICT (workspace_id, account_id, competence) DO NOTHING;
SELECT competence, recovery_target, captured_at, target_factor FROM overdraft_recovery_snapshots WHERE competence = DATE '2026-10-01' AND recovery_target = 1270.38;
COMMIT;
