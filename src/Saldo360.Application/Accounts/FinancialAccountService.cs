using Saldo360.Application.Workspaces;
using Saldo360.Domain.Accounts;
using Saldo360.Domain.Common;

namespace Saldo360.Application.Accounts;

/// <summary>
/// Contas do workspace pessoal. Somente o proprietário pode consultar, cadastrar e editar.
/// Workspace vem da rota, autoria vem da sessão; participação familiar não concede acesso.
/// </summary>
public sealed class FinancialAccountService(
    IFinancialWorkspaceRepository workspaces,
    IFinancialAccountRepository accounts,
    TimeProvider timeProvider)
{
    private const string ResponsibleField = "responsibleUserId";
    private const string InvalidResponsible = "Responsável inválido.";
    private const string PrimaryField = "isPrimary";
    private const string ReplacementField = "replacementPrimaryAccountId";
    private const string PrimaryMustBeBank = "Somente uma conta bancária (corrente ou poupança) ativa pode ser a conta principal.";
    private const string PrimaryNeedsReplacement =
        "Esta é a conta principal. Informe a conta substituta (replacementPrimaryAccountId) ou remova a condição de principal (isPrimary=false) antes de desativá-la.";

    public async Task<AccountResult> CreateAsync(Guid workspaceId, Guid userId, AccountInput input, CancellationToken cancellationToken)
    {
        if (!await IsParticipantAsync(workspaceId, userId, cancellationToken))
        {
            return AccountResult.NotFound;
        }

        var (details, errors) = await ValidateAsync(workspaceId, input, cancellationToken);
        if (input.IsPrimary == true && details is not null && !FinancialAccount.IsBankType(details.Type))
        {
            errors[PrimaryField] = [PrimaryMustBeBank];
            details = null;
        }

        if (details is null)
        {
            return AccountResult.Invalid(errors);
        }

        var account = FinancialAccount.Create(workspaceId, details, userId, timeProvider.GetUtcNow());
        return await accounts.RunExclusiveAsync(workspaceId, async () =>
        {
            await accounts.AddAsync(account, cancellationToken);
            if (input.IsPrimary == true)
            {
                await MakePrimaryAsync(workspaceId, account, cancellationToken);
            }

            return AccountResult.Ok((await accounts.GetAsync(workspaceId, account.Id, cancellationToken))!);
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<FinancialAccountDto>?> ListAsync(
        Guid workspaceId, Guid userId, bool includeInactive, CancellationToken cancellationToken) =>
        await IsParticipantAsync(workspaceId, userId, cancellationToken)
            ? await accounts.ListAsync(workspaceId, includeInactive, cancellationToken)
            : null;

    public async Task<FinancialAccountDto?> GetAsync(Guid workspaceId, Guid accountId, Guid userId, CancellationToken cancellationToken) =>
        await IsParticipantAsync(workspaceId, userId, cancellationToken)
            ? await accounts.GetAsync(workspaceId, accountId, cancellationToken)
            : null;

    /// <summary>
    /// Atualiza campos cadastrais e situação. Família, criador, moeda e data de criação não mudam. A conta principal só pode ser
    /// desativada (ou deixar de ser bancária) junto com uma substituta ou com a remoção explícita da condição de principal.
    /// </summary>
    public async Task<AccountResult> UpdateAsync(
        Guid workspaceId, Guid accountId, Guid userId, AccountInput input, bool? isActive, CancellationToken cancellationToken)
    {
        if (!await IsParticipantAsync(workspaceId, userId, cancellationToken))
        {
            return AccountResult.NotFound;
        }

        var (details, errors) = await ValidateAsync(workspaceId, input, cancellationToken);
        if (isActive is null)
        {
            errors["isActive"] = ["Informe se a conta está ativa."];
        }

        if (details is null || isActive is null)
        {
            return AccountResult.Invalid(errors);
        }

        return await accounts.RunExclusiveAsync(workspaceId, async () =>
        {
            if (await accounts.FindAsync(workspaceId, accountId, cancellationToken) is not { } account)
            {
                return AccountResult.NotFound;
            }

            var stillEligible = isActive.Value && FinancialAccount.IsBankType(details.Type);
            var leaving = account.IsPrimary && !stillEligible;
            FinancialAccount? replacement = null;
            if (leaving && input.IsPrimary != false && input.ReplacementPrimaryAccountId is null)
            {
                return AccountResult.Invalid(new Dictionary<string, string[]> { ["isActive"] = [PrimaryNeedsReplacement] });
            }

            if (input.IsPrimary == true && !stillEligible)
            {
                return AccountResult.Invalid(new Dictionary<string, string[]> { [PrimaryField] = [PrimaryMustBeBank] });
            }

            if (input.ReplacementPrimaryAccountId is { } replacementId)
            {
                if (!leaving || input.IsPrimary is not null)
                {
                    return AccountResult.Invalid(new Dictionary<string, string[]> { [ReplacementField] = ["A substituta só vale ao desativar a conta principal."] });
                }

                replacement = replacementId == accountId ? null : await accounts.FindAsync(workspaceId, replacementId, cancellationToken);
                if (replacement is not { IsEligiblePayer: true })
                {
                    return AccountResult.Invalid(new Dictionary<string, string[]> { [ReplacementField] = ["A substituta precisa ser outra conta bancária ativa do orçamento."] });
                }
            }

            var becomesPrimary = input.IsPrimary == true && !account.IsPrimary;
            if (becomesPrimary)
            {
                // A principal anterior é desmarcada e gravada antes da nova, para respeitar o índice único.
                await ClearCurrentPrimaryAsync(workspaceId, account.Id, cancellationToken);
            }

            if (leaving || input.IsPrimary == false)
            {
                account.ClearPrimary();
            }

            account.Update(details, isActive.Value);
            await accounts.SaveChangesAsync(cancellationToken);
            if (becomesPrimary)
            {
                account.MarkPrimary();
                await accounts.SaveChangesAsync(cancellationToken);
            }
            else if (replacement is not null)
            {
                replacement.MarkPrimary();
                await accounts.SaveChangesAsync(cancellationToken);
            }

            return AccountResult.Ok((await accounts.GetAsync(workspaceId, account.Id, cancellationToken))!);
        }, cancellationToken);
    }

    /// <summary>
    /// Define a conta como a principal do orçamento, substituindo a anterior de forma atômica. Só conta bancária ativa. Não altera saldo,
    /// movimentações nem a conta pagadora dos cartões. Idempotente para a conta que já é a principal.
    /// </summary>
    public async Task<AccountResult> SetPrimaryAsync(Guid workspaceId, Guid accountId, Guid userId, CancellationToken cancellationToken)
    {
        if (!await IsParticipantAsync(workspaceId, userId, cancellationToken))
        {
            return AccountResult.NotFound;
        }

        return await accounts.RunExclusiveAsync(workspaceId, async () =>
        {
            if (await accounts.FindAsync(workspaceId, accountId, cancellationToken) is not { } account)
            {
                return AccountResult.NotFound;
            }

            if (!account.IsEligiblePayer)
            {
                return AccountResult.Invalid(new Dictionary<string, string[]> { [PrimaryField] = [PrimaryMustBeBank] });
            }

            if (!account.IsPrimary)
            {
                await MakePrimaryAsync(workspaceId, account, cancellationToken);
            }

            return AccountResult.Ok((await accounts.GetAsync(workspaceId, account.Id, cancellationToken))!);
        }, cancellationToken);
    }

    /// <summary>Remove explicitamente a condição de conta principal (o orçamento fica sem principal). Idempotente.</summary>
    public async Task<AccountResult> ClearPrimaryAsync(Guid workspaceId, Guid accountId, Guid userId, CancellationToken cancellationToken)
    {
        if (!await IsParticipantAsync(workspaceId, userId, cancellationToken))
        {
            return AccountResult.NotFound;
        }

        return await accounts.RunExclusiveAsync(workspaceId, async () =>
        {
            if (await accounts.FindAsync(workspaceId, accountId, cancellationToken) is not { } account)
            {
                return AccountResult.NotFound;
            }

            if (account.IsPrimary)
            {
                account.ClearPrimary();
                await accounts.SaveChangesAsync(cancellationToken);
            }

            return AccountResult.Ok((await accounts.GetAsync(workspaceId, account.Id, cancellationToken))!);
        }, cancellationToken);
    }

    private async Task MakePrimaryAsync(Guid workspaceId, FinancialAccount account, CancellationToken cancellationToken)
    {
        await ClearCurrentPrimaryAsync(workspaceId, account.Id, cancellationToken);
        account.MarkPrimary();
        await accounts.SaveChangesAsync(cancellationToken);
    }

    private async Task ClearCurrentPrimaryAsync(Guid workspaceId, Guid exceptAccountId, CancellationToken cancellationToken)
    {
        if (await accounts.FindPrimaryAsync(workspaceId, cancellationToken) is { } current && current.Id != exceptAccountId)
        {
            current.ClearPrimary();
            await accounts.SaveChangesAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Saldo realizado no fechamento de <paramref name="asOf"/> (padrão: hoje em America/Sao_Paulo). Data futura é
    /// recusada. Sem saldo inicial, ou antes da data de referência, devolve indisponível (nunca zero). Conta
    /// inativa também é consultável. As somas vêm agregadas do banco; aqui só se combina em decimal.
    /// </summary>
    public async Task<BalanceResult> GetBalanceAsync(
        Guid workspaceId, Guid accountId, Guid userId, DateOnly? asOf, CancellationToken cancellationToken)
    {
        if (!await IsParticipantAsync(workspaceId, userId, cancellationToken))
        {
            return new BalanceResult(BalanceAccess.NotFound, null, null);
        }

        var today = BusinessCalendar.Today(timeProvider.GetUtcNow());
        var date = asOf ?? today;
        if (date > today)
        {
            return new BalanceResult(BalanceAccess.Invalid, null, new Dictionary<string, string[]>
            {
                ["asOf"] = ["A data não pode ser futura: o saldo realizado só considera movimentações já ocorridas."],
            });
        }

        var balance = await accounts.ReadConsistentAsync(
            () => ReadBalanceAsync(workspaceId, accountId, date, cancellationToken), cancellationToken);
        return balance is null
            ? new BalanceResult(BalanceAccess.NotFound, null, null)
            : new BalanceResult(BalanceAccess.Allowed, balance, null);
    }

    /// <summary>
    /// Saldo projetado até <paramref name="through"/> (hoje ou futuro). Data-base = hoje em America/Sao_Paulo, capturada
    /// uma vez. Parte do saldo realizado no fechamento da data-base e soma as previsões PENDENTES até a data-alvo:
    /// vencidas (antes de hoje) e do período (hoje..through), inclusive anteriores ao saldo inicial e com conta ou
    /// categoria inativas. Previsão realizada não entra (seu lançamento efetivo já está no realizado); cancelada também
    /// não. Tudo num único snapshot do banco. Saldo realizado indisponível → todos os valores nulos, com o motivo.
    /// </summary>
    public async Task<ProjectionResult> GetProjectionAsync(
        Guid workspaceId, Guid accountId, Guid userId, DateOnly through, CancellationToken cancellationToken)
    {
        if (!await IsParticipantAsync(workspaceId, userId, cancellationToken))
        {
            return new ProjectionResult(BalanceAccess.NotFound, null, null);
        }

        var baseDate = BusinessCalendar.Today(timeProvider.GetUtcNow());
        if (through < baseDate)
        {
            return new ProjectionResult(BalanceAccess.Invalid, null, new Dictionary<string, string[]>
            {
                ["through"] = ["A data-alvo deve ser hoje ou futura: a projeção parte do saldo realizado de hoje."],
            });
        }

        var projection = await accounts.ReadConsistentAsync<AccountProjectionDto?>(async () =>
        {
            if (await ReadBalanceAsync(workspaceId, accountId, baseDate, cancellationToken) is not { } balance)
            {
                return null;
            }

            if (balance.Balance is not { } realized)
            {
                return new AccountProjectionDto(
                    accountId, baseDate, through, null, null, null, null, null, null, balance.UnavailableReason);
            }

            var planned = await accounts.SumPendingPlannedAsync(workspaceId, accountId, baseDate, through, cancellationToken);
            var creditCard = await accounts.SumCreditCardObligationsAsync(workspaceId, accountId, through, baseDate, cancellationToken);
            var projected = FinancialBalanceCalculator.Project(realized, planned);
            projected -= creditCard.Outstanding;
            return new AccountProjectionDto(
                accountId, baseDate, through, realized, planned.OverdueIncome, planned.OverdueExpense,
                planned.PlannedIncome, planned.PlannedExpense, projected, null, creditCard.Outstanding, !creditCard.HasUnallocated);
        }, cancellationToken);

        return projection is null
            ? new ProjectionResult(BalanceAccess.NotFound, null, null)
            : new ProjectionResult(BalanceAccess.Allowed, projection, null);
    }

    /// <summary>
    /// Saldo realizado no fechamento de <paramref name="date"/> (chamar dentro de ReadConsistentAsync). null = conta
    /// inexistente na família. Sem saldo inicial ou antes da referência: indisponível (nunca zero).
    /// </summary>
    private async Task<AccountBalanceDto?> ReadBalanceAsync(
        Guid workspaceId, Guid accountId, DateOnly date, CancellationToken cancellationToken)
    {
        if (await accounts.GetOpeningBalanceAsync(workspaceId, accountId, cancellationToken) is not { } opening)
        {
            return null;
        }

        if (opening is not { Amount: { } amount, Date: { } referenceDate })
        {
            return Unavailable(BalanceUnavailableReason.OpeningBalanceNotConfigured);
        }

        if (date < referenceDate)
        {
            return Unavailable(BalanceUnavailableReason.BeforeOpeningBalanceDate);
        }

        var totals = await accounts.SumMovementsAsync(workspaceId, accountId, referenceDate, date, cancellationToken);
        var balance = FinancialBalanceCalculator.ApplyMovements(
            amount, totals.Income, totals.Expense, totals.TransferIn, totals.TransferOut);
        return new AccountBalanceDto(
            accountId, date, amount, referenceDate, totals.Income, totals.Expense, totals.TransferIn, totals.TransferOut,
            balance, null, opening.Source);

        AccountBalanceDto Unavailable(BalanceUnavailableReason reason) =>
            new(accountId, date, opening.Amount, opening.Date, null, null, null, null, null, reason, opening.Source);
    }

    private async Task<bool> IsParticipantAsync(Guid workspaceId, Guid userId, CancellationToken cancellationToken) =>
        await workspaces.GetOwnerRoleAsync(workspaceId, userId, cancellationToken) is not null;

    private async Task<(AccountDetails? Details, Dictionary<string, string[]> Errors)> ValidateAsync(
        Guid workspaceId, AccountInput input, CancellationToken cancellationToken)
    {
        var details = AccountDetails.TryCreate(
            input.Name, input.Institution, input.Type, input.FinancialScope, input.ResponsibleUserId,
            input.OpeningBalance, input.OpeningBalanceDate, out var errors);

        // Responsável precisa ser vínculo desta família. A mensagem não diz se o Id existe em outra.
        if (input.ResponsibleUserId is { } memberId && !errors.ContainsKey(ResponsibleField)
            && !await accounts.IsWorkspaceOwnerAsync(workspaceId, memberId, cancellationToken))
        {
            errors[ResponsibleField] = [InvalidResponsible];
            details = null;
        }

        return (details, errors);
    }
}
