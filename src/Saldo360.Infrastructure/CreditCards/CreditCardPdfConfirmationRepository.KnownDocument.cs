using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Saldo360.Application.CreditCards;
using Saldo360.Domain.Bootstrap;
using Saldo360.Domain.Categories;
using Saldo360.Domain.CreditCards;

namespace Saldo360.Infrastructure.CreditCards;

internal sealed partial class CreditCardPdfConfirmationRepository
{
    // Called only by explicit document confirmation, inside the existing workspace-root transaction.
    // No invoice/purchase/category/payment creation, no changes to official versions or original totals.
    private async Task<OfficialStatementConsolidationResult> ConfirmKnownDocumentAsync(FinancialDocumentInboxItem document,
        FinancialCreditCard card, InboxStatementAnalysis analysis, Guid user, DateTimeOffset now, IDbContextTransaction tx, CancellationToken ct)
    {
        var w = document.WorkspaceId;
        async Task<OfficialStatementConsolidationResult> Stop(string code, string status = "Conflict")
        { await tx.RollbackAsync(ct); db.ChangeTracker.Clear(); return new(status, FailureCode: code); }
        if (agentOptions?.DocumentaryImport.Enabled != true) return await Stop("DocumentaryImportDisabled", "NeedsReview");
        var decision = OfficialStatementAnalysisValidation.DecisionHash(analysis);
        var audit = await db.FinancialBootstrapProvenance.SingleOrDefaultAsync(p => p.WorkspaceId == w
            && p.SourceSystem == StatementKnownInstallmentImport.SourceSystem && p.SourceDocument == document.ContentHash && p.ExternalReference == "document", ct);
        if (audit is not null)
        {
            if (audit.EntityId != document.Id || audit.ContentHash != decision || document.CreditCardId != card.Id
                || document.Status != FinancialDocumentInboxStatus.Imported) return await Stop("DocumentConfirmationConflict");
            return await LinkConfirmedKnownInvoicesAsync(document, card, analysis, decision, user, now, tx, ct);
        }
        if (document.Status is not (FinancialDocumentInboxStatus.ReadyForReview or FinancialDocumentInboxStatus.ReadyToConfirm)
            || analysis.Status != "Reliable" || analysis.ConsolidationBlockReason is not null || analysis.KnownInstallmentImport?.Eligible != true)
            return await Stop("DocumentNotEligible", "NeedsReview");
        var eligibility = StatementKnownInstallmentImport.Evaluate(analysis.Extraction, card.Issuer, card.LastFourDigits, card.DueDay);
        if (!eligibility.Eligible) return await Stop(eligibility.Blockers[0], "NeedsReview");
        var catalog = await new StatementCategoryContextRepository(db).ReadAsync(w, card.Id, document.Id, ct);
        var dto = (await new FinancialCreditCardRepository(db).GetAsync(w, card.Id, ct))!;
        if (analysis.InputHash != InboxStatementAnalysisService.ComputeInputHash(document, dto, catalog, agentOptions))
            return await Stop("StaleAnalysis", "NeedsReview");
        var preview = analysis.Extraction!.Primary!;
        var invoices = await db.FinancialCreditCardInvoices.Where(i => i.WorkspaceId == w && i.CreditCardId == card.Id).ToListAsync(ct);
        var known = await db.FinancialCreditCardImportedInstallments.Where(i => i.WorkspaceId == w && i.CreditCardId == card.Id && i.IsHistorical).ToListAsync(ct);
        var normal = await db.FinancialCreditCardPurchases.Include(p => p.Installments).Where(p => p.WorkspaceId == w && p.CreditCardId == card.Id).ToListAsync(ct);
        if (known.Any(k => k.InvoiceId is null && k.Competence is null) || normal.Any(p => p.Status == CreditCardPurchaseStatus.Active
            && p.Installments.Any(i => i.Status == CreditCardInstallmentStatus.Active && i.InvoiceId is null))) return await Stop("UnassignedCardObligation");
        var existing = known.Select(k => new ExistingKnown(k.Id, k.Description, k.Amount, k.InstallmentNumber, k.InstallmentCount,
                k.Competence ?? Month(k.InvoiceId), k.DueDate ?? invoices.SingleOrDefault(i => i.Id == k.InvoiceId)?.DueDate,
                k.TransactionDate, k.Status == FinancialCreditCardImportedInstallmentStatus.Active, k))
            .Concat(normal.SelectMany(p => p.Installments.Select(i => new ExistingKnown(i.Id, p.Description, i.Amount, i.InstallmentNumber, p.InstallmentCount,
                Month(i.InvoiceId), invoices.SingleOrDefault(v => v.Id == i.InvoiceId)?.DueDate, p.PurchaseDate,
                p.Status == CreditCardPurchaseStatus.Active && i.Status == CreditCardInstallmentStatus.Active, null)))).ToArray();
        var assignments = new Dictionary<int, ExistingKnown>();
        foreach (var group in eligibility.Rows.GroupBy(r => Key(preview.Items[r.ItemIndex], r.Competence)))
        {
            var rows = group.OrderBy(r => r.ItemIndex).ToArray();
            var candidates = existing.Where(e => Key(e) == group.Key).OrderBy(e => e.Id).ToArray();
            if (candidates.Length == 0) continue;
            // Preserve multiplicity. Never merge two legitimate identical source occurrences into one obligation.
            if (candidates.Length != rows.Length) return await Stop("AmbiguousExistingInstallments");
            for (var n = 0; n < rows.Length; n++)
            {
                var row = rows[n]; var item = preview.Items[row.ItemIndex]; var match = candidates[n];
                if (!match.Active || match.Amount != decimal.Parse(item.Amount, CultureInfo.InvariantCulture)
                    || match.Due != row.DueDate || row.TransactionDate.HasValue && match.Date != row.TransactionDate)
                    return await Stop("ExistingInstallmentConflict");
                assignments.Add(row.ItemIndex, match);
            }
        }
        foreach (var row in eligibility.Rows)
        {
            var item = preview.Items[row.ItemIndex];
            var period = row.Competence.ToString("yyyy-MM", CultureInfo.InvariantCulture);
            var periodInvoices = invoices.Where(i => i.Competence == period).ToArray();
            if (periodInvoices.Length > 1) return await Stop("AmbiguousInvoiceCompetence");
            var invoice = periodInvoices.SingleOrDefault();
            if (invoice is not null && (invoice.DueDate != row.DueDate || item.Section == "CurrentInvoice"
                && preview.Header.ClosingDate is { } closing && invoice.ClosingDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) != closing))
                return await Stop("ExistingInvoiceCalendarConflict");
            FinancialCreditCardImportedInstallment? target;
            Guid entityId; string entityType;
            if (assignments.TryGetValue(row.ItemIndex, out var match))
            { target = match.Known; entityId = match.Id; entityType = target is null ? nameof(FinancialCreditCardInstallment) : nameof(FinancialCreditCardImportedInstallment); }
            else
            {
                var suggestion = analysis.Categories.SingleOrDefault(c => c.ItemIndex == row.ItemIndex);
                var category = suggestion is { Confidence: "High", IsProposal: false, CategoryId: { } id }
                    && catalog.Categories.Any(c => c.Id == id && c.IsActive && c.Type == CategoryType.Expense) ? suggestion.CategoryId : null;
                target = FinancialCreditCardImportedInstallment.CreateKnown(w, card.Id, category, item.OriginalDescription,
                    decimal.Parse(item.Amount, CultureInfo.InvariantCulture), item.InstallmentNumber, item.InstallmentCount,
                    row.Competence, row.DueDate, StatementKnownInstallmentImport.SourceSystem, $"{document.ContentHash}:row:{row.ItemIndex}", user, now, row.TransactionDate);
                db.FinancialCreditCardImportedInstallments.Add(target); entityId = target.Id; entityType = nameof(FinancialCreditCardImportedInstallment);
            }
            if (target is not null && invoice is not null && target.InvoiceId != invoice.Id)
            {
                if (invoice.OfficialVersion != 0 || await db.FinancialCreditCardInvoicePayments.AnyAsync(p => p.WorkspaceId == w && p.InvoiceId == invoice.Id && p.Status == FinancialCreditCardInvoicePaymentStatus.Active, ct)
                    || await db.FinancialPlannedInvoiceReconciliations.AnyAsync(p => p.WorkspaceId == w && p.CreditCardInvoiceId == invoice.Id && p.RemovedAt == null, ct))
                    return await Stop("ProtectedInvoiceComposition");
                target.AttachKnownInvoice(invoice);
            }
            var metadata = JsonSerializer.Serialize(new { document.Id, CardId = card.Id, row.ItemIndex, row.Competence, row.DueDate, item.Section,
                item.IncludedInCurrentTotal, item.Page, item.Evidence, item.TransactionDate, item.InstallmentNumber, item.InstallmentCount,
                row.AnchorItemIndex, CompetenceBasis = row.AnchorItemIndex is null ? "DocumentHeader" : "ExplicitNextInstallmentAndAuthorizedMonthlyCalendar",
                card.ClosingDay, card.DueDay, DocumentCompetence = preview.Header.Competence, DocumentDueDate = preview.Header.DueDate,
                ExistingObligation = assignments.ContainsKey(row.ItemIndex), DecisionHash = decision });
            db.FinancialBootstrapProvenance.Add(FinancialBootstrapProvenance.Create(w, StatementKnownInstallmentImport.SourceSystem,
                document.ContentHash, $"row:{row.ItemIndex}", decision, entityType, entityId, metadata, now));
        }
        db.FinancialBootstrapProvenance.Add(FinancialBootstrapProvenance.Create(w, StatementKnownInstallmentImport.SourceSystem,
            document.ContentHash, "document", decision, nameof(FinancialDocumentInboxItem), document.Id,
            JsonSerializer.Serialize(new { CardId = card.Id, ConfirmedByUserId = user, Workflow = StatementKnownInstallmentImport.Workflow,
                AssociationBasis = "AuthenticatedDocumentConfirmation", Rows = eligibility.Rows.Count,
                agentOptions.Provider, agentOptions.DocumentaryImport.Model, agentOptions.DocumentaryImport.ReasoningEffort,
                PromptVersion = CreditCardStatementAgentPrompt.DocumentaryVersion, SchemaVersion = "2.0" }), now));
        document.CompleteAnalysis("OpenAI.PurchasesOnly", preview.Header.Competence, card.Id, FinancialDocumentInboxStatus.ReadyToConfirm, now);
        document.MarkImported(card.Id, now);
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return new("Imported");

        DateOnly? Month(Guid? id) => invoices.SingleOrDefault(i => i.Id == id) is { } invoice
            ? DateOnly.ParseExact(invoice.Competence, "yyyy-MM", CultureInfo.InvariantCulture) : null;
    }

    private sealed record ExistingKnown(Guid Id, string Description, decimal Amount, int? Number, int? Count,
        DateOnly? Competence, DateOnly? Due, DateOnly? Date, bool Active, FinancialCreditCardImportedInstallment? Known);
    private static string Key(CreditCardStatementAgentItem item, DateOnly month) => $"{month:yyyy-MM}|{item.InstallmentNumber}|{item.InstallmentCount}|{StatementKnownInstallmentImport.DescriptionKey(item.OriginalDescription)}";
    private static string Key(ExistingKnown item) => $"{item.Competence:yyyy-MM}|{item.Number}|{item.Count}|{StatementKnownInstallmentImport.DescriptionKey(item.Description)}";

    // Explicit document-level continuation. The unique confirmation and original evidence remain immutable.
    private async Task<OfficialStatementConsolidationResult> LinkConfirmedKnownInvoicesAsync(FinancialDocumentInboxItem document,
        FinancialCreditCard card, InboxStatementAnalysis analysis, string decision, Guid user, DateTimeOffset now, IDbContextTransaction tx, CancellationToken ct)
    {
        async Task<OfficialStatementConsolidationResult> Stop(string code)
        { await tx.RollbackAsync(ct); db.ChangeTracker.Clear(); return new("Conflict", FailureCode: code); }
        var eligible = StatementKnownInstallmentImport.Evaluate(analysis.Extraction, card.Issuer, card.LastFourDigits, card.DueDay);
        if (!eligible.Eligible) return await Stop("ConfirmedDocumentContextChanged");
        var w = document.WorkspaceId;
        var journal = await db.FinancialBootstrapProvenance.Where(p => p.WorkspaceId == w && p.SourceSystem == StatementKnownInstallmentImport.SourceSystem
            && p.SourceDocument == document.ContentHash && p.ExternalReference.StartsWith("row:")).ToArrayAsync(ct);
        if (journal.Length != eligible.Rows.Count || journal.Any(p => p.ContentHash != decision)) return await Stop("ConfirmationJournalConflict");
        var ids = journal.Where(p => p.EntityType == nameof(FinancialCreditCardImportedInstallment)).Select(p => p.EntityId).ToArray();
        var known = await db.FinancialCreditCardImportedInstallments.Where(k => k.WorkspaceId == w && k.CreditCardId == card.Id && ids.Contains(k.Id)).ToListAsync(ct);
        var invoices = await db.FinancialCreditCardInvoices.Where(i => i.WorkspaceId == w && i.CreditCardId == card.Id).ToListAsync(ct);
        var normal = await db.FinancialCreditCardPurchases.Include(p => p.Installments).Where(p => p.WorkspaceId == w && p.CreditCardId == card.Id).ToListAsync(ct);
        var linked = new Dictionary<Guid, List<Guid>>();
        foreach (var row in eligible.Rows)
        {
            var entry = journal.SingleOrDefault(p => p.ExternalReference == $"row:{row.ItemIndex}");
            if (entry is null) return await Stop("ConfirmationJournalConflict");
            var item = analysis.Extraction!.Primary!.Items[row.ItemIndex];
            if (entry.EntityType == nameof(FinancialCreditCardInstallment))
            {
                var matches = (from p in normal from i in p.Installments where i.Id == entry.EntityId
                    && p.Status == CreditCardPurchaseStatus.Active && i.Status == CreditCardInstallmentStatus.Active
                    && i.InstallmentNumber == item.InstallmentNumber && p.InstallmentCount == item.InstallmentCount
                    && i.Amount == decimal.Parse(item.Amount, CultureInfo.InvariantCulture)
                    && StatementKnownInstallmentImport.DescriptionKey(p.Description) == StatementKnownInstallmentImport.DescriptionKey(item.OriginalDescription)
                    && (!row.TransactionDate.HasValue || p.PurchaseDate == row.TransactionDate)
                    && invoices.Any(v => v.Id == i.InvoiceId && v.Competence == row.Competence.ToString("yyyy-MM", CultureInfo.InvariantCulture) && v.DueDate == row.DueDate)
                    select i).Count();
                if (matches != 1) return await Stop("ConfirmedObligationChanged");
                continue;
            }
            if (entry.EntityType != nameof(FinancialCreditCardImportedInstallment)) return await Stop("ConfirmationJournalConflict");
            var target = known.SingleOrDefault(k => k.Id == entry.EntityId);
            if (target is null || target.Status != FinancialCreditCardImportedInstallmentStatus.Active || target.Competence != row.Competence
                || target.DueDate != row.DueDate || target.Amount != decimal.Parse(item.Amount, CultureInfo.InvariantCulture)
                || target.InstallmentNumber != item.InstallmentNumber || target.InstallmentCount != item.InstallmentCount
                || StatementKnownInstallmentImport.DescriptionKey(target.Description) != StatementKnownInstallmentImport.DescriptionKey(item.OriginalDescription)
                || row.TransactionDate.HasValue && target.TransactionDate != row.TransactionDate)
                return await Stop("ConfirmedObligationChanged");
            var periods = invoices.Where(i => i.Competence == row.Competence.ToString("yyyy-MM", CultureInfo.InvariantCulture)).ToArray();
            if (periods.Length > 1) return await Stop("AmbiguousInvoiceCompetence");
            var invoice = periods.SingleOrDefault();
            if (invoice is null) { if (target.InvoiceId is not null) return await Stop("ConfirmedInvoiceChanged"); continue; }
            if (invoice.DueDate != row.DueDate || item.Section == "CurrentInvoice" && analysis.Extraction!.Primary!.Header.ClosingDate is { } closing
                && invoice.ClosingDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) != closing) return await Stop("ExistingInvoiceCalendarConflict");
            if (target.InvoiceId == invoice.Id) continue;
            if (target.InvoiceId is not null || invoice.OfficialVersion != 0
                || await db.FinancialCreditCardInvoicePayments.AnyAsync(p => p.WorkspaceId == w && p.InvoiceId == invoice.Id && p.Status == FinancialCreditCardInvoicePaymentStatus.Active, ct)
                || await db.FinancialPlannedInvoiceReconciliations.AnyAsync(p => p.WorkspaceId == w && p.CreditCardInvoiceId == invoice.Id && p.RemovedAt == null, ct))
                return await Stop("ProtectedInvoiceComposition");
            target.AttachKnownInvoice(invoice);
            if (!linked.TryGetValue(invoice.Id, out var list)) linked[invoice.Id] = list = [];
            list.Add(target.Id);
        }
        if (linked.Count == 0) { await tx.RollbackAsync(ct); return new("AlreadyImported"); }
        foreach (var (invoice, items) in linked)
            db.FinancialBootstrapProvenance.Add(FinancialBootstrapProvenance.Create(w, StatementKnownInstallmentImport.SourceSystem,
                document.ContentHash, $"invoice:{invoice:N}", decision, nameof(FinancialCreditCardInvoice), invoice,
                JsonSerializer.Serialize(new { LinkedIds = items, LinkedByUserId = user }), now));
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return new("InvoicesLinked");
    }
}
