using Saldo360.Application.Calendar;
using Saldo360.Application.Recurrences;
using Saldo360.Domain.Categories;

namespace Saldo360.Tests.Application;

/// <summary>PROJ-05: a associação automática só ocorre com par único e evidência; valor igual ou descrição genérica isolados nunca associam.</summary>
[Trait("Category", "CORE")]
[Trait("Area", "Pluggy")]
public sealed class PendingBankMatcherTests
{
    private static readonly Guid Account = Guid.NewGuid();
    private static DateOnly Nov(int day) => new(2026, 11, day);

    private static FixedObligation Obligation(Guid recurrence, string description, decimal amount, DateOnly due, bool estimated = false) =>
        new(Guid.NewGuid(), recurrence, 2, new(due.Year, due.Month, 1), null, Account, Guid.NewGuid(), CategoryType.Expense, default, description, amount, estimated, due, ObligationSituation.Planned, true);

    private static PendingBankDebit Debit(string description, decimal amount, DateOnly date) => new(Guid.NewGuid(), Account, amount, date, description);

    private static IReadOnlyDictionary<Guid, IReadOnlyList<string>> History(Guid recurrence, params string[] descriptions) =>
        new Dictionary<Guid, IReadOnlyList<string>> { [recurrence] = descriptions };

    private static readonly IReadOnlyDictionary<Guid, IReadOnlyList<string>> NoHistory = new Dictionary<Guid, IReadOnlyList<string>>();

    [Fact]
    public void ASafeMatcherPairIsAssociatedWithoutHistory()
    {
        var luz = Obligation(Guid.NewGuid(), "Conta de luz", 200m, Nov(10), estimated: true);
        var debit = Debit("CONTA DE LUZ", 188.40m, Nov(10));

        var match = Assert.Single(PendingBankMatcher.Match([debit], [luz], NoHistory));

        Assert.Equal((BankMatchRule.SafeMatcher, debit.Id), (match.Rule, match.Debit.Id));
    }

    [Fact]
    public void TheHistoryOfTheSameRecurrenceAssociatesWhatTheDescriptionAloneNeverWould()
    {
        var recurrence = Guid.NewGuid();
        var crediario = Obligation(recurrence, "Crediário Solar", 750m, Nov(6));
        var debit = Debit("SAIDA CREDIARIO AUTOM", 748.31m, Nov(6));

        Assert.Empty(PendingBankMatcher.Match([debit], [crediario], NoHistory)); // valor próximo + descrição parcial: sem evidência, nada
        var match = Assert.Single(PendingBankMatcher.Match([debit], [crediario], History(recurrence, "SAIDA CREDIARIO AUTOM")));
        Assert.Equal(BankMatchRule.RecurrenceHistory, match.Rule);
    }

    [Fact]
    public void AGenericBoletoWithEqualValueButNoHistoryIsNeverAssociated()
    {
        var recurrence = Guid.NewGuid();
        var asttt = Obligation(recurrence, "ASTTI", 140m, Nov(10));
        var boleto = Debit("Pagamento de boleto DDA PAG TIT", 140m, Nov(10));

        Assert.Empty(PendingBankMatcher.Match([boleto], [asttt], NoHistory));
        Assert.Empty(PendingBankMatcher.Match([boleto], [asttt], History(Guid.NewGuid(), "Pagamento de boleto DDA PAG TIT"))); // histórico de OUTRA recorrência não vale
    }

    [Fact]
    public void AnotherObligationThatCouldBeTheSameDebitBlocksTheAssociation()
    {
        var recurrence = Guid.NewGuid();
        var asttt = Obligation(recurrence, "ASTTI", 140m, Nov(10));
        var rival = Obligation(Guid.NewGuid(), "Mensalidade clube", 141m, Nov(10)); // sem histórico próprio, mas plausível para o mesmo débito
        var boleto = Debit("Pagamento de boleto DDA PAG TIT", 140m, Nov(10));

        Assert.Empty(PendingBankMatcher.Match([boleto], [asttt, rival], History(recurrence, "Pagamento de boleto DDA PAG TIT")));
    }

    [Fact]
    public void TwoDebitsWithTheSameEvidenceForOneObligationStayAmbiguous()
    {
        var recurrence = Guid.NewGuid();
        var asttt = Obligation(recurrence, "ASTTI", 140m, Nov(10));
        var first = Debit("Pagamento de boleto DDA PAG TIT", 140m, Nov(10));
        var second = Debit("Pagamento de boleto DDA PAG TIT", 139m, Nov(11));

        Assert.Empty(PendingBankMatcher.Match([first, second], [asttt], History(recurrence, "Pagamento de boleto DDA PAG TIT")));
    }

    [Fact]
    public void ADifferentBoletoOnTheSameDayDoesNotBlockTheOneWithHistoricalEvidence()
    {
        var recurrence = Guid.NewGuid();
        var asttt = Obligation(recurrence, "ASTTI", 140m, Nov(10));
        var dda = Debit("Pagamento de boleto DDA PAG TIT", 140m, Nov(10));
        var itau = Debit("Pagamento de boleto ITAU UNIBANCO HOLDING S.A.", 135.58m, Nov(10));

        var match = Assert.Single(PendingBankMatcher.Match([dda, itau], [asttt], History(recurrence, "Pagamento de boleto DDA PAG TIT")));

        Assert.Equal(dda.Id, match.Debit.Id);
    }

    [Fact]
    public void ValueOutsideTheHistoryToleranceOrADateOutsideTheWindowIsNotAssociated()
    {
        var recurrence = Guid.NewGuid();
        var crediario = Obligation(recurrence, "Crediário Solar", 750m, Nov(6));
        var history = History(recurrence, "SAIDA CREDIARIO AUTOM");

        Assert.Empty(PendingBankMatcher.Match([Debit("SAIDA CREDIARIO AUTOM", 600m, Nov(6))], [crediario], history));  // -20%
        Assert.Empty(PendingBankMatcher.Match([Debit("SAIDA CREDIARIO AUTOM", 748.31m, Nov(20))], [crediario], history)); // 14 dias
    }


    [Fact]
    public void PriorPeriodEvidence_OnlyAUniqueMovementOfTheSameAccountDateAndValueCountsForOneRecurrence()
    {
        var crediario = new PriorOccurrence(Guid.NewGuid(), Account, new DateOnly(2026, 10, 6), 750m);
        var october = new PostedMovement(Guid.NewGuid(), Account, new DateOnly(2026, 10, 6), 748.31m, "Saída CREDIARIO AUTOM");

        Assert.Equal("Saída CREDIARIO AUTOM", PriorPeriodEvidence.Select([crediario], [october])[crediario.RecurrenceId]);
        Assert.Empty(PriorPeriodEvidence.Select([crediario], [october with { Amount = 600m }]));                                   // valor fora de 5%
        Assert.Empty(PriorPeriodEvidence.Select([crediario], [october with { Date = new DateOnly(2026, 10, 12) }]));              // data fora de 3 dias
        Assert.Empty(PriorPeriodEvidence.Select([crediario], [october, october with { Id = Guid.NewGuid(), Description = "OUTRO" }])); // duas candidatas: ambíguo
        Assert.Empty(PriorPeriodEvidence.Select([crediario, crediario with { RecurrenceId = Guid.NewGuid() }], [october]));         // a mesma movimentação serviria a duas recorrências
    }
    [Fact]
    public void TheDiagnosisExplainsWhatIsMissingToAutomate()
    {
        var recurrence = Guid.NewGuid();
        var crediario = Obligation(recurrence, "Crediário Solar", 750m, Nov(6));
        var debit = Debit("SAIDA CREDIARIO AUTOM", 748.31m, Nov(6));

        var withoutHistory = Assert.Single(PendingBankMatcher.Diagnose(debit, [crediario], NoHistory));
        var withOtherText = Assert.Single(PendingBankMatcher.Diagnose(debit, [crediario], History(recurrence, "OUTRO TEXTO")));

        Assert.Equal((false, false, false), (withoutHistory.SafeMatcherDescription, withoutHistory.HasHistory, withoutHistory.HistoryEvidence));
        Assert.Equal((true, false), (withOtherText.HasHistory, withOtherText.HistoryEvidence));
        Assert.True(withoutHistory.AmountDeltaPercent is > -1m and < 0m);
    }
}
