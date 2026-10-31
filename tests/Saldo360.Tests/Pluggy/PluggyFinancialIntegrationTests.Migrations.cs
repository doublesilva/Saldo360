using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Saldo360.Domain.Pluggy;

namespace Saldo360.Tests.Pluggy;

/// <summary>
/// Migrations de auditoria de vínculos e de devoluções sobre um banco PostgreSQL descartável JÁ POPULADO (movimentos, transferência inferida, rastros):
/// desce ao esquema anterior e sobe de novo sem alterar nenhum dado preexistente; o reprocessamento continua idempotente e as constraints funcionam.
/// </summary>
public sealed partial class PluggyFinancialIntegrationTests
{
    private const string PreviousSchema = "20261030184418_AddPluggyAutomationCenter";

    /// <summary>Retrato só com colunas que existem nos dois esquemas (anterior e atual).</summary>
    private async Task<string> DataFingerprintAsync()
    {
        await using var db = Db();
        return (await db.Database.SqlQueryRaw<string>("""
            SELECT (SELECT count(*) FROM financial_transactions)::text || '|' || (SELECT coalesce(sum(amount), 0) FROM financial_transactions)::text
                || '|' || (SELECT count(*) FROM financial_transactions WHERE is_cancelled)::text
                || '|' || (SELECT count(*) FROM financial_transfers)::text || '|' || (SELECT count(*) FROM financial_transfers WHERE is_cancelled)::text
                || '|' || (SELECT coalesce(string_agg(state || ':' || kind || ':' || coalesce(applied_amount::text, '-') || ':' || coalesce(applied_date::text, '-'), ',' ORDER BY id::text), '') FROM pluggy_postings)
                || '|' || (SELECT count(*) FROM pluggy_imported_transactions)::text AS "Value"
            """).ToListAsync()).Single();
    }

    private async Task<bool> ColumnExistsAsync(string table, string column)
    {
        await using var db = Db();
        return (await db.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM information_schema.columns WHERE table_name = {table} AND column_name = {column}").ToListAsync()).Single() == 1;
    }

    private async Task<bool> TableExistsAsync(string table)
    {
        await using var db = Db();
        return (await db.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM information_schema.tables WHERE table_name = {table}").ToListAsync()).Single() == 1;
    }

    [Fact]
    public async Task TheLinkAuditAndRefundMigrationsApplyOverAPopulatedDatabaseWithoutTouchingExistingDataAndTheEngineStaysIdempotent()
    {
        var p = await ArrangePairAsync("t-migration-populated", [Tx("pf-in", BankA, 4, 700m, operation: "PIX", description: "Pix recebido", category: "Transfer - PIX"),
            Tx("solo", BankA, 3, 123.45m, operation: "PIX", description: "Pix recebido de terceiro", category: "Transfer - PIX"), Tx("spend", BankA, 6, -45m, operation: "BOLETO", category: "Groceries")],
            [Tx("pj-out", BankB, 4, -700m, operation: "PIX", description: "Pix enviado", category: "Transfer - PIX")]);
        await SyncBothAsync(p);
        Assert.Equal(1, await ActiveTransfersAsync());
        var before = await DataFingerprintAsync();

        // Desce ao esquema anterior às duas migrations (perde apenas o que elas criaram) e sobe de novo.
        await using (var db = Db()) await db.GetService<IMigrator>().MigrateAsync(PreviousSchema);
        Assert.False(await ColumnExistsAsync("pluggy_postings", "link_origin"));
        Assert.False(await TableExistsAsync("financial_bank_refunds"));
        Assert.False(await TableExistsAsync("pluggy_transfer_rejections"));
        Assert.Equal(before, await DataFingerprintAsync()); // os dados preexistentes seguem intactos no esquema anterior

        await using (var db = Db()) await db.GetService<IMigrator>().MigrateAsync();
        Assert.True(await ColumnExistsAsync("pluggy_postings", "link_origin"));
        Assert.True(await TableExistsAsync("financial_bank_refunds"));
        Assert.Equal(before, await DataFingerprintAsync()); // ...e depois da subida

        // O vínculo anterior ao registro de origem (legado) é tratado como inferido; reprocessar e sincronizar não duplica nada.
        var reprocess = await PostAsync(p.C, $"{Integration(p.C.Workspace)}/reprocess");
        Assert.Equal(HttpStatusCode.OK, reprocess.StatusCode);
        var summary = await JsonOf(reprocess);
        Assert.Equal((0, 0), (summary.GetProperty("created").GetInt32(), summary.GetProperty("transfersPaired").GetInt32()));
        await SyncBothAsync(p);
        Assert.Equal(before, await DataFingerprintAsync());
        var transfers = (await JsonGetAsync(p.C, $"/api/financial-workspaces/{p.C.Workspace}/transfers")).GetProperty("items").EnumerateArray().Single();
        Assert.Equal("Inferred", transfers.GetProperty("integration").GetProperty("origin").GetString());
        Assert.Empty(Reasons(await ReviewAsync(p.C)));

        // Constraints novas funcionando.
        await ExpectViolationAsync("UPDATE pluggy_postings SET link_origin = 'Bogus' WHERE kind = 'Transfer'", "ck_pluggy_postings_link_origin");
        await ExpectViolationAsync("UPDATE pluggy_postings SET link_origin = 'Inferred' WHERE kind = 'BankMovement'", "ck_pluggy_postings_link_origin");
        await ExpectViolationAsync("UPDATE pluggy_postings SET kind = 'Bogus' WHERE kind = 'BankMovement'", "ck_pluggy_postings_");
        await using var check = Db();
        var ids = await check.Database.SqlQuery<RefundIds>($"""
            SELECT t.workspace_id AS workspace_id, t.id AS transaction_id, u.id AS user_id FROM financial_transactions t CROSS JOIN LATERAL (SELECT id FROM application_users LIMIT 1) u LIMIT 1
            """).ToListAsync();
        var one = ids.Single();
        await ExpectViolationAsync($"INSERT INTO financial_bank_refunds (id, workspace_id, refund_transaction_id, original_transaction_id, amount, confirmed_by_user_id, confirmed_at) VALUES (gen_random_uuid(), '{one.WorkspaceId}', '{one.TransactionId}', '{one.TransactionId}', 1, '{one.UserId}', now())",
            "ck_financial_bank_refunds_distinct");
        await ExpectViolationAsync($"INSERT INTO financial_bank_refunds (id, workspace_id, refund_transaction_id, original_transaction_id, amount, confirmed_by_user_id, confirmed_at) VALUES (gen_random_uuid(), '{one.WorkspaceId}', '{one.TransactionId}', gen_random_uuid(), 0, '{one.UserId}', now())",
            "ck_financial_bank_refunds_amount_positive");
        // Vínculo entre orçamentos é impossível: as duas FKs compostas exigem a mesma família.
        await ExpectViolationAsync($"INSERT INTO financial_bank_refunds (id, workspace_id, refund_transaction_id, original_transaction_id, amount, confirmed_by_user_id, confirmed_at) VALUES (gen_random_uuid(), gen_random_uuid(), '{one.TransactionId}', gen_random_uuid(), 1, '{one.UserId}', now())",
            "fk_financial_bank_refunds");
    }

    private sealed record RefundIds(Guid WorkspaceId, Guid TransactionId, Guid UserId);

    private async Task ExpectViolationAsync(string sql, string constraint)
    {
        await using var db = Db();
        var error = await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync(sql));
        Assert.Contains(constraint, error.ToString());
    }

    [Fact]
    public async Task RevertingTheRefundMigrationWhileConfirmedRefundsExistIsRefusedAtomicallyAndLosesNothing()
    {
        var (p, _) = await ArrangeRefundAsync("t-migration-down", 12, 55.99m, ("back", 2, 55.99m, MarketplaceBack));
        Assert.Equal(HttpStatusCode.OK, (await RefundActionAsync(p.C, "confirm", await PostingIdAsync("back"), await TransactionIdOfAsync("pay"))).StatusCode);
        var before = await DataFingerprintAsync();

        // O Down não pode apagar devoluções confirmadas em silêncio: o esquema anterior não conhece o tipo BankRefund, a migration é recusada e nada muda.
        await using (var db = Db())
            await Assert.ThrowsAnyAsync<Exception>(() => db.GetService<IMigrator>().MigrateAsync(PreviousSchema));

        Assert.True(await TableExistsAsync("financial_bank_refunds"));
        Assert.Equal(1, await Db().FinancialBankRefunds.CountAsync(l => l.CancelledAt == null));
        Assert.Equal(before, await DataFingerprintAsync());
        Assert.Equal(PluggyPostingKind.BankRefund, (await Db().PluggyPostings.SingleAsync(x => x.Kind == PluggyPostingKind.BankRefund)).Kind);
    }
}
