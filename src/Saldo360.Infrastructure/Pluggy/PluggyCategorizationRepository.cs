using Microsoft.EntityFrameworkCore;
using Saldo360.Application.Classification;
using Saldo360.Application.Pluggy;
using Saldo360.Domain.Categories;
using Saldo360.Domain.CreditCards;
using Saldo360.Domain.Pluggy;
using Saldo360.Domain.Transactions;
using Saldo360.Infrastructure.Persistence;

namespace Saldo360.Infrastructure.Pluggy;

/// <summary>
/// Persistência da categorização dos lançamentos gerados pela Pluggy. Só toca a categoria (do lançamento/compra e do rastro) e a auditoria da
/// classificação; valores, datas, contas, faturas e saldos nunca são alterados. Cada escrita é serializada por orçamento (mesma trava do motor).
/// </summary>
internal sealed class PluggyCategorizationRepository(Saldo360DbContext db) : IPluggyCategorizationRepository
{
    private const string FallbackKey = "outros";

    public async Task<IReadOnlyList<MappableCategory>> ListCategoriesAsync(Guid workspaceId, CancellationToken ct) =>
        await db.FinancialCategories.AsNoTracking().Where(c => c.WorkspaceId == workspaceId).ToMappable(db).ToListAsync(ct);

    public async Task<IReadOnlyList<PluggyCategorizationCandidate>> ListCandidatesAsync(Guid workspaceId, CancellationToken ct)
    {
        var rows = await (from p in db.PluggyPostings.AsNoTracking()
                          join i in db.PluggyImportedTransactions.AsNoTracking() on p.ImportedTransactionId equals i.Id
                          where p.WorkspaceId == workspaceId && p.State == PluggyPostingState.Applied
                                && (p.Kind == PluggyPostingKind.BankMovement || p.Kind == PluggyPostingKind.CardPurchase)
                          select new
                          {
                              p.Id, p.Kind, p.CategoryId, p.CategorySource, p.CategoryNeedsReview, p.CategoryClassifiedAt, p.CategoryMethodVersion, p.CategoryReason,
                              p.FinancialTransactionId, p.CreditCardPurchaseId, p.AppliedAmount, p.AppliedDate, p.AppliedType,
                              i.Description, i.DescriptionRaw, i.Category, i.OperationType, i.Type,
                          }).ToListAsync(ct);

        var txIds = rows.Where(r => r.FinancialTransactionId != null).Select(r => r.FinancialTransactionId!.Value).ToList();
        var purchaseIds = rows.Where(r => r.CreditCardPurchaseId != null).Select(r => r.CreditCardPurchaseId!.Value).ToList();
        var txCategories = await db.FinancialTransactions.AsNoTracking().Where(t => t.WorkspaceId == workspaceId && txIds.Contains(t.Id) && !t.IsCancelled)
            .Select(t => new { t.Id, t.CategoryId, t.Nature }).ToDictionaryAsync(t => t.Id, t => (t.CategoryId, t.Nature), ct);
        var purchaseCategories = await db.FinancialCreditCardPurchases.AsNoTracking().Where(p => p.WorkspaceId == workspaceId && purchaseIds.Contains(p.Id) && p.Status == CreditCardPurchaseStatus.Active)
            .Select(p => new { p.Id, p.CategoryId, p.Nature, p.CreditCardId }).ToDictionaryAsync(p => p.Id, p => (p.CategoryId, p.Nature, p.CreditCardId), ct);
        var fallbackIds = (await db.FinancialCategories.AsNoTracking().Where(c => c.WorkspaceId == workspaceId && c.Type == CategoryType.Expense).Select(c => new { c.Id, c.Name }).ToListAsync(ct))
            .Where(c => FinancialCategoryMatching.Key(c.Name) == FallbackKey).Select(c => c.Id).ToHashSet();

        var locked = await LockedPurchaseIdsAsync(workspaceId, purchaseIds, ct);
        var list = new List<PluggyCategorizationCandidate>(rows.Count);
        foreach (var r in rows)
        {
            Guid? current; Guid? recordId; Guid? cardId = null; var nature = Saldo360.Domain.Categories.ExpenseNature.Undefined;
            if (r.Kind == PluggyPostingKind.CardPurchase)
            {
                if (r.CreditCardPurchaseId is not { } pid || !purchaseCategories.TryGetValue(pid, out var pc) || locked.Contains(pid)) continue; // cancelada ou em fatura oficial (imutável): fora
                current = pc.CategoryId; recordId = pid; cardId = pc.CreditCardId; nature = pc.Nature;
            }
            else
            {
                if (r.FinancialTransactionId is not { } tid || !txCategories.TryGetValue(tid, out var tc)) continue; // lançamento cancelado: fora
                current = tc.CategoryId; recordId = tid; nature = tc.Nature;
            }
            list.Add(new PluggyCategorizationCandidate
            {
                PostingId = r.Id, Kind = r.Kind, IsIncome = r.AppliedType == CategoryType.Income, Description = r.Description ?? r.DescriptionRaw ?? string.Empty,
                Amount = r.AppliedAmount ?? 0m, Date = r.AppliedDate ?? default, ExternalCategory = r.Category, OperationType = r.OperationType,
                CurrentCategoryId = current, RecordId = recordId, CardId = cardId, Nature = nature, CurrentIsFallback = current is { } c && fallbackIds.Contains(c), PostingCategoryId = r.CategoryId, Source = r.CategorySource,
                NeedsReview = r.CategoryNeedsReview, ClassifiedAt = r.CategoryClassifiedAt, MethodVersion = r.CategoryMethodVersion, Reason = r.CategoryReason,
            });
        }
        return list;
    }

    public async Task<int> EnsureCategoriesAsync(Guid workspaceId, Guid userId, IReadOnlyCollection<(string Name, CategoryType Type)> categories, DateTimeOffset now, CancellationToken ct)
    {
        return await InTransactionAsync(workspaceId, async () =>
        {
            var existing = await db.FinancialCategories.AsNoTracking().Where(c => c.WorkspaceId == workspaceId).Select(c => new { c.Name, c.Type }).ToListAsync(ct);
            var created = 0;
            foreach (var (name, type) in categories)
            {
                if (!FinancialCategory.TryNormalizeName(name, out var normalized, out _)) continue;
                if (existing.Any(c => c.Type == type && FinancialCategoryMatching.Key(c.Name) == FinancialCategoryMatching.Key(normalized))) continue;
                db.FinancialCategories.Add(FinancialCategory.Create(workspaceId, normalized, type, userId, now));
                existing.Add(new { Name = normalized, Type = type });
                created++;
            }
            await db.SaveChangesAsync(ct);
            return created;
        }, ct);
    }

    public async Task<PluggyCategorizationApplied> ApplyAsync(Guid workspaceId, Guid userId, IReadOnlyList<PluggyCategoryDecision> decisions, DateTimeOffset now, CancellationToken ct)
    {
        return await InTransactionAsync(workspaceId, async () =>
        {
            var ids = decisions.Select(d => d.PostingId).ToList();
            var postings = await db.PluggyPostings.Where(p => p.WorkspaceId == workspaceId && ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
            var txIds = postings.Values.Where(p => p.FinancialTransactionId != null).Select(p => p.FinancialTransactionId!.Value).ToList();
            var purchaseIds = postings.Values.Where(p => p.CreditCardPurchaseId != null).Select(p => p.CreditCardPurchaseId!.Value).ToList();
            var transactions = await db.FinancialTransactions.Where(t => t.WorkspaceId == workspaceId && txIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, ct);
            var purchases = await db.FinancialCreditCardPurchases.Where(p => p.WorkspaceId == workspaceId && purchaseIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
            var categories = await db.FinancialCategories.AsNoTracking().Where(c => c.WorkspaceId == workspaceId).ToDictionaryAsync(c => c.Id, ct);
            var locked = await LockedPurchaseIdsAsync(workspaceId, purchaseIds, ct);

            int applied = 0, recorded = 0, skipped = 0;
            foreach (var decision in decisions)
            {
                if (!postings.TryGetValue(decision.PostingId, out var posting) || posting.State != PluggyPostingState.Applied) { skipped++; continue; }
                var isPurchase = posting.Kind == PluggyPostingKind.CardPurchase;
                var type = posting.AppliedType ?? CategoryType.Expense;

                Guid? current;
                FinancialTransaction? transaction = null; FinancialCreditCardPurchase? purchase = null;
                if (isPurchase)
                {
                    if (posting.CreditCardPurchaseId is not { } pid || !purchases.TryGetValue(pid, out purchase) || purchase.Status != CreditCardPurchaseStatus.Active || locked.Contains(pid)) { skipped++; continue; }
                    current = purchase.CategoryId;
                }
                else
                {
                    if (posting.FinancialTransactionId is not { } tid || !transactions.TryGetValue(tid, out transaction) || transaction.IsCancelled) { skipped++; continue; }
                    current = transaction.CategoryId;
                }
                // O usuário (ou outra execução) mexeu depois da leitura: a decisão dele prevalece e nada é gravado.
                if (posting.CategorySource == PluggyCategorySource.User || current != decision.ExpectedCurrentCategoryId) { skipped++; continue; }

                if (decision.CategoryId is { } categoryId)
                {
                    // Categoria precisa existir neste orçamento, estar disponível (ativa e, numa subcategoria, a principal também) e ter o tipo do lançamento.
                    if (!categories.TryGetValue(categoryId, out var category) || !category.IsActive || category.Type != type
                        || category.ParentCategoryId is { } parentId && !(categories.TryGetValue(parentId, out var parent) && parent.IsActive)) { skipped++; continue; }
                    if (current != categoryId)
                    {
                        if (isPurchase) purchase!.Recategorize(categoryId, userId, now); else transaction!.Recategorize(categoryId, userId, now);
                    }
                    posting.RecordClassification(categoryId, decision.Source, decision.Method, decision.Reason, false, now);
                    applied++;
                }
                else
                {
                    posting.RecordClassification(null, decision.Source, decision.Method, decision.Reason, decision.NeedsReview, now);
                    recorded++;
                }
            }
            await db.SaveChangesAsync(ct);
            return new PluggyCategorizationApplied(applied, recorded, skipped, 0);
        }, ct);
    }

    /// <summary>Compras que fazem parte de uma fatura oficial: o banco as torna imutáveis (nem a categoria muda), então ficam fora da categorização.</summary>
    private async Task<HashSet<Guid>> LockedPurchaseIdsAsync(Guid workspaceId, List<Guid> purchaseIds, CancellationToken ct) =>
        (await (from i in db.FinancialCreditCardInstallments.AsNoTracking()
                join invoice in db.FinancialCreditCardInvoices.AsNoTracking() on i.InvoiceId equals invoice.Id
                where i.WorkspaceId == workspaceId && purchaseIds.Contains(i.PurchaseId) && invoice.OfficialVersion > 0
                select i.PurchaseId).Distinct().ToListAsync(ct)).ToHashSet();

    public async Task<PluggyCategorizationCounts> CountAsync(Guid workspaceId, CancellationToken ct)
    {
        var rows = await db.PluggyPostings.AsNoTracking()
            .Where(p => p.WorkspaceId == workspaceId && p.State == PluggyPostingState.Applied && (p.Kind == PluggyPostingKind.BankMovement || p.Kind == PluggyPostingKind.CardPurchase))
            .GroupBy(p => new { p.CategorySource, p.CategoryNeedsReview, Evaluated = p.CategoryClassifiedAt != null, ByRule = p.CategoryMethodVersion != null && p.CategoryMethodVersion.StartsWith("rule:") })
            .Select(g => new { g.Key.CategorySource, g.Key.CategoryNeedsReview, g.Key.Evaluated, g.Key.ByRule, Count = g.Count() }).ToListAsync(ct);
        return new(rows.Sum(r => r.Count),
            rows.Where(r => r.CategorySource == PluggyCategorySource.User).Sum(r => r.Count),
            rows.Where(r => r.CategorySource is PluggyCategorySource.Pluggy or PluggyCategorySource.Mapped).Sum(r => r.Count),
            rows.Where(r => r.CategorySource == PluggyCategorySource.None && r.ByRule && !r.CategoryNeedsReview).Sum(r => r.Count),
            rows.Where(r => r.CategorySource == PluggyCategorySource.AI).Sum(r => r.Count),
            rows.Where(r => r.CategorySource == PluggyCategorySource.None && r.CategoryNeedsReview).Sum(r => r.Count),
            rows.Where(r => r.CategorySource == PluggyCategorySource.None && !r.Evaluated).Sum(r => r.Count));
    }

    private async Task<T> InTransactionAsync<T>(Guid workspaceId, Func<Task<T>> work, CancellationToken ct)
    {
        var ownsTransaction = db.Database.CurrentTransaction is null;
        var tx = ownsTransaction ? await db.Database.BeginTransactionAsync(ct) : null;
        try
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM financial_workspaces WHERE id = {workspaceId} FOR UPDATE", ct);
            var result = await work();
            if (tx is not null) await tx.CommitAsync(ct);
            return result;
        }
        catch
        {
            if (tx is not null) await tx.RollbackAsync(CancellationToken.None);
            db.ChangeTracker.Clear();
            throw;
        }
        finally
        {
            if (tx is not null) await tx.DisposeAsync();
        }
    }
}
