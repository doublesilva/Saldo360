using Microsoft.EntityFrameworkCore;
using Saldo360.Application.Calendar;
using Saldo360.Domain.Pluggy;
using Saldo360.Infrastructure.Persistence;

namespace Saldo360.Infrastructure.Calendar;

/// <summary>
/// Débitos bancários PENDENTES da Pluggy (ainda não efetivados, logo fora do realizado e da agenda), por conta bancária integrada. Mesma seleção usada pelo Plano × Realizado
/// desde a T10.1: só BRL, só o que a última sincronização ainda enxerga e só a partir da data inicial de importação do vínculo. Efetivados deixam de ser pendentes e passam a
/// existir como lançamento (nunca aparecem aqui e lá ao mesmo tempo); os registros importados nunca são alterados.
/// </summary>
internal static class PendingBankReader
{
    public static async Task<List<PendingBankDebit>> LoadDebitsAsync(Saldo360DbContext db, Guid workspaceId, IReadOnlyCollection<Guid>? accountIds, DateOnly through, CancellationToken ct)
    {
        var endUtc = new DateTimeOffset(through.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var rows = await (from t in db.PluggyImportedTransactions.AsNoTracking()
                          join l in db.PluggyAccountLinks.AsNoTracking() on new { t.ConnectionId, t.PluggyAccountId } equals new { l.ConnectionId, l.PluggyAccountId }
                          where t.WorkspaceId == workspaceId && l.WorkspaceId == workspaceId && l.Kind == PluggyLinkedAccountKind.Bank && l.FinancialAccountId != null
                             && (accountIds == null || accountIds.Contains(l.FinancialAccountId.Value))
                             && t.Status == PluggyTransactionStatus.Pending && t.MissingSyncCount == 0 && t.Date != null && t.Date < endUtc
                             && (t.Currency == null || t.Currency == "BRL") && t.Amount != null
                          select new { t.Id, t.Type, t.Amount, t.Date, t.Description, t.DescriptionRaw, AccountId = l.FinancialAccountId!.Value, l.ImportFrom }).ToListAsync(ct);
        return rows.Where(r => string.Equals(r.Type, "DEBIT", StringComparison.OrdinalIgnoreCase))
            .Select(r => new { r.Id, r.Amount, Description = $"{r.Description} {r.DescriptionRaw}".Trim(), r.AccountId, r.ImportFrom, Date = Saldo360.Application.Pluggy.PluggyFinancialMapper.BusinessDate(r.Date!.Value) })
            .Where(r => r.ImportFrom is not { } from || r.Date >= from)
            .Select(r => new PendingBankDebit(r.Id, r.AccountId, Math.Abs(r.Amount!.Value), r.Date, r.Description)).ToList();
    }
}
