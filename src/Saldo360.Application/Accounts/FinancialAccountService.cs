using Saldo360.Application.Families;
using Saldo360.Domain.Accounts;

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
