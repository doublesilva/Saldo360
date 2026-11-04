using Saldo360.Application.Calendar;
using Saldo360.Application.Recurrences;
using Saldo360.Domain.Categories;

namespace Saldo360.Tests.Application;

/// <summary>PROJ-03: um débito bancário pendente da Pluggy afeta o saldo projetado no máximo uma vez e nunca é confirmado por valor igual.</summary>
[Trait("Category", "CORE")]
[Trait("Area", "Planning")]
public sealed class PendingBankCoverageTests
{
    private static readonly Guid Pf = Guid.NewGuid();
    private static readonly Guid Pj = Guid.NewGuid();
    private static DateOnly Nov(int day) => new(2026, 11, day);

    private static FixedObligation Obligation(string description, decimal amount, DateOnly due, Guid? account = null, bool estimated = false, bool virtualOccurrence = true, Guid? recurrence = null,
        ObligationSituation situation = ObligationSituation.Planned) =>
        new(Guid.NewGuid(), recurrence ?? Guid.NewGuid(), 1, new(due.Year, due.Month, 1), virtualOccurrence ? null : Guid.NewGuid(), account ?? Pf, Guid.NewGuid(), CategoryType.Expense, default,
            description, amount, estimated, due, situation, virtualOccurrence);

    private static PendingBankDebit Debit(string description, decimal amount, DateOnly date, Guid? account = null) => new(Guid.NewGuid(), account ?? Pf, amount, date, description);

    private static PendingBankCoverageState State(IReadOnlyList<PendingBankClassification> result, PendingBankDebit debit) => result.Single(r => r.Debit.Id == debit.Id).State;

    [Fact]
    public void AConfirmedCompetenceValueCoversTheBankDebit_OnlyWhenTheDescriptionIsAlsoCompatible()
    {
        var crediario = Debit("SAIDA CREDIARIO AUTOM", 748.31m, Nov(6));
        var asBase = Obligation("Crediário", 750m, Nov(6));
        var confirmed = Obligation("Crediário", 748.31m, Nov(6), virtualOccurrence: false);
        var otherName = Obligation("Crediário Solar", 748.31m, Nov(6), virtualOccurrence: false);

        Assert.Equal(PendingBankCoverageState.Ambiguous, State(PendingBankCoverage.Classify([crediario], [asBase], []), crediario));
        Assert.Equal(PendingBankCoverageState.Ambiguous, State(PendingBankCoverage.Classify([crediario], [otherName], []), crediario)); // valor igual não basta: a descrição genérica não associa sozinha
        var covered = Assert.Single(PendingBankCoverage.Classify([crediario], [confirmed], []));
        Assert.Equal((PendingBankCoverageState.Covered, confirmed.StableId), (covered.State, covered.CoveredBy));
    }

    [Fact]
    public void AGenericBoletoNeverCoversAnObligationByValueAlone_EvenWhenTheValueIsConfirmed()
    {
        var boleto = Debit("Pagamento de boleto DDA", 140m, Nov(10));
        var asti = Obligation("ASTTI", 140m, Nov(10), virtualOccurrence: false);

        Assert.Equal(PendingBankCoverageState.Ambiguous, State(PendingBankCoverage.Classify([boleto], [asti], []), boleto));
    }

    [Fact]
    public void ADebitWithAUniqueSafeMatchIsCoveredAndAddsNothing()
    {
        var dmae = Debit("Débito automático DA DMAE-P", 138.60m, Nov(11));
        var planned = Obligation("DMAE — novembro/2026", 138.60m, Nov(11), virtualOccurrence: false);

        var result = PendingBankCoverage.Classify([dmae], [planned], []);

        var item = Assert.Single(result);
        Assert.Equal((PendingBankCoverageState.Covered, planned.StableId), (item.State, item.CoveredBy));
    }

    [Fact]
    public void ADebitWithNoPlausibleCandidateInTheSameAccountAndPeriodIsUncovered()
    {
        var debit = Debit("PIX PADARIA", 40m, Nov(5));
        var farAway = Obligation("Padaria", 40m, Nov(20));            // data fora das 3 dias
        var otherAccount = Obligation("Padaria", 40m, Nov(5), Pj);    // outra conta
        var otherValue = Obligation("Padaria", 400m, Nov(5));         // valor longe
        var cancelledOrDone = Obligation("Padaria", 40m, Nov(5), situation: ObligationSituation.Realized);

        var result = PendingBankCoverage.Classify([debit], [farAway, otherAccount, otherValue, cancelledOrDone], []);

        Assert.Equal(PendingBankCoverageState.Uncovered, State(result, debit));
    }

    [Fact]
    public void ACandidateWithoutASafeMatchMakesTheDebitAmbiguousInsteadOfAdditionalExpense()
    {
        var crediario = Debit("SAIDA CREDIARIO AUTOM", 748.31m, Nov(6));
        var boleto = Debit("Pagamento de boleto DDA PAG", 140m, Nov(10));
        var obligations = new[] { Obligation("Crediário Solar", 750m, Nov(6)), Obligation("ASTTI", 140m, Nov(10)) };

        var result = PendingBankCoverage.Classify([crediario, boleto], obligations, []);

        Assert.Equal((PendingBankCoverageState.Ambiguous, PendingBankCoverageState.Ambiguous), (State(result, crediario), State(result, boleto)));
    }

    [Fact]
    public void TwoDebitsOfTheSameValueNeverConfirmTheSameObligation()
    {
        var first = Debit("Débito automático DA DMAE-P", 138.60m, Nov(11));
        var second = Debit("Débito automático DA DMAE-P", 138.60m, Nov(11));
        var planned = Obligation("DMAE — novembro/2026", 138.60m, Nov(11), virtualOccurrence: false);

        var result = PendingBankCoverage.Classify([first, second], [planned], []);

        Assert.All(result, r => Assert.Equal(PendingBankCoverageState.Ambiguous, r.State)); // sem par inequívoco, ninguém é confirmado nem somado
    }

    [Fact]
    public void AnObligationThatConfirmsOneDebitIsNoLongerACandidateForAnother()
    {
        var confirmed = Debit("Débito automático DA DMAE-P", 138.60m, Nov(11));
        var stray = Debit("PIX OUTRO", 138.60m, Nov(12));
        var planned = Obligation("DMAE — novembro/2026", 138.60m, Nov(11), virtualOccurrence: false);

        var result = PendingBankCoverage.Classify([confirmed, stray], [planned], []);

        Assert.Equal((PendingBankCoverageState.Covered, PendingBankCoverageState.Uncovered), (State(result, confirmed), State(result, stray)));
    }

    [Fact]
    public void CardInvoicesTransfersAndDebtInstallmentsOnlyDetectAmbiguityAndNeverConfirm()
    {
        var invoicePayment = Debit("Débito automático Fatura Personnalité", 6806.73m, Nov(15));
        var transfer = Debit("PIX para conta própria", 2000m, Nov(7));
        var candidates = new[]
        {
            new PendingBankCandidate(Guid.Empty, Pf, 6806.73m, Nov(15), "CardInvoice"),
            new PendingBankCandidate(Guid.NewGuid(), Pf, 2000m, Nov(8), "PlannedTransfer"),
        };

        var result = PendingBankCoverage.Classify([invoicePayment, transfer], [], candidates);

        Assert.Equal((PendingBankCoverageState.Ambiguous, PendingBankCoverageState.Ambiguous), (State(result, invoicePayment), State(result, transfer)));
    }

    [Fact]
    public void ANonPositiveAmountIsIgnoredAndTheClassificationKeepsEveryDebitTraceable()
    {
        var zero = Debit("ESTORNO", 0m, Nov(5));
        var normal = Debit("PIX PADARIA", 40m, Nov(5));

        var result = PendingBankCoverage.Classify([zero, normal], [], []);

        Assert.Equal(2, result.Count);
        Assert.Equal((PendingBankCoverageState.Ignored, PendingBankCoverageState.Uncovered), (State(result, zero), State(result, normal)));
    }

    [Fact]
    public void NovemberReferenceCaseCoversDmaeAndKeepsEightHundredEightyEightAndThirtyOneCentsAmbiguous()
    {
        var debits = new[] { Debit("SAIDA CREDIARIO AUTOM", 748.31m, Nov(6)), Debit("Pagamento de boleto DDA PAG", 140m, Nov(10)), Debit("Débito automático DA DMAE-P", 138.60m, Nov(11)) };
        var obligations = new[]
        {
            Obligation("Crediário Solar", 750m, Nov(6)), Obligation("ASTTI", 140m, Nov(10)),
            Obligation("DMAE — novembro/2026", 138.60m, Nov(11), virtualOccurrence: false), Obligation("IPTU Casa Salso — parcela 2/3", 319.35m, Nov(8), virtualOccurrence: false),
        };

        var result = PendingBankCoverage.Classify(debits, obligations, []);

        decimal Sum(PendingBankCoverageState state) => result.Where(r => r.State == state).Sum(r => r.Debit.Amount);
        Assert.Equal((0m, 138.60m, 888.31m), (Sum(PendingBankCoverageState.Uncovered), Sum(PendingBankCoverageState.Covered), Sum(PendingBankCoverageState.Ambiguous)));
    }
}
