using Microsoft.EntityFrameworkCore;
using Saldo360.Domain.Recurrences;

namespace Saldo360.Tests.Pluggy;

public sealed partial class PluggyFinancialIntegrationTests
{
    [Fact]
    public async Task ThreeConfirmedMonthlyCardChargesCreateOneRecurrenceAndUserDeactivationIsPreserved()
    {
        var c = await ArrangeAsync("card02-recurring", seed: false);
        var closings = Enumerable.Range(0, 3).Select(i => LatestClosing.AddMonths(-i)).Order().ToList();
        Pluggy.BillsByAccount[CardA] = closings.Select((closing, index) => TotalBill($"rec-bill-{index}", closing, 49.90m)).ToList();
        var charges = closings.Select((closing, index) =>
            CardTx($"rec-{index}", "STREAMING PLUS", 49.90m, closing.AddDays(-3), $"rec-bill-{index}")).ToList();
        Pluggy.TransactionsByAccount[CardA] = charges.Skip(1).ToList();

        await LinkCardAsync(c, ClosingDay, DueDay);
        await SyncAsync(c);
        await using (var candidateDb = Db())
        {
            var candidate = await candidateDb.FinancialRecurrences.SingleAsync(x => x.WorkspaceId == c.Workspace);
            Assert.Equal((RecurrenceStatus.Paused, 2, "Candidate"), (candidate.Status, candidate.EvidenceCount, candidate.DetectionConfidence));
        }
        Pluggy.TransactionsByAccount[CardA] = charges;
        await SyncAsync(c);

        Guid recurrenceId;
        await using (var db = Db())
        {
            var recurrence = await db.FinancialRecurrences.SingleAsync(x => x.WorkspaceId == c.Workspace);
            recurrenceId = recurrence.Id;
            Assert.Equal((RecurrenceOrigin.PluggyDetected, 3, "High", false),
                (recurrence.Origin, recurrence.EvidenceCount, recurrence.DetectionConfidence, recurrence.IsEstimated));
            recurrence.Archive(DateTimeOffset.UtcNow);
            await db.SaveChangesAsync();
        }

        await SyncAsync(c);

        await using var verification = Db();
        var preserved = await verification.FinancialRecurrences.SingleAsync(x => x.WorkspaceId == c.Workspace);
        Assert.Equal((recurrenceId, RecurrenceStatus.Archived, true), (preserved.Id, preserved.Status, preserved.IsUserAdjusted));
    }
}
