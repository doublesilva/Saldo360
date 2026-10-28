using Saldo360.Application.Workspaces;
using Saldo360.Domain.Pluggy;

namespace Saldo360.Application.Pluggy;

/// <summary>
/// Fila de revisão: movimentos importados sem classificação segura ficam fora de receitas, despesas e saldos até o dono do orçamento
/// decidir. A decisão do usuário prevalece sobre a classificação automática e é preservada nas sincronizações seguintes.
/// </summary>
public sealed class PluggyReviewService(IFinancialWorkspaceRepository workspaces, IPluggyFinancialRepository repository, IPluggyPostingEngine engine,
    PluggyOptions options, TimeProvider time)
{
    public const int MaxPageSize = 100;

    public async Task<PluggyResult<PluggyReviewPage>> ListAsync(Guid workspaceId, Guid userId, Guid? linkId, int page, int pageSize, CancellationToken ct)
    {
        if (!await CanAccessAsync(workspaceId, userId, ct)) return PluggyResult<PluggyReviewPage>.NotFound();
        if (linkId is { } id && await repository.FindLinkAsync(workspaceId, id, ct) is null) return PluggyResult<PluggyReviewPage>.NotFound();
        return PluggyResult<PluggyReviewPage>.Ok(await repository.ListReviewAsync(workspaceId, new(linkId, Math.Max(1, page), Math.Clamp(pageSize, 1, MaxPageSize)), ct));
    }

    public async Task<PluggyResult<PluggyReviewItemView>> ResolveAsync(Guid workspaceId, Guid userId, Guid postingId, string? resolution, CancellationToken ct)
    {
        if (!await CanAccessAsync(workspaceId, userId, ct) || await repository.FindReviewTargetAsync(workspaceId, postingId, ct) is not { } target)
            return PluggyResult<PluggyReviewItemView>.NotFound();
        if (resolution is null || !Enum.GetNames<PluggyResolution>().Contains(resolution, StringComparer.Ordinal) || !Enum.TryParse<PluggyResolution>(resolution, out var parsed))
            return PluggyResult<PluggyReviewItemView>.Invalid("Decisão inválida. Use AsIncome, AsExpense, AsCardPurchase ou Ignore.");
        if (target.State != PluggyPostingState.NeedsReview)
            return new(PluggyAccess.Conflict, Message: "Este movimento não está em revisão.");
        if (!AllowedResolutions(target.LinkKind, target.Type).Contains(parsed.ToString()))
            return PluggyResult<PluggyReviewItemView>.Invalid("Esta decisão não se aplica a este movimento.");

        var now = time.GetUtcNow();
        await repository.SaveResolutionAsync(workspaceId, postingId, parsed, userId, now, ct);
        await engine.ApplyAsync(workspaceId, userId, now, ct);
        return await repository.GetReviewItemAsync(workspaceId, postingId, ct) is { } item ? PluggyResult<PluggyReviewItemView>.Ok(item) : PluggyResult<PluggyReviewItemView>.NotFound();
    }

    /// <summary>Decisões permitidas conforme o sentido do movimento: receita só para crédito, despesa/compra só para débito.</summary>
    public static IReadOnlyList<string> AllowedResolutions(PluggyLinkedAccountKind kind, string? type)
    {
        var isDebit = string.Equals(type, "DEBIT", StringComparison.OrdinalIgnoreCase);
        var isCredit = string.Equals(type, "CREDIT", StringComparison.OrdinalIgnoreCase);
        var list = new List<string>();
        if (kind == PluggyLinkedAccountKind.Bank && isCredit) list.Add(nameof(PluggyResolution.AsIncome));
        if (kind == PluggyLinkedAccountKind.Bank && isDebit) list.Add(nameof(PluggyResolution.AsExpense));
        if (kind == PluggyLinkedAccountKind.Card && isDebit) list.Add(nameof(PluggyResolution.AsCardPurchase));
        list.Add(nameof(PluggyResolution.Ignore));
        return list;
    }

    private async Task<bool> CanAccessAsync(Guid workspaceId, Guid userId, CancellationToken ct) =>
        options.Enabled && await workspaces.GetOwnerRoleAsync(workspaceId, userId, ct) is not null;
}
