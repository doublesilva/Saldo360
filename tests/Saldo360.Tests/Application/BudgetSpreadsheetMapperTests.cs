using System.Text.Json;
using Saldo360.Application.Bootstrap;
using Saldo360.Domain.Budgets;
using Saldo360.Infrastructure.Bootstrap;
using Saldo360.Tests.Support;

namespace Saldo360.Tests.Application;

[Trait("Category", "INFRA")]
public sealed class BudgetSpreadsheetMapperTests
{
    private static SpreadsheetImportPlan Map(SpreadsheetSnapshot? s = null) => new BudgetSpreadsheetMapper().Map(Guid.Parse("11111111-1111-4111-8111-111111111111"), s ?? BudgetSpreadsheetFixture.Source());
    [Fact] public void Snapshot_Roundtrip_LogicalHashExcludesTransportAndDisplay()
    {
        var s = BudgetSpreadsheetFixture.Source();
        var parsed = SpreadsheetSnapshot.Parse(JsonSerializer.Serialize(s, SpreadsheetSnapshot.Json));
        Assert.Equal(s.LogicalHash, parsed.LogicalHash);
        Assert.Equal(s.LogicalHash, (s with { ReadAtUtc = s.ReadAtUtc.AddHours(1) }).LogicalHash);
        parsed.Sheet("Parâmetros").Rows[1][1] = parsed.Sheet("Parâmetros").Rows[1][1] with { Display = "100,00" };
        Assert.Equal(s.LogicalHash, parsed.LogicalHash);
    }
    [Fact] public void SourceChange_RequiresNewDryRun_AndFreshReadIsRequired()
    {
        var s = BudgetSpreadsheetFixture.Source(); var fresh = BudgetSpreadsheetFixture.Source() with { ReadAtUtc = s.ReadAtUtc.AddMinutes(1) };
        s.RequireUnchanged(fresh);
        fresh.Sheet("Parâmetros").Rows[1][1] = BudgetSpreadsheetFixture.Cell(101m);
        Assert.Throws<InvalidOperationException>(() => s.RequireUnchanged(fresh));
        Assert.Throws<InvalidOperationException>(() => s.RequireUnchanged(s));
        Assert.Throws<InvalidOperationException>(() => s.RequireUnchanged(s with { ReadAtUtc = s.ReadAtUtc.AddMinutes(1), ModifiedAtUtc = s.ModifiedAtUtc.AddMinutes(1) }));
    }
    [Fact] public void Parsing_RejectsForgedLiteralRawAndUnknownFields()
    {
        var s = BudgetSpreadsheetFixture.Source(); s.Sheet("Metas Mensais").Rows[1][4] = s.Sheet("Metas Mensais").Rows[1][4] with { RawNumber = "100" };
        Assert.Throws<InvalidOperationException>(() => SpreadsheetSnapshot.Parse(JsonSerializer.Serialize(s, SpreadsheetSnapshot.Json)));
        Assert.Throws<JsonException>(() => SpreadsheetSnapshot.Parse(JsonSerializer.Serialize(BudgetSpreadsheetFixture.Source(), SpreadsheetSnapshot.Json).Replace("\"schemaVersion\": 1", "\"unexpected\": 1, \"schemaVersion\": 1")));
    }
    [Fact] public void MappingAndReferences_AreDeterministic_DespiteRandomDomainIds()
    {
        var a = Map(); var b = Map(); Assert.Equal(a.PlanHash, b.PlanHash);
        Assert.Equal(a.Items.Select(i => i.ContentHash), b.Items.Select(i => i.ContentHash));
        Assert.Equal(a.Items.Count(i => i.IsEntity), a.Items.Where(i => i.IsEntity).Select(i => i.ExternalReference).Distinct().Count());
    }
    [Fact] public void DuplicateIds_AreBlocking_BeforeGraphConstruction()
    {
        var s = BudgetSpreadsheetFixture.Source(); var rows = s.Sheet("Lançamentos").Rows; rows[2][11] = rows[1][11];
        Assert.Contains(Map(s).Items, i => i.Reason == "DuplicateSourceId" && i.Status == BootstrapDisposition.Invalid);
    }
    [Fact] public void RequiredIssuer_UsesExplicitOwnerApproval_NotBrandInference()
    {
        var cards = Map().Items.Where(i => i.EntityType == SpreadsheetEntity.Card).ToArray(); Assert.Equal(4, cards.Length);
        Assert.Equal("Itau", cards.Single(i => i.Payload.Name == "Credicard").Payload.Institution);
        Assert.Equal("Bourbon", cards.Single(i => i.Payload.Name == "Zaffari/Mercado").Payload.Institution);
        Assert.All(cards, i => Assert.Equal(BootstrapDisposition.Created, i.Status));
        Assert.All(cards.Where(i => i.Payload.Name is "Credicard" or "Zaffari/Mercado"), i => Assert.Contains("OwnerApproval:T09.3", i.MetadataJson));
        Assert.DoesNotContain(cards, i => i.Payload.Name == "242");
    }
    [Fact] public void Recurrences_AreDistinctAccountOrCardDefinitions_NotBankTransactions()
    {
        var items = Map().Items; Assert.Equal(11, items.Count(i => i.EntityType == SpreadsheetEntity.AccountRecurrence));
        Assert.Equal(6, items.Count(i => i.EntityType == SpreadsheetEntity.CardRecurrence));
        Assert.All(items.Where(i => i.EntityType == SpreadsheetEntity.CardRecurrence), i => { Assert.Null(i.Payload.Account); Assert.NotNull(i.Payload.Card); });
        Assert.All(items.Where(i => i.EntityType == SpreadsheetEntity.AccountRecurrence), i => { Assert.NotNull(i.Payload.Account); Assert.Null(i.Payload.Card); });
        Assert.Equal(7, items.Count(i => i.EntityType == SpreadsheetEntity.PlannedTransaction));
        Assert.DoesNotContain(items, i => i.Reason.StartsWith("MissingChargeDate"));
        foreach (var id in new[] { "REC-003", "REC-ZAF-001" })
        {
            var recurrence = items.Single(i => i.ExternalReference == id + ":recurrence");
            Assert.Equal(10, recurrence.Payload.Day);
            Assert.Equal(recurrence.Payload.Competence!.Value.AddMonths(-1).AddDays(9), recurrence.Payload.Date);
            Assert.Contains("OwnerApproval:T09.3", recurrence.MetadataJson);
            Assert.Null(recurrence.Warnings);
        }
    }
    [Fact] public void ChargeDate_DoesNotEqualInvoiceCompetencePlaceholder()
    {
        var s = BudgetSpreadsheetFixture.Source(); var row = s.Sheet("Lançamentos").Rows.Single(r => r.Length > 11 && r[11].Text == "REC-002");
        row[0] = BudgetSpreadsheetFixture.Cell(BudgetSpreadsheetFixture.Serial(10, 27));
        Assert.Contains(Map(s).Items, i => i.ExternalReference == "REC-002:recurrence" && i.Reason == "InvoiceCompetenceMismatch");
    }
    [Fact] public void FullPurchase_UsesRealTotalDateAndAutomaticCompatibleSchedule()
    {
        var items = Map().Items; var amazon = Assert.Single(items, i => i.ExternalReference == "PAR-BL-022-01:purchase");
        Assert.Equal(293.35m, amazon.Payload.Amount); Assert.Equal(new DateOnly(2026, 10, 9), amazon.Payload.Date); Assert.Equal(3, amazon.Payload.Count);
        Assert.Equal(new[] { 97.79m, 97.78m, 97.78m }, items.Where(i => i.EntityType == SpreadsheetEntity.Installment && i.Payload.Purchase == amazon.ExternalReference).Select(i => i.Payload.Amount!.Value));
    }
    [Fact] public void Kalunga_PreservesExplicitCentAmounts_AsKnownObligations()
    {
        var items = Map().Items.Where(i => i.ExternalReference.StartsWith("PAR-NU-004-")).ToArray();
        Assert.Equal(3, items.Length); Assert.All(items, i => Assert.Equal(SpreadsheetEntity.KnownInstallment, i.EntityType));
        Assert.Equal(new[] { "PAR-NU-004-OUT:installment:1", "PAR-NU-004-FUT:installment:2", "PAR-NU-004-FUT:installment:3" }, items.Select(i => i.ExternalReference));
        Assert.Equal("Synthetic — parcela 1/3", items[0].Payload.Name);
        Assert.Equal("Synthetic", items[1].Payload.Name); // preserve FUT's own description, not OUT's 1/3 label
        Assert.Equal("Synthetic", items[2].Payload.Name);
        Assert.Equal(new[] { 111.78m, 111.76m, 111.76m }, items.Select(i => i.Payload.Amount!.Value));
        Assert.All(items, i => Assert.Null(i.Payload.Date));
    }
    [Fact] public void IntermediateSevenOfTen_KeepsNumbersAndDoesNotInventPurchase()
    {
        var items = Map().Items.Where(i => i.ExternalReference.StartsWith("PAR-BL-002:")).ToArray();
        Assert.Equal(new[] { 7, 8, 9, 10 }, items.Select(i => i.Payload.Number!.Value)); Assert.All(items, i => { Assert.Equal(10, i.Payload.Count); Assert.Null(i.Payload.Date); Assert.Equal(SpreadsheetEntity.KnownInstallment, i.EntityType); });
    }
    [Fact] public void Recovery_KeepsLiteralLexicalValue_AndNeverRecalculates()
    {
        var items = Map().Items; Assert.Equal(48, items.Count(i => i.EntityType == SpreadsheetEntity.MonthlyAggregateTarget));
        var recovery = items.Single(i => i.ExternalReference == "META-2026-10:RecoveryAmount");
        Assert.Equal(1270.38m, recovery.Payload.Amount); Assert.Contains("1270.3759999999995", recovery.MetadataJson);
        Assert.Equal(MonthlyAggregateTargetType.RecoveryAmount, recovery.Payload.TargetType);
    }
    [Fact] public void RemoteAndHealth_AreApprovedExclusions_NotCreated()
    {
        var items = Map().Items;
        Assert.Contains(items, i => i.ExternalReference == "LAN-BL-AMZ-701-9749415-1047439" && i.Status == BootstrapDisposition.Unmapped && i.ExcludedByApprovedDecision);
        Assert.Contains(items, i => i.ExternalReference == "PF-007" && i.Status == BootstrapDisposition.Unmapped && i.ExcludedByApprovedDecision);
    }
    [Fact] public void HistoryAndOpeningMovements_AreNotDuplicated_OpeningRemainsApproved()
    {
        var items = Map().Items; Assert.Equal(37, items.Count(i => i.Reason == "AlreadyRepresentedInLaunches"));
        Assert.Contains(items, i => i.Reason == "AbsorbedInOpeningBalance");
        var pf = items.Single(i => i.ExternalReference == "ACCOUNT:ItauPF"); Assert.Equal(-13826.92m, pf.Payload.Amount); Assert.Equal(new DateOnly(2026, 10, 8), pf.Payload.Date);
        Assert.DoesNotContain(items, i => i.EntityType == SpreadsheetEntity.Exclusion && i.IsEntity);
    }
    [Fact] public void HistoryWithMissingSourceIdentity_BlocksInsteadOfImportingASecondList()
    {
        var s = BudgetSpreadsheetFixture.Source(); s.Sheet("Histórico compromissos 08-10").Rows[1][0] = BudgetSpreadsheetFixture.Cell("missing");
        Assert.Contains(Map(s).Items, i => i.Reason == "HistoryIdentityNotUniqueInLaunches" && i.Status == BootstrapDisposition.Invalid);
    }
    [Fact] public void FutureMovementOrChangedOpening_BlocksInsteadOfSilentlyOmitting()
    {
        var s = BudgetSpreadsheetFixture.Source(); s.Sheet("Movimentos Conta").Rows[1][0] = BudgetSpreadsheetFixture.Cell(BudgetSpreadsheetFixture.Serial(10, 9));
        Assert.Contains(Map(s).Items, i => i.Reason == "OpeningSnapshotChanged");
    }
    [Theory] [InlineData(BootstrapDisposition.Invalid, false, false)] [InlineData(BootstrapDisposition.Conflict, false, false)]
    [InlineData(BootstrapDisposition.Unmapped, false, false)] [InlineData(BootstrapDisposition.Unmapped, true, true)]
    [InlineData(BootstrapDisposition.AlreadyImported, false, true)] [InlineData(BootstrapDisposition.Created, false, true)]
    public void CanExecute_AccountsForEveryDisposition(BootstrapDisposition status, bool approved, bool canExecute)
    {
        var item = new SpreadsheetPlanItem("test", SpreadsheetEntity.Exclusion, new("Test"), "Synthetic", Status: status, ExcludedByApprovedDecision: approved);
        var result = new SpreadsheetDryRunResult(Guid.NewGuid(), Guid.NewGuid(), "source", "plan", BudgetSpreadsheetFixture.ReadAt, BudgetSpreadsheetFixture.ReadAt, true, [item], new Dictionary<string, long>());
        Assert.Equal(canExecute, result.CanExecute); Assert.True(result.ZeroWrite);
        Assert.False((result with { DatabaseReadOnly = false }).CanExecute);
    }
    [Fact] public void DomainGraph_DoesNotTurnInvalidCardIntoValidDependentRows()
    {
        var source = BudgetSpreadsheetFixture.Source();
        var row = source.Sheet("Lançamentos").Rows.Single(r => r.Length > 11 && r[7].Text == "Credicard");
        row[10] = BudgetSpreadsheetFixture.Cell(row[10].Text + "; Emissor confirmado: Divergent issuer;");
        var plan = Map(source); var graph = SpreadsheetDomainGraph.Build(plan, Guid.NewGuid(), BudgetSpreadsheetFixture.ReadAt);
        Assert.Contains(graph.Items, i => i.EntityType == SpreadsheetEntity.KnownInstallment && i.Payload.Card == "CARD:Credicard" && i.Status == BootstrapDisposition.Invalid);
        Assert.DoesNotContain(graph.Entities.Values, e => e is Saldo360.Domain.Transactions.FinancialTransaction);
    }
    [Fact] public void ProvisionalFields_AreWarningsWithDurableMetadata_NotInventedSourceEvidence()
    {
        var source = BudgetSpreadsheetFixture.Source();
        foreach (var row in source.Sheet("Lançamentos").Rows.Skip(1))
            if (row[7].Text == "Black") row[10] = BudgetSpreadsheetFixture.Cell(row[10].Text.Replace("PDF Itaú", "").Replace("27/09", ""));
        var plan = new BudgetSpreadsheetMapper(12).Map(Guid.NewGuid(), source);
        var card = plan.Items.Single(i => i.ExternalReference == "CARD:Black");
        Assert.Equal("Não informado", card.Payload.Institution);
        Assert.Contains("\"issuerProvisional\":true", card.MetadataJson);
        Assert.Single(card.Warnings!); Assert.Equal(BootstrapDisposition.Created, card.Status);
        var recurrence = plan.Items.Single(i => i.ExternalReference == "REC-002:recurrence");
        Assert.Equal(12, recurrence.Payload.Day); Assert.Single(recurrence.Warnings!);
        Assert.Equal(BootstrapDisposition.Created, recurrence.Status);
        Assert.Contains("\"chargeDayProvisional\":true", recurrence.MetadataJson);
        Assert.Equal(10, plan.Items.Single(i => i.ExternalReference == "REC-003:recurrence").Payload.Day);
    }
    [Fact] public void SourceCategoryCase_IsCanonicalized_WithoutDuplicateCategoryEntities()
    {
        var s = BudgetSpreadsheetFixture.Source(); s.Sheet("Lançamentos").Rows.Single(r => r.Length > 11 && r[11].Text == "LAN-006")[4] = BudgetSpreadsheetFixture.Cell("assinaturas");
        var plan = Map(s); Assert.Equal(10, plan.Items.Count(i => i.EntityType == SpreadsheetEntity.Category));
        Assert.Equal("CATEGORY:Assinaturas", plan.Items.Single(i => i.ExternalReference == "LAN-006:purchase").Payload.Category);
    }
    [Fact] public void MissingNumber_IsInvalid_NotANewPurchaseStartingAtOne()
    {
        var s = BudgetSpreadsheetFixture.Source(); s.Sheet("Lançamentos").Rows.Single(r => r.Length > 11 && r[11].Text == "PAR-BL-002")[10] = BudgetSpreadsheetFixture.Cell("Original numbering unavailable");
        Assert.Contains(Map(s).Items, i => i.ExternalReference == "PAR-BL-002" && i.Reason == "MissingExplicitInstallmentNumber");
    }
    [Fact] public void FormulaRecoveryAndUnknownParameters_AreNotApprovedByDefault()
    {
        var s = BudgetSpreadsheetFixture.Source(); s.Sheet("Metas Mensais").Rows[1][4] = s.Sheet("Metas Mensais").Rows[1][4] with { Formula = "=100" };
        s.Sheet("Parâmetros").Rows[1][0] = BudgetSpreadsheetFixture.Cell("Unknown metric"); var plan = Map(s);
        Assert.Contains(plan.Items, i => i.Reason == "TargetMustBeLiteral");
        Assert.Contains(plan.Items, i => i.Reason == "NeedsReview:UnknownParameter" && !i.ExcludedByApprovedDecision);
    }
    [Fact] public void ConfirmedAmount_IsNotEstimatedJustBecauseItReplacesAnOldEstimate()
    {
        var s = BudgetSpreadsheetFixture.Source();
        s.Sheet("Despesas Fixas").Rows.Single(r => r[0].Text == "PF-002-202610")[11] = BudgetSpreadsheetFixture.Cell("Real conhecido; substitui R$ 200 estimados");
        s.Sheet("Lançamentos").Rows.Single(r => r.Length > 11 && r[11].Text == "REC-001")[10] = BudgetSpreadsheetFixture.Cell("Recorrência confirmada; 05/10; substitui valor estimado");
        var plan = Map(s);
        Assert.False(plan.Items.Single(i => i.ExternalReference == "PF-002-202610:planned").Payload.IsEstimated);
        Assert.False(plan.Items.Single(i => i.ExternalReference == "REC-001:recurrence").Payload.IsEstimated);
        Assert.True(plan.Items.Single(i => i.ExternalReference == "PF-TAX-202702:planned").Payload.IsEstimated);
    }
}
