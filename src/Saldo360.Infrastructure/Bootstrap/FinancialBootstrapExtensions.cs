using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Saldo360.Application.Bootstrap;
using Saldo360.Application.Workspaces;
using Saldo360.Domain.Bootstrap;
using Saldo360.Domain.Budgets;
using Saldo360.Domain.Categories;
using Saldo360.Domain.CreditCards;
using Saldo360.Domain.Workspaces;
using Saldo360.Infrastructure.Persistence;

namespace Saldo360.Infrastructure.Bootstrap;

internal sealed class FinancialBootstrapExtensions(Saldo360DbContext db, TimeProvider time) : IFinancialBootstrapExtensions
{
    private Task<bool> Authorized(Guid w, Guid actor, CancellationToken ct) => db.FinancialWorkspaces.AsNoTracking()
        .AnyAsync(x => x.Id == w && x.OwnerUserId == actor && x.IsActive && x.Type == FinancialWorkspaceType.Personal, ct);

    private Task<FinancialBootstrapProvenance?> Find(Guid w, BootstrapSource s, CancellationToken ct) => db.FinancialBootstrapProvenance.AsNoTracking()
        .SingleOrDefaultAsync(x => x.WorkspaceId == w && x.SourceSystem == s.SourceSystem && x.SourceDocument == s.SourceDocument && x.ExternalReference == s.ExternalReference, ct);

    public async Task<BootstrapWriteResult> InspectProvenanceAsync(Guid w, Guid actorId, BootstrapSource source, string contentHash, CancellationToken ct)
    {
        if (!await Authorized(w, actorId, ct)) return new(BootstrapDisposition.Invalid, null, "NotFound");
        if (contentHash.Length != 64 || !contentHash.All(Uri.IsHexDigit)) return new(BootstrapDisposition.Invalid, null, "InvalidHash");
        try { _ = FinancialBootstrapProvenance.Create(w, source.SourceSystem, source.SourceDocument, source.ExternalReference, contentHash, "Inspect", Guid.NewGuid(), source.MetadataJson, time.GetUtcNow()); }
        catch (Exception e) when (e is ArgumentException or JsonException) { return new(BootstrapDisposition.Invalid, null, "InvalidProvenance"); }
        var existing = await Find(w, source, ct);
        return existing is null ? new(BootstrapDisposition.Created, null) : existing.ContentHash == contentHash.ToLowerInvariant()
            ? new(BootstrapDisposition.AlreadyImported, existing.EntityId) : new(BootstrapDisposition.Conflict, null, "SourceContentChanged");
    }

    public async Task<IReadOnlyList<FinancialMonthlyAggregateTarget>?> ListTargetsAsync(Guid w, Guid actorId, CancellationToken ct) =>
        await Authorized(w, actorId, ct) ? await db.FinancialMonthlyAggregateTargets.AsNoTracking().Where(x => x.WorkspaceId == w).OrderBy(x => x.Competence).ThenBy(x => x.TargetType).ToListAsync(ct) : null;

    public Task<BootstrapWriteResult> SaveTargetAsync(Guid w, Guid actorId, AggregateTargetInput input, CancellationToken ct) =>
        Save(w, actorId, input.Source, nameof(FinancialMonthlyAggregateTarget), input, async () => {
            if (await db.FinancialMonthlyAggregateTargets.AnyAsync(x => x.WorkspaceId == w && x.Competence == input.Competence && x.TargetType == input.TargetType, ct)) return (null, "TargetAlreadyExists");
            var target = FinancialMonthlyAggregateTarget.Create(w, input.Competence, input.TargetType, input.Amount, time.GetUtcNow());
            db.FinancialMonthlyAggregateTargets.Add(target); return (target.Id, null);
        }, ct);

    public Task<BootstrapWriteResult> SaveKnownInstallmentAsync(Guid w, Guid actorId, KnownCardInstallmentInput input, CancellationToken ct) =>
        Save(w, actorId, input.Source, nameof(FinancialCreditCardImportedInstallment), input, async () => {
            var card = await db.FinancialCreditCards.AsNoTracking().SingleOrDefaultAsync(x => x.WorkspaceId == w && x.Id == input.CreditCardId && !x.IsArchived, ct);
            if (card is null) return (null, "CardNotFound");
            if (input.CategoryId is { } category && !await db.FinancialCategories.AnyAsync(x => x.WorkspaceId == w && x.Id == category && x.Type == CategoryType.Expense && x.IsActive, ct)) return (null, "CategoryNotFound");
            // Competence means the known invoice due month, not a guessed purchase/closing date.
            var due = input.DueDate ?? new DateOnly(input.Competence.Year, input.Competence.Month, Math.Min(card.DueDay, DateTime.DaysInMonth(input.Competence.Year, input.Competence.Month)));
            if (due.Year != input.Competence.Year || due.Month != input.Competence.Month) return (null, "DueDateOutsideCompetence");
            var item = FinancialCreditCardImportedInstallment.CreateKnown(w, input.CreditCardId, input.CategoryId, input.Description,
                input.Amount, input.InstallmentNumber, input.TotalInstallments, input.Competence, due, input.Source.SourceSystem, input.Source.ExternalReference, actorId, time.GetUtcNow());
            db.FinancialCreditCardImportedInstallments.Add(item); return (item.Id, null);
        }, ct);

    private async Task<BootstrapWriteResult> Save<T>(Guid w, Guid actor, BootstrapSource source, string entityType, T content, Func<Task<(Guid? Id, string? Error)>> create, CancellationToken ct)
    {
        using var writeScope = FinancialWriteScope.EnterOperation(w);
        if (!await Authorized(w, actor, ct)) return new(BootstrapDisposition.Invalid, null, "NotFound");
        var hash = BootstrapContentFingerprint.Compute(entityType, content);
        try { _ = FinancialBootstrapProvenance.Create(w, source.SourceSystem, source.SourceDocument, source.ExternalReference, hash, entityType, Guid.NewGuid(), source.MetadataJson, time.GetUtcNow()); }
        catch (Exception e) when (e is ArgumentException or JsonException) { return new(BootstrapDisposition.Invalid, null, "InvalidProvenance"); }
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM financial_workspaces WHERE id={w} FOR UPDATE", ct);
            if (!await Authorized(w, actor, ct)) return new(BootstrapDisposition.Invalid, null, "NotFound");
            var existing = await Find(w, source, ct);
            if (existing is not null) return existing.ContentHash == hash && existing.EntityType == entityType
                ? new(BootstrapDisposition.AlreadyImported, existing.EntityId) : new(BootstrapDisposition.Conflict, null, "SourceContentChanged");
            var (id, error) = await create();
            if (id is null) return new(error == "TargetAlreadyExists" ? BootstrapDisposition.Conflict : BootstrapDisposition.Invalid, null, error);
            db.FinancialBootstrapProvenance.Add(FinancialBootstrapProvenance.Create(w, source.SourceSystem, source.SourceDocument, source.ExternalReference, hash, entityType, id.Value, source.MetadataJson, time.GetUtcNow()));
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
            return new(BootstrapDisposition.Created, id);
        }
        catch (ArgumentException) { await tx.RollbackAsync(CancellationToken.None); db.ChangeTracker.Clear(); return new(BootstrapDisposition.Invalid, null, "InvalidData"); }
        catch { await tx.RollbackAsync(CancellationToken.None); db.ChangeTracker.Clear(); throw; }
    }

    public async Task<BootstrapWriteResult> AttachKnownInvoiceAsync(Guid w, Guid actorId, Guid installmentId, Guid invoiceId, CancellationToken ct)
    {
        using var writeScope = FinancialWriteScope.EnterOperation(w);
        if (!await Authorized(w, actorId, ct)) return new(BootstrapDisposition.Invalid, null, "NotFound");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM financial_workspaces WHERE id={w} FOR UPDATE", ct);
        if (!await Authorized(w, actorId, ct)) return new(BootstrapDisposition.Invalid, null, "NotFound");
        var item = await db.FinancialCreditCardImportedInstallments.SingleOrDefaultAsync(x => x.WorkspaceId == w && x.Id == installmentId && x.SourceSystem != null, ct);
        var invoice = await db.FinancialCreditCardInvoices.SingleOrDefaultAsync(x => x.WorkspaceId == w && x.Id == invoiceId, ct);
        if (item is null || invoice is null) return new(BootstrapDisposition.Invalid, null, "NotFound");
        if (item.InvoiceId == invoiceId) return new(BootstrapDisposition.AlreadyImported, item.Id);
        if (await db.FinancialCreditCardInvoicePayments.AnyAsync(x => x.WorkspaceId == w && x.InvoiceId == invoiceId && x.Status == FinancialCreditCardInvoicePaymentStatus.Active, ct))
            return new(BootstrapDisposition.Conflict, null, "InvoiceHasPayments");
        try { item.AttachKnownInvoice(invoice); }
        catch (ArgumentException) { return new(BootstrapDisposition.Conflict, null, "InvoiceMismatch"); }
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return new(BootstrapDisposition.Created, item.Id);
    }
}
