using Saldo360.Application.Families;
using Saldo360.Domain.Accounts;
using Saldo360.Domain.Common;

namespace Saldo360.Application.Accounts;

/// <summary>
/// Contas financeiras da família. Owner e Member consultam, cadastram e editam; todas as contas são
/// visíveis a todos os integrantes. Família vem da rota, criador vem da sessão.
/// </summary>
public sealed class FinancialAccountService(
    IFamilyRepository families,
    IFinancialAccountRepository accounts,
    TimeProvider timeProvider)
{
    private const string ResponsibleField = "responsibleMemberId";
    private const string InvalidResponsible = "Responsável inválido.";

    public async Task<AccountResult> CreateAsync(Guid familyId, Guid userId, AccountInput input, CancellationToken cancellationToken)
    {
        if (!await IsParticipantAsync(familyId, userId, cancellationToken))
        {
            return AccountResult.NotFound;
        }

        var (details, errors) = await ValidateAsync(familyId, input, cancellationToken);
        if (details is null)
        {
            return AccountResult.Invalid(errors);
        }

        var account = FinancialAccount.Create(familyId, details, userId, timeProvider.GetUtcNow());
        await accounts.AddAsync(account, cancellationToken);
        return AccountResult.Ok((await accounts.GetAsync(familyId, account.Id, cancellationToken))!);
    }

    public async Task<IReadOnlyList<FinancialAccountDto>?> ListAsync(
        Guid familyId, Guid userId, bool includeInactive, CancellationToken cancellationToken) =>
        await IsParticipantAsync(familyId, userId, cancellationToken)
            ? await accounts.ListAsync(familyId, includeInactive, cancellationToken)
            : null;

    public async Task<FinancialAccountDto?> GetAsync(Guid familyId, Guid accountId, Guid userId, CancellationToken cancellationToken) =>
        await IsParticipantAsync(familyId, userId, cancellationToken)
            ? await accounts.GetAsync(familyId, accountId, cancellationToken)
            : null;

    /// <summary>Atualiza campos cadastrais e situação. Família, criador, moeda e data de criação não mudam.</summary>
    public async Task<AccountResult> UpdateAsync(
        Guid familyId, Guid accountId, Guid userId, AccountInput input, bool? isActive, CancellationToken cancellationToken)
    {
        if (!await IsParticipantAsync(familyId, userId, cancellationToken)
            || await accounts.FindAsync(familyId, accountId, cancellationToken) is not { } account)
        {
            return AccountResult.NotFound;
        }

        var (details, errors) = await ValidateAsync(familyId, input, cancellationToken);
        if (isActive is null)
        {
            errors["isActive"] = ["Informe se a conta está ativa."];
        }

        if (details is null || isActive is null)
        {
            return AccountResult.Invalid(errors);
        }

        account.Update(details, isActive.Value);
        await accounts.SaveChangesAsync(cancellationToken);
        return AccountResult.Ok((await accounts.GetAsync(familyId, account.Id, cancellationToken))!);
    }

    /// <summary>
    /// Saldo realizado no fechamento de <paramref name="asOf"/> (padrão: hoje em America/Sao_Paulo). Data futura é
    /// recusada. Sem saldo inicial, ou antes da data de referência, devolve indisponível (nunca zero). Conta
    /// inativa também é consultável. As somas vêm agregadas do banco; aqui só se combina em decimal.
    /// </summary>
    public async Task<BalanceResult> GetBalanceAsync(
        Guid familyId, Guid accountId, Guid userId, DateOnly? asOf, CancellationToken cancellationToken)
    {
        if (!await IsParticipantAsync(familyId, userId, cancellationToken)
            || await accounts.GetOpeningBalanceAsync(familyId, accountId, cancellationToken) is not { } opening)
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

        if (opening is not { Amount: { } amount, Date: { } referenceDate })
        {
            return Unavailable(BalanceUnavailableReason.OpeningBalanceNotConfigured);
        }

        if (date < referenceDate)
        {
            return Unavailable(BalanceUnavailableReason.BeforeOpeningBalanceDate);
        }

        var totals = await accounts.SumTransactionsAsync(familyId, accountId, referenceDate, date, cancellationToken);
        return new BalanceResult(BalanceAccess.Allowed, new AccountBalanceDto(
            accountId, date, amount, referenceDate, totals.Income, totals.Expense,
            amount + totals.Income - totals.Expense, null), null);

        BalanceResult Unavailable(BalanceUnavailableReason reason) => new(BalanceAccess.Allowed, new AccountBalanceDto(
            accountId, date, opening.Amount, opening.Date, null, null, null, reason), null);
    }

    private async Task<bool> IsParticipantAsync(Guid familyId, Guid userId, CancellationToken cancellationToken) =>
        await families.GetMemberRoleAsync(familyId, userId, cancellationToken) is not null;

    private async Task<(AccountDetails? Details, Dictionary<string, string[]> Errors)> ValidateAsync(
        Guid familyId, AccountInput input, CancellationToken cancellationToken)
    {
        var details = AccountDetails.TryCreate(
            input.Name, input.Institution, input.Type, input.FinancialScope, input.ResponsibleMemberId,
            input.OpeningBalance, input.OpeningBalanceDate, out var errors);

        // Responsável precisa ser vínculo desta família. A mensagem não diz se o Id existe em outra.
        if (input.ResponsibleMemberId is { } memberId && !errors.ContainsKey(ResponsibleField)
            && !await accounts.IsMemberOfFamilyAsync(familyId, memberId, cancellationToken))
        {
            errors[ResponsibleField] = [InvalidResponsible];
            details = null;
        }

        return (details, errors);
    }
}
