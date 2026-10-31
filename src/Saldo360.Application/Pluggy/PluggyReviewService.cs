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
        var result = await repository.ListReviewAsync(workspaceId, new(linkId, Math.Max(1, page), Math.Clamp(pageSize, 1, MaxPageSize)), ct);
        return PluggyResult<PluggyReviewPage>.Ok(await WithCandidatesAsync(workspaceId, result, ct));
    }

    /// <summary>Resposta de uma ação sobre vínculo: <c>Done</c> (efeito aplicado agora) ou <c>AlreadyDone</c> (repetição, nenhum efeito novo).</summary>
    public sealed record LinkActionView(string Outcome, PluggyApplySummary? Summary);

    public Task<PluggyResult<LinkActionView>> ConfirmTransferAsync(Guid workspaceId, Guid userId, Guid postingId, Guid? counterpartPostingId, CancellationToken ct) =>
        ActAsync(workspaceId, userId, counterpartPostingId, partner => engine.ConfirmTransferAsync(workspaceId, userId, postingId, partner, time.GetUtcNow(), ct), ct);

    public Task<PluggyResult<LinkActionView>> RejectTransferAsync(Guid workspaceId, Guid userId, Guid postingId, Guid? counterpartPostingId, CancellationToken ct) =>
        ActAsync(workspaceId, userId, counterpartPostingId, partner => engine.RejectTransferAsync(workspaceId, userId, postingId, partner, time.GetUtcNow(), ct), ct);

    public async Task<PluggyResult<LinkActionView>> UndoTransferAsync(Guid workspaceId, Guid userId, Guid postingId, CancellationToken ct)
    {
        if (!await CanAccessAsync(workspaceId, userId, ct)) return PluggyResult<LinkActionView>.NotFound();
        return Map(await engine.UndoTransferAsync(workspaceId, userId, postingId, time.GetUtcNow(), ct));
    }

    private async Task<PluggyResult<LinkActionView>> ActAsync(Guid workspaceId, Guid userId, Guid? counterpartPostingId, Func<Guid, Task<PluggyManualResult>> action, CancellationToken ct)
    {
        if (!await CanAccessAsync(workspaceId, userId, ct)) return PluggyResult<LinkActionView>.NotFound();
        if (counterpartPostingId is not { } partner || partner == Guid.Empty) return PluggyResult<LinkActionView>.Invalid("Informe a outra ponta da transferência (counterpartPostingId).");
        return Map(await action(partner));
    }

    private static PluggyResult<LinkActionView> Map(PluggyManualResult result) => result.Outcome switch
    {
        PluggyManualOutcome.Done or PluggyManualOutcome.AlreadyDone => PluggyResult<LinkActionView>.Ok(new(result.Outcome.ToString(), result.Summary)),
        PluggyManualOutcome.NotFound => PluggyResult<LinkActionView>.NotFound(),
        PluggyManualOutcome.Conflict => new(PluggyAccess.Conflict, Message: Describe(result.Reason)),
        _ => PluggyResult<LinkActionView>.Invalid(Describe(result.Reason)),
    };

    private static string Describe(string? reason) => (reason switch
    {
        PluggyTransferEligibility.Reasons.SameDirection => "As duas pontas têm o mesmo sentido: uma transferência precisa de uma saída e de uma entrada.",
        PluggyTransferEligibility.Reasons.SameAccount => "As pontas precisam ser de contas diferentes do orçamento.",
        PluggyTransferEligibility.Reasons.NotBankAccount => "Só contas bancárias integradas formam transferência.",
        PluggyTransferEligibility.Reasons.NotTransferEvidence => "Esta ponta não é um PIX, TED, DOC ou transferência entre contas.",
        PluggyTransferEligibility.Reasons.ManualDecision => "Uma das pontas tem decisão manual incompatível com ser transferência.",
        PluggyTransferEligibility.Reasons.DifferentAmount => "As pontas têm valores diferentes.",
        PluggyTransferEligibility.Reasons.DifferentDate => "As pontas têm datas diferentes; uma transferência tem uma única data e o Saldo360 não força uma data comum.",
        PluggyTransferEligibility.Reasons.IncompatibleOperation => "As operações bancárias das pontas não são compatíveis.",
        PluggyTransferEligibility.Reasons.EndUnavailable => "Uma das pontas não está disponível (pendente, ignorada ou sem efeito financeiro).",
        PluggyTransferEligibility.Reasons.EndUsedElsewhere => "Uma das pontas já está usada em outro vínculo.",
        "AlreadyLinked" => "Estas pontas já formam uma transferência: desfaça o vínculo em vez de rejeitá-lo.",
        "NotLinked" => "Esta ponta não faz parte de uma transferência aplicada.",
        _ => "Ação não permitida para estas pontas.",
    }) + (reason is null ? string.Empty : $" ({reason})");

    /// <summary>Prévia do reprocessamento: o motor roda numa transação sempre desfeita. Com valor e/ou período, devolve a cadeia completa dos movimentos que casam (rastreio).</summary>
    public async Task<PluggyResult<PluggyRecoveryPreview>> PreviewAsync(Guid workspaceId, Guid userId, decimal? amount, DateOnly? from, DateOnly? to, int? take, CancellationToken ct)
    {
        if (!await CanAccessAsync(workspaceId, userId, ct)) return PluggyResult<PluggyRecoveryPreview>.NotFound();
        if (from is { } start && to is { } end && start > end) return PluggyResult<PluggyRecoveryPreview>.Invalid("O início do período não pode ser posterior ao fim.");
        if (amount is <= 0) return PluggyResult<PluggyRecoveryPreview>.Invalid("O valor deve ser positivo.");
        return PluggyResult<PluggyRecoveryPreview>.Ok(await engine.PreviewAsync(workspaceId, userId, time.GetUtcNow(), new(amount, from, to, take ?? 100), ct));
    }

    /// <summary>Para cada item de vínculo, as pontas de mesmo valor (até um dia de distância) em outras contas e se podem ser confirmadas, pelas mesmas regras da confirmação.</summary>
    private async Task<PluggyReviewPage> WithCandidatesAsync(Guid workspaceId, PluggyReviewPage page, CancellationToken ct)
    {
        if (page.Items.All(i => i.ReviewKind != "Link")) return page;
        var today = Saldo360.Domain.Common.BusinessCalendar.Today(time.GetUtcNow());
        var rejected = await repository.ListRejectedPairsAsync(workspaceId, ct);
        var pool = (await repository.ListTransferPoolAsync(workspaceId, ct)).Select(row =>
        {
            var movement = PluggyFinancialMapper.Decide(row.Facts, today, row.Resolution);
            return (Row: row, Movement: movement, Side: new TransferSide(row.PostingId, row.Facts.LinkId, row.FinancialAccountId, row.Facts.LinkKind, movement.Disposition, movement.IsDebit,
                movement.Amount, movement.Date, PluggyTransferEligibility.OperationOf(row.Facts.OperationType), row.State, row.Kind, row.CounterpartPostingId, row.Resolution));
        }).ToList();
        var items = page.Items.Select(item =>
        {
            var ownIndex = pool.FindIndex(p => p.Row.PostingId == item.PostingId);
            if (item.ReviewKind != "Link" || ownIndex < 0) return item;
            var own = pool[ownIndex];
            var candidates = new List<PluggyTransferCandidateView>();
            foreach (var other in pool)
            {
                if (other.Row.PostingId == own.Row.PostingId || other.Movement.IsDebit == own.Movement.IsDebit || other.Movement.Amount != own.Movement.Amount
                    || other.Row.Facts.LinkId == own.Row.Facts.LinkId || Math.Abs(other.Movement.Date.DayNumber - own.Movement.Date.DayNumber) > 1) continue;
                if (rejected.Contains(Saldo360.Domain.Pluggy.PluggyTransferRejection.Order(own.Row.PostingId, other.Row.PostingId))) continue;
                var blocked = PluggyTransferEligibility.AlreadyLinkedToEachOther(own.Side, other.Side) ? "AlreadyLinked"
                    : PluggyTransferEligibility.UsedByAnotherLink(own.Side, other.Side) ? PluggyTransferEligibility.Reasons.EndUsedElsewhere
                    : PluggyTransferEligibility.Check(own.Side, other.Side);
                candidates.Add(new(other.Row.PostingId, other.Row.Facts.LinkId, other.Row.LinkDisplayName, other.Movement.Date, other.Movement.Amount.ToString("F2", System.Globalization.CultureInfo.InvariantCulture),
                    other.Movement.IsDebit ? "Expense" : "Income", other.Movement.Description, other.Row.Facts.OperationType, other.Row.State is PluggyPostingState.Applied,
                    other.Movement.Date == own.Movement.Date, blocked is null, blocked));
            }
            return item with { Candidates = candidates.OrderBy(c => c.SameDay ? 0 : 1).ThenBy(c => c.Date).Take(5).ToList() };
        }).ToList();
        return page with { Items = items };
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
