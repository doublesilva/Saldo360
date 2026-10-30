using Saldo360.Application.Budgets;
using Saldo360.Application.Pluggy;
using Saldo360.Application.Workspaces;
using Saldo360.Domain.Categories;
using Saldo360.Domain.Common;

namespace Saldo360.Application.Classification;

/// <summary>Onde a classificação deste registro é corrigida: lançamento, compra de cartão (inclusive a importada), previsão, ou em lugar nenhum (encargo de fatura, dívida).</summary>
public enum ClassificationAction { Transaction, CardPurchase, Planned, None }

/// <summary>
/// Um registro de despesa para revisar a classificação. <c>Scope</c> diz o alcance da correção: sempre o próprio registro (uma compra importada do banco é uma
/// cobrança, com uma parcela), nunca uma alteração em massa. <c>CanChangeCategory</c>: a categoria só se corrige aqui em registro importado da Pluggy (os demais
/// têm o seu formulário).
/// </summary>
public sealed record ClassificationReviewItem(ClassificationAction Action, Guid RecordId, Guid? CardId, DateOnly Date, string Description, decimal Amount, Guid? CategoryId, string? CategoryPath,
    ExpenseNature Nature, string? CardName, string? AccountName, bool Imported, bool CanChangeCategory, bool CanChangeNature, string Scope);

public sealed record ClassificationReviewPage(string Kind, DateOnly? From, DateOnly? To, bool WholeBudget, IReadOnlyList<ClassificationReviewItem> Items, int Total, int Page, int PageSize, int UnclassifiableCount);

public enum ClassificationReviewAccess { Allowed, NotFound, Invalid }

public sealed record ClassificationReviewResult(ClassificationReviewAccess Access, ClassificationReviewPage? Page, IDictionary<string, string[]>? Errors = null)
{
    public static ClassificationReviewResult Invalid(string field, string message) => new(ClassificationReviewAccess.Invalid, null, new Dictionary<string, string[]> { [field] = [message] });
}

/// <summary>
/// Lista de revisão da classificação (R04). Mostra exatamente os registros que originam as contagens de "sem tipo de despesa" (mesma lista do orçamento, inclusive
/// compras de cartão) e de "sem categoria segura" (lançamentos e compras importados pela Pluggy). É edição de classificação: nada aqui concilia nem confere documentos.
/// </summary>
public sealed class ClassificationReviewService(IFinancialWorkspaceRepository workspaces, IFinancialBudgetExecutionRepository facts, IPluggyCategorizationRepository categorization)
{
    public const string NatureKind = "nature";
    public const string CategoryKind = "category";
    public const int MaxPageSize = 100;

    public async Task<ClassificationReviewResult> ListAsync(Guid workspaceId, Guid userId, string? kind, DateOnly? from, DateOnly? to, int page, int pageSize, DateOnly today, CancellationToken ct)
    {
        if (await workspaces.GetOwnerRoleAsync(workspaceId, userId, ct) is null) return new(ClassificationReviewAccess.NotFound, null);
        kind ??= NatureKind;
        if (kind is not (NatureKind or CategoryKind)) return ClassificationReviewResult.Invalid("kind", "Use nature ou category.");
        if (from is { } f && to is { } t && f > t) return ClassificationReviewResult.Invalid("from", "O início do período não pode ser posterior ao fim.");
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);
        return kind == NatureKind ? await NatureAsync(workspaceId, from, to, page, pageSize, today, ct) : await CategoryAsync(workspaceId, page, pageSize, ct);
    }

    private async Task<ClassificationReviewResult> NatureAsync(Guid workspaceId, DateOnly? from, DateOnly? to, int page, int pageSize, DateOnly today, CancellationToken ct)
    {
        // Sem período: o mês corrente (o mesmo recorte do orçamento do mês).
        var start = from ?? new DateOnly(today.Year, today.Month, 1);
        var end = to ?? start.AddMonths(1).AddDays(-1);
        var (all, paths) = await facts.ReadFactsAsync(workspaceId, start, end.AddDays(1), ct);
        var undefined = all.Where(fact => fact.Nature == ExpenseNature.Undefined && fact.Amount > 0m).ToList();
        var classifiable = undefined.Where(fact => fact.IsClassifiable).OrderBy(fact => fact.Date).ThenBy(fact => fact.Description, StringComparer.CurrentCultureIgnoreCase).ThenBy(fact => fact.RecordId).ToList();
        var items = classifiable.Skip((page - 1) * pageSize).Take(pageSize).Select(fact => ToItem(fact, paths)).ToList();
        return new(ClassificationReviewAccess.Allowed, new(NatureKind, start, end, false, items, classifiable.Count, page, pageSize, undefined.Count - classifiable.Count));
    }

    private static ClassificationReviewItem ToItem(BudgetFact fact, IReadOnlyDictionary<Guid, string> paths)
    {
        var path = fact.CategoryId is { } id && paths.TryGetValue(id, out var p) ? p : null;
        return fact.Origin switch
        {
            BudgetFactOrigin.CardInstallment => new(ClassificationAction.CardPurchase, fact.PurchaseId ?? fact.RecordId, fact.CardId, fact.Date, fact.Description, fact.Amount, fact.CategoryId, path, fact.Nature,
                fact.CardName, null, fact.Imported, fact.Imported, true, "Esta compra (as parcelas herdam o tipo)"),
            BudgetFactOrigin.PlannedTransaction => new(ClassificationAction.Planned, fact.RecordId, null, fact.Date, fact.Description, fact.Amount, fact.CategoryId, path, fact.Nature,
                null, fact.AccountName, false, false, false, "Esta previsão (editada em Previsões)"),
            _ => new(ClassificationAction.Transaction, fact.RecordId, null, fact.Date, fact.Description, fact.Amount, fact.CategoryId, path, fact.Nature,
                null, fact.AccountName, fact.Imported, fact.Imported, true, "Este lançamento"),
        };
    }

    /// <summary>Lançamentos e compras importados pela Pluggy que ficaram sem categoria segura (orçamento inteiro, não só um mês).</summary>
    private async Task<ClassificationReviewResult> CategoryAsync(Guid workspaceId, int page, int pageSize, CancellationToken ct)
    {
        var pending = (await categorization.ListCandidatesAsync(workspaceId, ct)).Where(c => !c.IsManual && c.NeedsReview && c.PostingCategoryId is null && !c.IsIncome)
            .OrderBy(c => c.Date).ThenBy(c => c.Description, StringComparer.CurrentCultureIgnoreCase).ThenBy(c => c.PostingId).ToList();
        var items = pending.Skip((page - 1) * pageSize).Take(pageSize).Select(c => c.IsCardPurchase
            ? new ClassificationReviewItem(ClassificationAction.CardPurchase, c.RecordId ?? c.PostingId, c.CardId, c.Date, c.Description, c.Amount, c.CurrentCategoryId, null, c.Nature, null, null, true, true, true, "Esta compra (uma cobrança do banco)")
            : new ClassificationReviewItem(ClassificationAction.Transaction, c.RecordId ?? c.PostingId, null, c.Date, c.Description, c.Amount, c.CurrentCategoryId, null, c.Nature, null, null, true, true, true, "Este lançamento")).ToList();
        return new(ClassificationReviewAccess.Allowed, new(CategoryKind, null, null, true, items, pending.Count, page, pageSize, 0));
    }
}
