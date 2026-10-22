using Saldo360.Application.Bootstrap;
using Saldo360.Domain.Budgets;
using Saldo360.Domain.Categories;
using Saldo360.Domain.CreditCards;
using Saldo360.Domain.Recurrences;

namespace Saldo360.Tests.Application;

public sealed class BudgetBootstrapDomainTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void Recurrence_DomainAlsoEnforcesExclusiveDestination(bool account, bool card)
    {
        var details = new RecurrenceDetails(account ? Guid.NewGuid() : null, Guid.NewGuid(), CategoryType.Expense, "Synthetic", 10m, false, RecurrenceFrequency.Monthly, 1, new(2026, 10, 1), null, null, 10, card ? Guid.NewGuid() : null);
        Assert.Throws<ArgumentException>(() => FinancialRecurrence.Create(Guid.NewGuid(), details, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void KnownInstallment_DoesNotInferMissingNumbersDatesOrTotals()
    {
        var row = FinancialCreditCardImportedInstallment.CreateKnown(Guid.NewGuid(), Guid.NewGuid(), null, "Known amount only", 123m, null, null, new(2026, 11, 1), null, "BudgetSpreadsheet", "row/1", Guid.NewGuid(), DateTimeOffset.UtcNow);
        Assert.Null(row.TransactionDate); Assert.Null(row.InstallmentNumber); Assert.Null(row.InstallmentCount); Assert.Null(row.InvoiceId); Assert.Null(row.DueDate);
    }

    [Theory]
    [InlineData(7, 6)]
    [InlineData(0, 6)]
    [InlineData(1, 0)]
    public void KnownInstallment_RejectsInvalidExplicitNumbering(int number, int count) => Assert.Throws<ArgumentException>(() =>
        FinancialCreditCardImportedInstallment.CreateKnown(Guid.NewGuid(), Guid.NewGuid(), null, "Invalid", 1m, number, count, new(2026, 11, 1), null, "BudgetSpreadsheet", "row", Guid.NewGuid(), DateTimeOffset.UtcNow));

    [Theory]
    [InlineData(-1)]
    [InlineData(1.001)]
    public void Target_RejectsNegativeOrExcessScale(decimal amount) => Assert.Throws<ArgumentException>(() =>
        FinancialMonthlyAggregateTarget.Create(Guid.NewGuid(), new(2026, 11, 1), MonthlyAggregateTargetType.TotalExpenses, amount, DateTimeOffset.UtcNow));

    [Fact]
    public void ZeroRecoveryIsAnIndependentValidSnapshot()
    {
        var target = FinancialMonthlyAggregateTarget.Create(Guid.NewGuid(), new(2026, 11, 1), MonthlyAggregateTargetType.RecoveryAmount, 0m, DateTimeOffset.UtcNow);
        Assert.Equal(0m, target.Amount);
    }

    [Theory]
    [InlineData("1270.3759999999995", "1270.38")]
    [InlineData("1501.2445000000016", "1501.24")]
    [InlineData("1.005", "1.01")]
    public void Recovery_RoundsOnlySnapshot_PreservesRawValue(string raw, string expected)
    {
        var result = ApprovedRecoverySnapshot.FromRaw(raw);
        Assert.Equal(expected, Saldo360.Domain.Common.Money.Format(result.Amount));
        Assert.Contains(raw, result.MetadataJson);
    }

    [Fact]
    public void DryRun_EnumeratesAllFiveDispositions_WithoutTreatingUnmappedAsImported()
    {
        var report = new BootstrapDryRunReport(Guid.NewGuid(), Enum.GetValues<BootstrapDisposition>().Select((x, i) => new BootstrapDryRunItem("Synthetic", $"row/{i}", x)).ToArray());
        Assert.True(report.ZeroWrite); Assert.False(report.CanExecute); Assert.All(report.Counts.Values, x => Assert.Equal(1, x));
    }

    [Fact]
    public void Fingerprint_PreservesIdentityAndMetadata_ButNotIrrelevantDecimalScale()
    {
        var first = new AggregateTargetInput(new(2026, 11, 1), MonthlyAggregateTargetType.RecoveryAmount, 12m, new("BudgetSpreadsheet", "Synthetic", "row", "{\"raw\":12}"));
        var fingerprint = BootstrapContentFingerprint.Compute("Target", first);
        Assert.Equal(fingerprint, BootstrapContentFingerprint.Compute("Target", first with { Amount = 12.00m }));
        Assert.NotEqual(fingerprint, BootstrapContentFingerprint.Compute("Target", first with { Source = first.Source with { MetadataJson = "{\"raw\":12.001}" } }));
    }

    [Fact]
    public void ApprovedExcludedRemote_RemainsUnmapped_ButIsNeverACreateCandidate()
    {
        var report = new BootstrapDryRunReport(Guid.NewGuid(), [new("Purchase", "excluded", BootstrapDisposition.Unmapped, "NeedsReview", ExcludedByApprovedDecision: true)]);
        Assert.True(report.CanExecute); Assert.Equal(0, report.Counts[BootstrapDisposition.Created]); Assert.Equal(1, report.Counts[BootstrapDisposition.Unmapped]);
    }
}
