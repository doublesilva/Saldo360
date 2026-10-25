using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Saldo360.Application.CreditCards;
using Saldo360.Domain.Categories;
using Saldo360.Domain.Common;
using Saldo360.Domain.CreditCards;
using Saldo360.Domain.Workspaces;

namespace Saldo360.Infrastructure.CreditCards;

internal sealed partial class CreditCardPdfConfirmationRepository
{
    public async Task<OfficialStatementConsolidationResult> ConsolidateInboxAsync(Guid workspaceId, Guid cardId, Guid documentId, Guid userId, DateTimeOffset now, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        try
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM financial_workspaces WHERE id = {workspaceId} FOR UPDATE", ct);
            db.ChangeTracker.Clear(); // Re-read all authoritative inputs after obtaining the writer/reset root lock.
            if (workspaceId != CardEmailSearchAuthorization.AuthorizedPersonalId || !await db.FinancialWorkspaces.AnyAsync(w => w.Id == workspaceId && w.OwnerUserId == userId && w.IsActive && w.Type == FinancialWorkspaceType.Personal, ct)) return await Abort("NotFound");
            var card = await db.FinancialCreditCards.SingleOrDefaultAsync(c => c.WorkspaceId == workspaceId && c.Id == cardId && !c.IsArchived, ct);
            var document = await db.FinancialDocumentInboxItems.SingleOrDefaultAsync(d => d.WorkspaceId == workspaceId && d.Id == documentId, ct);
            if (card is null || document is null) return await Abort("NotFound");
            if (document.Status == FinancialDocumentInboxStatus.Analyzing) return await Abort("Busy", "DocumentAnalysisInProgress");
            if (document.Status is FinancialDocumentInboxStatus.Ignored or FinancialDocumentInboxStatus.Conflict
                || document.CreditCardId is { } associated && associated != cardId) return await Abort("Conflict", "DocumentAssociationConflict");
            var analysis = InboxStatementAnalysisService.Read(document.StatementAnalysisJson);
            if (analysis is null || analysis.CardId != cardId || storage is null || agentOptions is null) return await Abort("NeedsReview", "AnalysisRequired");
            await using (var file = await storage.OpenReadAsync(document.StorageReference, ct))
            {
                if (file is null || file.Length <= 0 || file.Length > CreditCardPdfPreviewService.MaxFileBytes) return await Abort("NeedsReview", "StoredDocumentUnavailable");
                var actualHash = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, ct));
                if (actualHash != document.ContentHash) return await Abort("Conflict", "DocumentHashMismatch");
            }
            if (analysis.Workflow == StatementKnownInstallmentImport.Workflow)
                return await ConfirmKnownDocumentAsync(document, card, analysis, userId, now, tx, ct);
            if (agentOptions.DocumentaryImport.Enabled) return await Abort("NeedsReview", "DocumentaryAnalysisRequired");
            if (await db.FinancialBootstrapProvenance.AnyAsync(p => p.WorkspaceId == workspaceId && p.SourceSystem == StatementKnownInstallmentImport.SourceSystem
                && p.SourceDocument == document.ContentHash && p.ExternalReference == "document", ct)) return await Abort("Conflict", "DocumentAlreadyConfirmedAsKnownInstallments");
            var decisionHash = OfficialStatementAnalysisValidation.DecisionHash(analysis);
            var previousDocument = await db.FinancialCreditCardInvoiceConsolidations.SingleOrDefaultAsync(v => v.WorkspaceId == workspaceId && v.CreditCardId == cardId && v.DocumentHash == document.ContentHash, ct);
            if (previousDocument is not null)
            {
                var existingInvoice = await db.FinancialCreditCardInvoices.SingleAsync(i => i.WorkspaceId == workspaceId && i.Id == previousDocument.InvoiceId, ct);
                if (document.Status != FinancialDocumentInboxStatus.Imported || document.CreditCardId != cardId
                    || previousDocument.DecisionHash != decisionHash || existingInvoice.OfficialVersion != previousDocument.Version
                    || previousDocument.StateHash != OfficialStatementAnalysisValidation.Hash(await CompositionSnapshotAsync(workspaceId, cardId, existingInvoice.Id, existingInvoice.Competence, ct)))
                    return await Abort("Conflict", "OfficialVersionOrDecisionsChanged");
                await tx.RollbackAsync(ct);
                return new("AlreadyImported", existingInvoice.Id, previousDocument.Version, Money.Format(previousDocument.OfficialTotal));
            }
            if (document.Status == FinancialDocumentInboxStatus.Imported) return await Abort("Conflict", "ImportedWithoutMatchingVersion");
            if (document.Status is not (FinancialDocumentInboxStatus.ReadyForReview or FinancialDocumentInboxStatus.ReadyToConfirm)) return await Abort("NeedsReview", "AnalysisRequired");
            if (!OfficialStatementAnalysisValidation.Revalidate(analysis, cardId, out var preview)) return await Abort("NeedsReview", "AnalysisNotReliable");
            var header = preview!.Header;
            var identityResolver = new CreditCardStatementResolver(new FinancialCreditCardRepository(db));
            var resolution = header.IdentityEvidence is not null
                ? await identityResolver.ResolveDocumentAsync(workspaceId, header, ct)
                : await identityResolver.ResolveAsync(workspaceId, header.Issuer, header.CardLastDigits, null, ct);
            if (resolution.Status != CreditCardStatementResolutionStatus.Matched || resolution.CardId != cardId) return await Abort("NeedsReview", "CardIdentityUnresolved");
            var catalog = await new StatementCategoryContextRepository(db).ReadAsync(workspaceId, cardId, documentId, ct);
            var cardDto = (await new FinancialCreditCardRepository(db).GetAsync(workspaceId, cardId, ct))!;
            if (analysis.InputHash != InboxStatementAnalysisService.ComputeInputHash(document, cardDto, catalog, agentOptions)) return await Abort("NeedsReview", "StaleAnalysis");
            if (!DateOnly.TryParseExact(header.Competence, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var competence)
                || !DateOnly.TryParseExact(header.ClosingDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var closing)
                || !DateOnly.TryParseExact(header.DueDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var due)
                || due.ToString("yyyy-MM", CultureInfo.InvariantCulture) != header.Competence) return await Abort("NeedsReview", "InvoiceCalendarRequired");
            var cycle = CreditCardInvoiceCalendarCalculator.ForInstallment(closing, 1, card.ClosingDay, card.DueDay);
            if (cycle.ClosingDate != closing || cycle.DueDate != due || cycle.Competence != header.Competence) return await Abort("NeedsReview", "InvoiceCalendarConflict");
            var officialTotal = decimal.Parse(header.OfficialTotal!, CultureInfo.InvariantCulture);
            var periodInvoices = await db.FinancialCreditCardInvoices.Where(i => i.WorkspaceId == workspaceId && i.CreditCardId == cardId && i.Competence == header.Competence).ToListAsync(ct);
            if (periodInvoices.Count > 1) return await Abort("Conflict", "AmbiguousInvoiceCompetence");
            var invoice = periodInvoices.SingleOrDefault();
            if (invoice is not null && (invoice.ClosingDate != closing || invoice.DueDate != due)) return await Abort("Conflict", "ExistingInvoiceCalendarConflict");
            if (invoice is null)
            {
                invoice = FinancialCreditCardInvoice.Create(workspaceId, cardId, header.Competence!, cycle.CycleStartDate, closing, due, now);
                db.FinancialCreditCardInvoices.Add(invoice);
            }
            if (await db.FinancialCreditCardInvoicePayments.AnyAsync(p => p.WorkspaceId == workspaceId && p.InvoiceId == invoice.Id && p.Status == FinancialCreditCardInvoicePaymentStatus.Active, ct)
                || await db.FinancialPlannedInvoiceReconciliations.AnyAsync(r => r.WorkspaceId == workspaceId && r.CreditCardInvoiceId == invoice.Id && r.RemovedAt == null, ct))
                return await Abort("Conflict", "ExistingPaymentOrAccountingBinding");
            var before = await CompositionSnapshotAsync(workspaceId, cardId, invoice.Id, invoice.Competence, ct);
            var priorAnalyses = new List<InboxStatementAnalysis>();
            var priorAudits = await db.FinancialCreditCardInvoiceConsolidations.AsNoTracking().Where(v => v.WorkspaceId == workspaceId && v.CreditCardId == cardId).OrderByDescending(v => v.CreatedAt).ThenByDescending(v => v.Version).Select(v => v.SnapshotJson).ToListAsync(ct);
            foreach (var priorAudit in priorAudits)
            {
                using var priorSnapshot = JsonDocument.Parse(priorAudit);
                InboxStatementAnalysis? priorAnalysis = null;
                if (priorSnapshot.RootElement.TryGetProperty("EffectiveAnalysis", out var effectiveAnalysis)) priorAnalysis = effectiveAnalysis.Deserialize<InboxStatementAnalysis>();
                else if (priorSnapshot.RootElement.TryGetProperty("Analysis", out var storedAnalysis)) priorAnalysis = storedAnalysis.Deserialize<InboxStatementAnalysis>();
                if (priorAnalysis is not null) priorAnalyses.Add(priorAnalysis);
            }
            var purchases = await db.FinancialCreditCardPurchases.Include(p => p.Installments).Where(p => p.WorkspaceId == workspaceId && p.CreditCardId == cardId && p.Status == CreditCardPurchaseStatus.Active).ToListAsync(ct);
            var known = await db.FinancialCreditCardImportedInstallments.Where(k => k.WorkspaceId == workspaceId && k.CreditCardId == cardId && k.IsHistorical && k.Status == FinancialCreditCardImportedInstallmentStatus.Active).ToListAsync(ct);
            if (known.Any(k => k.InvoiceId is null && k.Competence is null)) return await Abort("NeedsReview", "UnassignedCardObligation");
            var invoices = await db.FinancialCreditCardInvoices.Where(i => i.WorkspaceId == workspaceId && i.CreditCardId == cardId).ToDictionaryAsync(i => i.Id, ct);
            invoices[invoice.Id] = invoice;
            var scheduleBefore = JsonSerializer.Serialize(new { Normal = purchases.SelectMany(p => p.Installments).OrderBy(i => i.Id), Known = known.OrderBy(k => k.Id) });
            var retained = new HashSet<Guid>(); var matchedFutures = new HashSet<int>();
            var categoryCache = catalog.Categories.ToList();
            var effectiveCategories = analysis.Categories.ToArray();
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config('saldo360.official_invoice', {invoice.Id.ToString()}, true)", ct);
            for (var index = 0; index < preview.Items.Count; index++)
            {
                var item = preview.Items[index];
                if (!item.IncludedInCurrentTotal) continue;
                if (item.Section == "FutureInstallments" || item.Type == "Payment") return await Abort("NeedsReview", "InvalidCurrentComponent");
                var amount = decimal.Parse(item.Amount, CultureInfo.InvariantCulture);
                if (amount == 0m || item.OriginalDescription.Length > 200) return await Abort("NeedsReview", "InvalidComponentAmountOrDescription");
                DateOnly? date = DateOnly.TryParseExact(item.TransactionDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ? parsed : null;
                if (item.Type is "Purchase" or "Installment")
                {
                    if (item.Nature != "Debit") return await Abort("NeedsReview", "PurchaseNatureConflict");
                    var category = ResolveCategory(index);
                    if (category is null) return await Abort("NeedsReview", "CategoryUnresolved");
                    var number = item.InstallmentNumber ?? 1; var count = item.InstallmentCount ?? 1;
                    var normalMatches = (from p in purchases from i in p.Installments
                        where i.Status == CreditCardInstallmentStatus.Active && i.InvoiceId == invoice.Id && i.InstallmentNumber == number && p.InstallmentCount == count
                            && SameDescription(p.Description, item.OriginalDescription) && (!date.HasValue || p.PurchaseDate == date)
                        select (Purchase: p, Installment: i)).ToList();
                    var knownMatches = known.Where(k => (k.InvoiceId == invoice.Id || k.InvoiceId == null && k.Competence == competence)
                        && k.InstallmentNumber == number && k.InstallmentCount == count && SameDescription(k.Description, item.OriginalDescription)
                        && (!date.HasValue || !k.TransactionDate.HasValue || k.TransactionDate == date)).ToList();
                    if (normalMatches.Count + knownMatches.Count > 1) return await Abort("NeedsReview", "AmbiguousInstallmentMatch");
                    if (normalMatches.Count == 1)
                    {
                        var match = normalMatches[0];
                        if (!retained.Add(match.Installment.Id)) return await Abort("NeedsReview", "DuplicateComponentMatch");
                        match.Installment.ApplyOfficial(amount, category.Id);
                        foreach (var future in match.Purchase.Installments.Where(i => i.Status == CreditCardInstallmentStatus.Active && i.InstallmentNumber > number))
                        {
                            if (future.InvoiceId is not { } fi || !invoices.TryGetValue(fi, out var futureInvoice)) return await Abort("NeedsReview", "UnassignedFutureInstallment");
                            if (futureInvoice.Competence != competence.AddMonths(future.InstallmentNumber - number).ToString("yyyy-MM", CultureInfo.InvariantCulture)) return await Abort("NeedsReview", "InstallmentChronologyConflict");
                            var explicitRows = FutureRows(item, future.InstallmentNumber);
                            if (explicitRows.Count > 1) return await Abort("NeedsReview", "AmbiguousFutureInstallment");
                            var futureAmount = explicitRows.Count == 1 ? decimal.Parse(explicitRows[0].Item.Amount, CultureInfo.InvariantCulture) : amount;
                            if (futureInvoice.OfficialVersion > 0)
                            { if (explicitRows.Count == 1 && futureAmount != future.Amount) return await Abort("Conflict", "OfficialFutureInstallmentImmutable"); }
                            else
                            {
                                if (await HasProtectedFinancialLink(fi)) return await Abort("Conflict", "FuturePaymentOrBinding");
                                var futureCategory = explicitRows.Count == 1 ? ResolveCategory(explicitRows[0].Index) : null;
                                if (explicitRows.Count == 1 && futureCategory is null) return await Abort("NeedsReview", "FutureCategoryUnresolved");
                                future.ApplyOfficial(futureAmount, futureCategory?.Id ?? future.OfficialCategoryId ?? match.Purchase.CategoryId);
                            }
                            foreach (var row in explicitRows) if (!matchedFutures.Add(row.Index)) return await Abort("NeedsReview", "AmbiguousFutureOwnership");
                        }
                    }
                    else if (knownMatches.Count == 1)
                    {
                        var match = knownMatches[0]; if (!retained.Add(match.Id)) return await Abort("NeedsReview", "DuplicateComponentMatch");
                        match.ApplyOfficial(amount, category.Id, match.InvoiceId is null ? invoice : null);
                        var group = Group(match);
                        if (number < count && group is null) return await Abort("NeedsReview", "InstallmentOriginGroupRequired");
                        var pendingGroup = known.Where(k => k.InstallmentNumber > number && k.InstallmentCount == count && Group(k) == group && group is not null).ToArray();
                        if (pendingGroup.GroupBy(k => k.InstallmentNumber).Any(g => g.Count() != 1) || pendingGroup.Length != count - number)
                            return await Abort("NeedsReview", "IncompleteInstallmentSchedule");
                        foreach (var future in known.Where(k => k.InstallmentNumber > number && k.InstallmentCount == count && Group(k) == group && group is not null))
                        {
                            if (!SameDescription(future.Description, match.Description)) return await Abort("NeedsReview", "InstallmentGroupDescriptionConflict");
                            var futureCompetence = future.Competence?.ToString("yyyy-MM", CultureInfo.InvariantCulture) ?? (future.InvoiceId is { } fid && invoices.TryGetValue(fid, out var inv) ? inv.Competence : null);
                            if (futureCompetence != competence.AddMonths(future.InstallmentNumber!.Value - number).ToString("yyyy-MM", CultureInfo.InvariantCulture)) return await Abort("NeedsReview", "InstallmentChronologyConflict");
                            var explicitRows = FutureRows(item, future.InstallmentNumber!.Value);
                            if (explicitRows.Count > 1) return await Abort("NeedsReview", "AmbiguousFutureInstallment");
                            var futureAmount = explicitRows.Count == 1 ? decimal.Parse(explicitRows[0].Item.Amount, CultureInfo.InvariantCulture) : amount;
                            if (future.InvoiceId is { } officialId && invoices[officialId].OfficialVersion > 0)
                            { if (explicitRows.Count == 1 && futureAmount != future.Amount) return await Abort("Conflict", "OfficialFutureInstallmentImmutable"); }
                            else
                            {
                                if (future.InvoiceId is { } protectedId && await HasProtectedFinancialLink(protectedId)) return await Abort("Conflict", "FuturePaymentOrBinding");
                                var futureCategory = explicitRows.Count == 1 ? ResolveCategory(explicitRows[0].Index) : null;
                                if (explicitRows.Count == 1 && futureCategory is null) return await Abort("NeedsReview", "FutureCategoryUnresolved");
                                future.ApplyOfficial(futureAmount, futureCategory?.Id ?? future.CategoryId ?? category.Id);
                            }
                            foreach (var row in explicitRows) if (!matchedFutures.Add(row.Index)) return await Abort("NeedsReview", "AmbiguousFutureOwnership");
                        }
                    }
                    else
                    {
                        if (number != 1 || count != 1) return await Abort("NeedsReview", "InstallmentLinkRequired");
                        if (date is null || CreditCardInvoiceCalendarCalculator.ForInstallment(date.Value, 1, card.ClosingDay, card.DueDay).Competence != invoice.Competence)
                            return await Abort("NeedsReview", "PurchaseDateOrCycleConflict");
                        var purchase = FinancialCreditCardPurchase.Create(workspaceId, cardId, category.Id, item.OriginalDescription, date.Value, amount, 1, userId, now);
                        purchase.Installments.Single().AssignInvoice(invoice.Id); db.FinancialCreditCardPurchases.Add(purchase); retained.Add(purchase.Installments.Single().Id);
                    }
                }
                else
                {
                    if (date is null || !Enum.TryParse<FinancialCreditCardInvoiceAdjustmentType>(item.Type, out var type)
                        || (type is FinancialCreditCardInvoiceAdjustmentType.Credit or FinancialCreditCardInvoiceAdjustmentType.Refund ? item.Nature != "Credit" : item.Nature != "Debit"))
                        return await Abort("NeedsReview", "UnsupportedOrUndatedFinancialComponent");
                    var adjustment = FinancialCreditCardInvoiceAdjustment.Create(workspaceId, cardId, invoice.Id, type,
                        Enum.Parse<FinancialCreditCardInvoiceAdjustmentNature>(item.Nature), date.Value, item.OriginalDescription, amount,
                        $"official:{document.ContentHash}:{index}", type is FinancialCreditCardInvoiceAdjustmentType.Fee or FinancialCreditCardInvoiceAdjustmentType.Credit or FinancialCreditCardInvoiceAdjustmentType.Refund ? GenericAdjustmentCategory()?.Id : null, userId, now);
                    if (type is FinancialCreditCardInvoiceAdjustmentType.Fee or FinancialCreditCardInvoiceAdjustmentType.Credit or FinancialCreditCardInvoiceAdjustmentType.Refund && adjustment.CategoryId is null)
                        return await Abort("NeedsReview", "AdjustmentCategoryUnresolved");
                    db.FinancialCreditCardInvoiceAdjustments.Add(adjustment); retained.Add(adjustment.Id);
                }
            }
            if (preview.Items.Select((item, index) => (item, index)).Any(x => x.item.Section == "FutureInstallments" && !matchedFutures.Contains(x.index)))
                return await Abort("NeedsReview", "UnmatchedExplicitFutureInstallment");
            foreach (var i in purchases.SelectMany(p => p.Installments).Where(i => i.InvoiceId == invoice.Id && i.Status == CreditCardInstallmentStatus.Active && !retained.Contains(i.Id))) i.SupersedeOfficial();
            foreach (var purchase in purchases.Where(p => p.Installments.All(i => i.Status == CreditCardInstallmentStatus.Cancelled)
                && !p.Installments.Any(i => i.InvoiceId is { } id && id != invoice.Id && invoices[id].OfficialVersion > 0))) purchase.Cancel(userId, now);
            foreach (var k in known.Where(k => (k.InvoiceId == invoice.Id || k.InvoiceId == null && k.Competence == competence) && !retained.Contains(k.Id))) k.Cancel(userId, now);
            foreach (var adjustment in await db.FinancialCreditCardInvoiceAdjustments.Where(a => a.WorkspaceId == workspaceId && a.InvoiceId == invoice.Id && a.Status == FinancialCreditCardInvoiceAdjustmentStatus.Active).ToListAsync(ct)) adjustment.Cancel(userId, now);
            await db.SaveChangesAsync(ct);
            var persistedTotal = await FinancialCreditCardInvoiceTotals.TotalAsync(db, workspaceId, invoice.Id, ct);
            if (persistedTotal != officialTotal) return await Abort("Conflict", "PersistedCompositionMismatch");
            var after = await CompositionSnapshotAsync(workspaceId, cardId, invoice.Id, invoice.Competence, ct);
            var version = invoice.OfficialVersion + 1;
            db.FinancialCreditCardInvoiceConsolidations.Add(FinancialCreditCardInvoiceConsolidation.Create(workspaceId, cardId, invoice.Id, documentId,
                document.ContentHash, decisionHash, OfficialStatementAnalysisValidation.Hash(after),
                JsonSerializer.Serialize(new { Before = before, After = after, ScheduleBefore = scheduleBefore,
                    ScheduleAfter = new { Normal = purchases.SelectMany(p => p.Installments).OrderBy(i => i.Id), Known = known.OrderBy(k => k.Id) }, Analysis = analysis,
                    EffectiveAnalysis = analysis with { Categories = effectiveCategories } }), version, officialTotal, userId, now));
            invoice.CloseOfficial(version, officialTotal, now);
            document.CompleteAnalysis(document.Parser ?? analysis.Extraction!.PrimarySource, invoice.Competence, cardId, FinancialDocumentInboxStatus.ReadyForReview, now);
            document.MarkImported(cardId, now);
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
            return new(version == 1 ? "Imported" : "NewVersionConsolidated", invoice.Id, version, Money.Format(officialTotal));

            FinancialCategory? ResolveCategory(int row)
            {
                var proposals = analysis.Categories.Where(c => c.ItemIndex == row).ToArray();
                if (proposals.Length != 1) return null;
                var selected = proposals[0];
                if (selected.Source != "Manual" && !analysis.ManualOverrides.ContainsKey(selected.Key))
                {
                    var current = preview.Items[row];
                    foreach (var priorAnalysis in priorAnalyses)
                    {
                        if (priorAnalysis.Extraction?.Primary is not { } priorPreview) continue;
                        var incompleteManualIdentity = priorAnalysis.Categories.Where(c => c.Source == "Manual" || priorAnalysis.ManualOverrides.ContainsKey(c.Key))
                            .Where(c => c.ItemIndex >= 0 && c.ItemIndex < priorPreview.Items.Count)
                            .Select(c => priorPreview.Items[c.ItemIndex])
                            .Any(old => SameDescription(old.OriginalDescription, current.OriginalDescription)
                                && old.InstallmentNumber == current.InstallmentNumber && old.InstallmentCount == current.InstallmentCount
                                && (old.TransactionDate is null || current.TransactionDate is null));
                        if (incompleteManualIdentity) return null; // Do not silently replace an applicable-looking correction whose identity cannot be proved.
                        var priorManual = priorAnalysis.Categories.Where(c => c.CategoryId.HasValue && (c.Source == "Manual" || priorAnalysis.ManualOverrides.ContainsKey(c.Key))
                            && c.ItemIndex >= 0 && c.ItemIndex < priorPreview.Items.Count)
                            .Where(c => { var old = priorPreview.Items[c.ItemIndex]; return SameDescription(old.OriginalDescription, current.OriginalDescription)
                                && old.TransactionDate is not null && old.TransactionDate == current.TransactionDate && old.InstallmentNumber == current.InstallmentNumber && old.InstallmentCount == current.InstallmentCount; })
                            .Select(c => c.CategoryId!.Value).Distinct().ToArray();
                        if (priorManual.Length > 1) return null;
                        if (priorManual.Length == 1)
                        {
                            var persisted = categoryCache.SingleOrDefault(c => c.Id == priorManual[0] && c.IsActive && c.Type == CategoryType.Expense);
                            if (persisted is not null)
                            {
                                var selectedIndex = Array.FindIndex(effectiveCategories, c => c.Key == selected.Key && c.ItemIndex == row);
                                effectiveCategories[selectedIndex] = selected with { CategoryId = persisted.Id, CategoryName = persisted.Name, Source = "Manual", IsProposal = false, Confidence = "High" };
                            }
                            return persisted is null ? null : FinancialCategoryReference(persisted.Id);
                        }
                    }
                }
                var matching = categoryCache.Where(c => c.Type == CategoryType.Expense && StatementCategorization.Normalize(c.Name) == StatementCategorization.Normalize(selected.CategoryName)).ToArray();
                if (selected.CategoryId is { } id)
                {
                    var c = categoryCache.SingleOrDefault(c => c.Id == id && c.IsActive && c.Type == CategoryType.Expense);
                    return c is null ? null : db.FinancialCategories.Local.SingleOrDefault(c => c.Id == id) ?? FinancialCategoryReference(id);
                }
                if (!selected.IsProposal || !FinancialCategory.TryNormalizeName(selected.CategoryName, out var name, out _) || name.Any(char.IsControl)) return null;
                if (matching.Length > 1 || matching.Length == 1 && !matching[0].IsActive) return null;
                if (matching.Length == 1) return db.FinancialCategories.Local.SingleOrDefault(c => c.Id == matching[0].Id) ?? FinancialCategoryReference(matching[0].Id);
                var created = FinancialCategory.Create(workspaceId, name, CategoryType.Expense, userId, now);
                db.FinancialCategories.Add(created); categoryCache.Add(new(created.Id, created.Name, created.Type, created.IsActive, now)); return created;
            }
            FinancialCategory? GenericAdjustmentCategory()
            {
                // T16.2 categorizes purchases/installments. Unattributed fees and credits use the approved generic fallback;
                // carried/financed balances are liabilities, not new category consumption.
                var matching = categoryCache.Where(c => c.Type == CategoryType.Expense && StatementCategorization.Normalize(c.Name) == "OUTROS").ToArray();
                if (matching.Length > 1 || matching.Length == 1 && !matching[0].IsActive) return null;
                if (matching.Length == 1) return FinancialCategoryReference(matching[0].Id);
                var category = FinancialCategory.Create(workspaceId, "Outros", CategoryType.Expense, userId, now);
                db.Add(category); categoryCache.Add(new(category.Id, category.Name, category.Type, category.IsActive, now)); return category;
            }
            FinancialCategory FinancialCategoryReference(Guid id) => db.FinancialCategories.Find(id)!;
            List<(CreditCardStatementAgentItem Item, int Index)> FutureRows(CreditCardStatementAgentItem current, int number) => preview.Items.Select((item, index) => (Item: item, Index: index))
                .Where(x => x.Item.Section == "FutureInstallments" && x.Item.Nature == "Debit" && (x.Item.Type is "Installment" or "Purchase") && x.Item.InstallmentNumber == number && x.Item.InstallmentCount == current.InstallmentCount
                    && SameDescription(x.Item.OriginalDescription, current.OriginalDescription) && (x.Item.TransactionDate is null || current.TransactionDate is null || x.Item.TransactionDate == current.TransactionDate)).ToList();
            async Task<bool> HasProtectedFinancialLink(Guid id) => await db.FinancialCreditCardInvoicePayments.AnyAsync(p => p.WorkspaceId == workspaceId && p.InvoiceId == id && p.Status == FinancialCreditCardInvoicePaymentStatus.Active, ct)
                || await db.FinancialPlannedInvoiceReconciliations.AnyAsync(r => r.WorkspaceId == workspaceId && r.CreditCardInvoiceId == id && r.RemovedAt == null, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { await tx.RollbackAsync(CancellationToken.None); db.ChangeTracker.Clear(); throw; }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or JsonException)
        { return await Abort("NeedsReview", "ConsolidationPreflightFailed"); }
        catch (Exception) { return await Abort("Conflict", "ConsolidationRolledBack"); }

        async Task<OfficialStatementConsolidationResult> Abort(string status, string? code = null)
        { await tx.RollbackAsync(ct); db.ChangeTracker.Clear(); return new(status, FailureCode: code); }
    }

    private static bool SameDescription(string left, string right) => StatementCategorization.Normalize(left) == StatementCategorization.Normalize(right);
    private static string? Group(FinancialCreditCardImportedInstallment item) => item.SourceSystem is { } source && item.ExternalReference is { } reference && reference.LastIndexOf(":installment:", StringComparison.Ordinal) is var index && index > 0 ? source + ":" + reference[..index] : null;

    internal async Task<string> CompositionSnapshotAsync(Guid workspaceId, Guid cardId, Guid invoiceId, string competence, CancellationToken ct)
    {
        var month = DateOnly.ParseExact(competence, "yyyy-MM", CultureInfo.InvariantCulture);
        var normal = await (from i in db.FinancialCreditCardInstallments.AsNoTracking() join p in db.FinancialCreditCardPurchases.AsNoTracking() on i.PurchaseId equals p.Id
            where i.WorkspaceId == workspaceId && p.CreditCardId == cardId && i.InvoiceId == invoiceId && i.Status == CreditCardInstallmentStatus.Active && p.Status == CreditCardPurchaseStatus.Active
            orderby i.Id select new { i.Id, p.Description, p.PurchaseDate, i.InstallmentNumber, i.Amount, CategoryId = i.OfficialCategoryId ?? p.CategoryId }).ToArrayAsync(ct);
        var historical = await db.FinancialCreditCardImportedInstallments.AsNoTracking().Where(k => k.WorkspaceId == workspaceId && k.CreditCardId == cardId && k.IsHistorical && k.Status == FinancialCreditCardImportedInstallmentStatus.Active && (k.InvoiceId == invoiceId || k.InvoiceId == null && k.Competence == month))
            .OrderBy(k => k.Id).Select(k => new { k.Id, k.Description, k.Amount, k.CategoryId, k.InvoiceId, k.InstallmentNumber, k.InstallmentCount }).ToArrayAsync(ct);
        var adjustments = await db.FinancialCreditCardInvoiceAdjustments.AsNoTracking().Where(a => a.WorkspaceId == workspaceId && a.InvoiceId == invoiceId && a.Status == FinancialCreditCardInvoiceAdjustmentStatus.Active)
            .OrderBy(a => a.Id).Select(a => new { a.Id, a.Type, a.Nature, a.Amount, a.Description, a.CategoryId }).ToArrayAsync(ct);
        return JsonSerializer.Serialize(new { Normal = normal, Historical = historical, Adjustments = adjustments });
    }
}
