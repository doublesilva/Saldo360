using Microsoft.EntityFrameworkCore;
using Saldo360.Application.Bootstrap;
using Saldo360.Domain.Accounts;
using Saldo360.Domain.CreditCards;
using Saldo360.Domain.Pluggy;
using Saldo360.Domain.Users;
using Saldo360.Domain.Workspaces;
using Saldo360.Infrastructure.Bootstrap;
using Saldo360.Infrastructure.Persistence;
using Saldo360.Tests.Support;

namespace Saldo360.Tests.Persistence;

/// <summary>
/// FIX-01.1: the PF/PJ accounts of the fixed-expenses import are associated with existing (e.g. Pluggy-linked) accounts, never
/// concluded by name alone, never modified, and never duplicated. Local/Testcontainers only; all data synthetic.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "INFRA")]
[Trait("Area", "Admin")]
[Trait("Dependency", "PostgreSQL")]
public sealed class BudgetSpreadsheetAccountAssociationTests(PostgresFixture postgres)
{
    private const string Pf = "ACCOUNT:ItauPF", Pj = "ACCOUNT:ItauEmpresas";
    private static readonly DateTimeOffset Now = BudgetSpreadsheetFixture.ReadAt;

    private async Task<(string Connection, Guid Workspace, Guid Owner)> Setup(string? connection = null)
    {
        connection ??= await postgres.CreateDatabaseAsync();
        await using var db = PostgresFixture.CreateDbContext(connection);
        var owner = ApplicationUser.Create("https://synthetic.invalid", Guid.NewGuid().ToString(), "Synthetic", Now);
        var workspace = FinancialWorkspace.CreatePersonal(owner.Id, Now);
        db.Users.Add(owner); db.FinancialWorkspaces.Add(workspace); await db.SaveChangesAsync();
        return (connection, workspace.Id, owner.Id);
    }

    private static async Task<Guid> AddAccount(string connection, Guid workspace, Guid owner, string name, string? institution, string scope,
        string type = "Checking", bool linked = false, string? linkInstitution = null, bool opening = true, bool active = true)
    {
        await using var db = PostgresFixture.CreateDbContext(connection);
        var details = AccountDetails.TryCreate(name, institution, type, scope, null, opening ? -15284.39m : null, opening ? new DateOnly(2026, 10, 1) : null, out _)!;
        var account = FinancialAccount.Create(workspace, details, owner, Now);
        if (!active) account.Update(details, false);
        db.FinancialAccounts.Add(account);
        if (linked)
        {
            var item = PluggyItemLink.Create(workspace, owner, Guid.NewGuid().ToString("D"), "Synthetic", Now);
            db.Add(item);
            db.Add(PluggyAccountLink.Create(workspace, item.Id, "acc-" + Guid.NewGuid().ToString("N")[..8], PluggyLinkedAccountKind.Bank, account.Id, null,
                "Synthetic ••••0000", linkInstitution ?? institution, "BANK", "CHECKING_ACCOUNT", null, owner, Now));
        }
        await db.SaveChangesAsync();
        return account.Id;
    }

    private static async Task<SpreadsheetDryRunResult> Dry(string c, Guid w, Dictionary<string, Guid>? map = null)
    {
        await using var db = PostgresFixture.CreateDbContext(c);
        return await new BudgetSpreadsheetImporter(db, TimeProvider.System, BudgetSpreadsheetImportScope.FixedExpensesOnly, map)
            .DryRunAsync(w, BudgetSpreadsheetFixture.ExecutableSyntheticSource());
    }

    private static async Task<int> Execute(string c, Guid w, SpreadsheetDryRunResult report, Dictionary<string, Guid>? map = null)
    {
        var source = BudgetSpreadsheetFixture.ExecutableSyntheticSource();
        await using var db = PostgresFixture.CreateDbContext(c);
        return await new BudgetSpreadsheetImporter(db, TimeProvider.System, BudgetSpreadsheetImportScope.FixedExpensesOnly, map)
            .ExecuteApprovedAsync(w, source, source with { ReadAtUtc = DateTimeOffset.UtcNow }, report, report.ReportHash);
    }

    private static SpreadsheetPlanItem Item(SpreadsheetDryRunResult r, string reference) => r.Items.Single(i => i.ExternalReference == reference);

    [Fact] public async Task ProvenPluggyLinkedAccounts_WithDifferentOpeningBalanceAndInstitution_AreAdoptedWithoutConflict_AndNothingIsDuplicatedOrChanged()
    {
        var (c, w, owner) = await Setup();
        var pf = await AddAccount(c, w, owner, "Conta Itaú principal", "Itaú Unibanco S.A.", "PF", linked: true);
        var pj = await AddAccount(c, w, owner, "Itaú PJ", "Itaú", "PJ", linked: true, opening: false);
        var report = await Dry(c, w);

        Assert.True(report.CanExecute, string.Join(";", report.Items.Where(i => i.Status is BootstrapDisposition.Invalid or BootstrapDisposition.Conflict).Select(i => i.ExternalReference + ":" + i.Reason)));
        Assert.Equal(pf, Item(report, Pf).AccountResolution!.SelectedAccountId); Assert.Equal(pj, Item(report, Pj).AccountResolution!.SelectedAccountId);
        Assert.All(new[] { Pf, Pj }, r => Assert.Equal(BootstrapDisposition.AlreadyImported, Item(report, r).Status));
        var pfCandidate = Item(report, Pf).AccountResolution!.Candidates.Single();
        Assert.Contains("OpeningBalance", pfCandidate.DivergentFields); Assert.Contains("Name", pfCandidate.DivergentFields); Assert.True(pfCandidate.HasPluggyBankLink);
        Assert.DoesNotContain(report.Items, i => i.EntityType == SpreadsheetEntity.Account && i.Status == BootstrapDisposition.Created);
        // The seven competence exceptions, the active recurrences, the excluded inactive expense and no card data.
        Assert.Equal(7, report.Items.Count(i => i.EntityType == SpreadsheetEntity.PlannedTransaction && i.Status == BootstrapDisposition.Created));
        Assert.Equal(11, report.Items.Count(i => i.EntityType == SpreadsheetEntity.AccountRecurrence && i.Status == BootstrapDisposition.Created));
        Assert.Contains(report.Items, i => i.Status == BootstrapDisposition.Unmapped && i.ExcludedByApprovedDecision && i.Payload.Name.Contains("saúde", StringComparison.OrdinalIgnoreCase));
        foreach (var kind in new[] { SpreadsheetEntity.Card, SpreadsheetEntity.Purchase, SpreadsheetEntity.Installment, SpreadsheetEntity.Invoice, SpreadsheetEntity.CardRecurrence, SpreadsheetEntity.KnownInstallment })
            Assert.Equal(0, report.CountsByAggregate[kind.ToString()]);

        Assert.True(await Execute(c, w, report) > 0);
        await using (var verify = PostgresFixture.CreateDbContext(c))
        {
            Assert.Equal(2, await verify.FinancialAccounts.CountAsync()); // no 'Itaú Empresas'/'Itaú PF' created
            var account = await verify.FinancialAccounts.SingleAsync(a => a.Id == pf);
            Assert.Equal((-15284.39m, new DateOnly(2026, 10, 1), "Conta Itaú principal"), (account.OpeningBalance, account.OpeningBalanceDate, account.Name));
            Assert.Equal(2, await verify.PluggyAccountLinks.CountAsync());
            var ledger = await verify.FinancialBootstrapProvenance.Where(p => p.EntityType == "Account").ToListAsync();
            Assert.Equal(new[] { pf, pj }.Order(), ledger.Select(p => p.EntityId).Order());
        }
        // Idempotence: preserved association, nothing new planned, re-execution writes nothing.
        var again = await Dry(c, w);
        Assert.True(again.CanExecute); Assert.Equal(0, again.PlannedNewProvenanceRecords);
        Assert.Equal("Ledger", Item(again, Pf).AccountResolution!.Method);
        Assert.Equal(0, await Execute(c, w, again));
        await using var final = PostgresFixture.CreateDbContext(c);
        Assert.Equal(2, await final.FinancialAccounts.CountAsync());
        Assert.Equal(await final.FinancialBootstrapProvenance.CountAsync(), again.ProvenanceRecords);
    }

    [Fact] public async Task MultipleCandidates_Block_ListingOptions_AndDependentItemsStayBlocked()
    {
        var (c, w, owner) = await Setup();
        var a = await AddAccount(c, w, owner, "Itaú PF", "Itaú", "PF", linked: true);
        var b = await AddAccount(c, w, owner, "Itaú PF antiga", "Itaú", "PF");
        var report = await Dry(c, w);
        var item = Item(report, Pf);
        Assert.False(report.CanExecute); Assert.Equal(BootstrapDisposition.Conflict, item.Status); Assert.Equal("AccountAssociationRequired:AmbiguousCandidates", item.Reason);
        Assert.Equal(new[] { a, b }.Order(), item.AccountResolution!.Candidates.Select(x => x.Id).Order());
        Assert.Contains("--account-map ACCOUNT:ItauPF=", item.AccountResolution.Instruction);
        Assert.Contains(report.Items, i => i.EntityType == SpreadsheetEntity.AccountRecurrence && i.Payload.Account == Pf && i.Reason == "InvalidAccountDependency");
    }

    [Fact] public async Task NameAndInstitutionAlone_WithoutProofBeyondTheName_DoNotConcludeTheMatch()
    {
        var (c, w, owner) = await Setup();
        await AddAccount(c, w, owner, "Itaú PF", "Itaú", "PF");
        var report = await Dry(c, w);
        Assert.False(report.CanExecute); Assert.Equal("AccountAssociationRequired:InsufficientProof", Item(report, Pf).Reason);
        Assert.Single(Item(report, Pf).AccountResolution!.Candidates);
    }

    [Fact] public async Task ExplicitMapping_ResolvesAmbiguity_IsPersistedInLedger_AndPreservedWithoutTheFlag()
    {
        var (c, w, owner) = await Setup();
        var chosen = await AddAccount(c, w, owner, "Itaú PF", "Itaú", "PF");
        await AddAccount(c, w, owner, "Itaú PF antiga", "Itaú", "PF");
        var pj = await AddAccount(c, w, owner, "Itaú PJ", "Itaú", "PJ", linked: true);
        var map = new Dictionary<string, Guid> { [Pf] = chosen };
        var report = await Dry(c, w, map);
        Assert.True(report.CanExecute); Assert.Equal("ExplicitMapping", Item(report, Pf).AccountResolution!.Method);
        Assert.Equal(pj, Item(report, Pj).AccountResolution!.SelectedAccountId);
        await Execute(c, w, report, map);
        var again = await Dry(c, w); // no flag: preserved association
        Assert.True(again.CanExecute); Assert.Equal(chosen, Item(again, Pf).AccountResolution!.SelectedAccountId); Assert.Equal("Ledger", Item(again, Pf).AccountResolution!.Method);
        Assert.Equal(0, again.PlannedNewProvenanceRecords);
        // A different explicit choice later is a conflict with the preserved association, never a silent switch.
        await using var db = PostgresFixture.CreateDbContext(c);
        var other = (await db.FinancialAccounts.Where(a => a.Id != chosen && a.Scope == FinancialScope.PF).SingleAsync()).Id;
        var differing = await Dry(c, w, new() { [Pf] = other });
        Assert.False(differing.CanExecute); Assert.Equal("AccountMappingDiffersFromPreservedAssociation", Item(differing, Pf).Reason);
    }

    [Fact] public async Task InvalidExplicitMappings_AreRejected_WithSpecificReasons_AndNothingIsWritten()
    {
        var (c, w, owner) = await Setup();
        var pjAccount = await AddAccount(c, w, owner, "Itaú PJ", "Itaú", "PJ", linked: true);
        await AddAccount(c, w, owner, "Itaú PF", "Itaú", "PF", linked: true);
        var inactive = await AddAccount(c, w, owner, "Itaú inativa", "Itaú", "PF", active: false);
        var cash = await AddAccount(c, w, owner, "Carteira", null, "PF", type: "Cash", opening: false);
        Guid card;
        await using (var db = PostgresFixture.CreateDbContext(c))
        {
            var created = FinancialCreditCard.Create(w, CreditCardDetails.TryCreate("Cartão sintético", "Itaú", null, 1000m, 5, 10, null, out _)!, owner, Now);
            db.FinancialCreditCards.Add(created); await db.SaveChangesAsync(); card = created.Id;
        }
        var (c2, w2, owner2) = await Setup(c); // another workspace in the same database
        var foreign = await AddAccount(c2, w2, owner2, "Itaú PF", "Itaú", "PF", linked: true);

        async Task Assert_(Guid id, string reason)
        {
            var report = await Dry(c, w, new() { [Pf] = id });
            Assert.False(report.CanExecute); Assert.Equal(reason, Item(report, Pf).Reason); Assert.Equal("InvalidExplicitMapping", Item(report, Pf).AccountResolution!.Method);
        }
        await Assert_(pjAccount, "AccountMappingScopeMismatch");
        await Assert_(inactive, "AccountMappingTargetInactive");
        await Assert_(cash, "AccountMappingTargetNotBankAccount");
        await Assert_(card, "AccountMappingTargetIsCard");
        await Assert_(foreign, "AccountMappingTargetNotFoundInWorkspace");
        await Assert_(Guid.NewGuid(), "AccountMappingTargetNotFoundInWorkspace");
        await using var verify = PostgresFixture.CreateDbContext(c);
        Assert.Empty(verify.FinancialBootstrapProvenance);
    }

    [Fact] public async Task PjWithoutEquivalentAccount_IsCreatedOnce_ButAnEquivalentUnprovenPjAccountBlocksInsteadOfDuplicating()
    {
        var (c, w, owner) = await Setup();
        await AddAccount(c, w, owner, "Itaú PF", "Itaú", "PF", linked: true);
        var created = await Dry(c, w);
        Assert.True(created.CanExecute); Assert.Equal(BootstrapDisposition.Created, Item(created, Pj).Status); Assert.Equal("NoCompatibleAccount", Item(created, Pj).AccountResolution!.Method);

        var (c2, w2, owner2) = await Setup();
        await AddAccount(c2, w2, owner2, "Itaú PF", "Itaú", "PF", linked: true);
        await AddAccount(c2, w2, owner2, "Itaú PJ", null, "PJ", opening: false); // only the name suggests it: not proof
        var blocked = await Dry(c2, w2);
        Assert.False(blocked.CanExecute); Assert.Equal("AccountAssociationRequired:InsufficientProof", Item(blocked, Pj).Reason);
        Assert.DoesNotContain(blocked.Items, i => i.EntityType == SpreadsheetEntity.Account && i.Status == BootstrapDisposition.Created);
    }

    [Fact] public async Task ReviewedAssociation_CannotBeSwappedAtExecution()
    {
        var (c, w, owner) = await Setup();
        var a = await AddAccount(c, w, owner, "Itaú PF", "Itaú", "PF");
        var b = await AddAccount(c, w, owner, "Itaú PF 2", "Itaú", "PF");
        await AddAccount(c, w, owner, "Itaú PJ", "Itaú", "PJ", linked: true);
        var report = await Dry(c, w, new() { [Pf] = a });
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Execute(c, w, report, new() { [Pf] = b }));
        Assert.Equal("AccountAssociationChangedNewDryRunRequired", error.Message);
        await using var verify = PostgresFixture.CreateDbContext(c);
        Assert.Empty(verify.FinancialBootstrapProvenance); Assert.Empty(verify.FinancialRecurrences);
    }

    [Fact] public async Task AccountMap_IsRejectedOutsideFixedExpensesScope_AndForUnknownReferences()
    {
        var (c, w, _) = await Setup();
        await using (var db = PostgresFixture.CreateDbContext(c))
            await Assert.ThrowsAsync<InvalidOperationException>(() => new BudgetSpreadsheetImporter(db, TimeProvider.System, BudgetSpreadsheetImportScope.FullLegacyBootstrap,
                new Dictionary<string, Guid> { [Pf] = Guid.NewGuid() }).DryRunAsync(w, BudgetSpreadsheetFixture.ExecutableSyntheticSource()));
        await using (var db = PostgresFixture.CreateDbContext(c))
            await Assert.ThrowsAsync<InvalidOperationException>(() => new BudgetSpreadsheetImporter(db, TimeProvider.System, BudgetSpreadsheetImportScope.FixedExpensesOnly,
                new Dictionary<string, Guid> { ["ACCOUNT:Outra"] = Guid.NewGuid() }).DryRunAsync(w, BudgetSpreadsheetFixture.ExecutableSyntheticSource()));
    }
}
