using Saldo360.Application.Accounts;
using Saldo360.Application.Dashboard.Overview;
using Saldo360.Domain.Accounts;

namespace Saldo360.Tests.Application;

/// <summary>Origem bancária de um conjunto de contas: manual, integrada sem sincronização, integrada sincronizada e recorte misto. O horário é sempre o da sincronização mais antiga.</summary>
[Trait("Category", "CORE")]
[Trait("Area", "Dashboard")]
public sealed class BankSourceTests
{
    private static readonly DateTimeOffset Created = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static FinancialAccountDto Account(DateTimeOffset? syncedAt = null, bool integrated = false) => new(
        Guid.NewGuid(), "Conta", null, AccountType.Checking, FinancialScope.PF, null, null, null, null, true, "BRL", Created,
        Integration: integrated ? new AccountIntegrationDto(Guid.NewGuid(), Guid.NewGuid(), "Banco", null, "Active", syncedAt, null, null, null) : null);

    [Fact]
    public void OnlyManualAccountsHaveNoIntegrationAndNoTime() =>
        Assert.Equal(new BankSourceDto(2, 0, 0, null), BankSourceDto.From([Account(), Account()]));

    [Fact]
    public void AnIntegrationWithoutASuccessfulSyncCountsAsIntegratedButHasNoTime() =>
        Assert.Equal(new BankSourceDto(1, 1, 0, null), BankSourceDto.From([Account(integrated: true)]));

    [Fact]
    public void AMixedSetReportsTheOldestSyncOfTheIntegratedAccountsOnly()
    {
        var older = new DateTimeOffset(2026, 10, 14, 17, 5, 0, TimeSpan.Zero); var newer = older.AddHours(5);
        var source = BankSourceDto.From([Account(), Account(newer, true), Account(older, true), Account(integrated: true)]);
        Assert.Equal(new BankSourceDto(4, 3, 2, older), source); // a manual nunca entra; a integrada sem sincronização não tem horário
    }
}
