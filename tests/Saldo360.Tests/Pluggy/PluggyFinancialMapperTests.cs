using Saldo360.Application.Pluggy;
using Saldo360.Domain.Accounts;
using Saldo360.Domain.Categories;
using Saldo360.Domain.Pluggy;

namespace Saldo360.Tests.Pluggy;

/// <summary>Regras puras de transformação Pluggy → Saldo360 (sem banco nem rede).</summary>
[Trait("Category", "CORE")]
[Trait("Area", "Pluggy")]
public sealed class PluggyFinancialMapperTests
{
    private static readonly DateOnly Today = new(2026, 10, 28);

    private static ImportedFacts Facts(PluggyLinkedAccountKind kind = PluggyLinkedAccountKind.Bank, string? type = "DEBIT", decimal? amount = -10.25m, string status = "Posted",
        string? operation = "BOLETO", string day = "2026-10-05", PluggyTransactionClass? cls = null, string reason = "BankDebit:BOLETO", string? currency = "BRL",
        int? number = null, int? total = null, string? description = "Mercado", DateOnly? importFrom = null, string? category = null)
    {
        var snapshot = new PluggyTransactionSnapshot("t", "a", kind == PluggyLinkedAccountKind.Bank ? "BANK" : "CREDIT", "Conta", DateTimeOffset.Parse(day + "T12:00:00Z"), amount, currency,
            description, description, type, Enum.Parse<PluggyTransactionStatus>(status), operation, category, number, total, null, null, null);
        var classification = cls is { } c ? new PluggyClassification(c, reason) : PluggyTransactionClassifier.Classify(snapshot);
        return new(Guid.NewGuid(), Guid.NewGuid(), kind, importFrom, snapshot.Status, type, amount, currency, snapshot.Date, description, description, operation, category, number, total,
            classification.Class, classification.Reason);
    }

    [Theory]
    [InlineData("CHECKING_ACCOUNT", AccountType.Checking, true)]
    [InlineData("SAVINGS_ACCOUNT", AccountType.Savings, true)]
    [InlineData("PAYMENT_ACCOUNT", AccountType.Checking, false)]
    [InlineData(null, AccountType.Checking, false)]
    public void BankSubtypesAreNormalizedAndUnknownOnesAreFlagged(string? subtype, AccountType expected, bool recognized) =>
        Assert.Equal((expected, recognized), PluggyFinancialMapper.AccountTypeOf(subtype));

    [Theory]
    [InlineData("BANK", PluggyLinkedAccountKind.Bank)]
    [InlineData("credit", PluggyLinkedAccountKind.Card)]
    [InlineData("INVESTMENT", null)]
    [InlineData(null, null)]
    public void OnlyBankAndCreditAccountsAreSupported(string? type, PluggyLinkedAccountKind? expected) => Assert.Equal(expected, PluggyFinancialMapper.KindOf(type));

    [Fact]
    public void PostedBankDebitAndCreditBecomeExpenseAndIncomeWithExactCents()
    {
        var expense = PluggyFinancialMapper.Decide(Facts(), Today);
        var income = PluggyFinancialMapper.Decide(Facts(type: "CREDIT", amount: 1234.56m, operation: "BOLETO"), Today);

        Assert.Equal((PluggyDisposition.BankMovement, CategoryType.Expense, 10.25m), (expense.Disposition, expense.Direction!.Value, expense.Amount));
        Assert.Equal((PluggyDisposition.BankMovement, CategoryType.Income, 1234.56m), (income.Disposition, income.Direction!.Value, income.Amount));
        Assert.Equal(new DateOnly(2026, 10, 5), expense.Date);
    }

    [Fact]
    public void DirectionComesFromTheStructuredTypeNotFromTheAmountSign()
    {
        // Pluggy já devolveu valores positivos em débitos de cartão e negativos em contas: o sinal não decide.
        var debit = PluggyFinancialMapper.Decide(Facts(type: "DEBIT", amount: 50m), Today);
        var credit = PluggyFinancialMapper.Decide(Facts(type: "CREDIT", amount: -50m, operation: "PIX"), Today);

        Assert.Equal((CategoryType.Expense, 50m), (debit.Direction!.Value, debit.Amount));
        Assert.Equal((CategoryType.Income, 50m), (credit.Direction!.Value, credit.Amount));
    }

    [Fact]
    public void PendingAndFutureDatedBankMovementsWaitAndDoNotImpactTheBalance()
    {
        Assert.Equal(PluggyDisposition.Waiting, PluggyFinancialMapper.Decide(Facts(status: "Pending"), Today).Disposition);
        var future = PluggyFinancialMapper.Decide(Facts(day: "2026-11-15"), Today);
        Assert.Equal((PluggyDisposition.Waiting, "FutureDated"), (future.Disposition, future.Reason));
    }

    [Theory]
    [InlineData("MissingTypeOrAmount", null, -10.25)]
    [InlineData("MissingTypeOrAmount", "DEBIT", null)]
    [InlineData("MissingTypeOrAmount", "DEBIT", 0.0)]
    public void MovementsWithoutTypeOrAmountNeedReview(string _, string? type, double? amount)
    {
        var decision = PluggyFinancialMapper.Decide(Facts(type: type, amount: amount is { } a ? (decimal)a : null), Today);
        Assert.Equal(PluggyDisposition.Review, decision.Disposition);
    }

    [Fact]
    public void AmountsBeyondCentsAreNeverRoundedAndForeignCurrencyIsNotMixedIn()
    {
        Assert.Equal("AmountPrecision", PluggyFinancialMapper.Decide(Facts(amount: -10.255m), Today).Reason);
        Assert.Equal("ForeignCurrency", PluggyFinancialMapper.Decide(Facts(currency: "USD"), Today).Reason);
        Assert.Equal(10.25m, PluggyFinancialMapper.ExactCents(10.2500m));
        Assert.Null(PluggyFinancialMapper.ExactCents(0.001m));
        // numeric(19,4) devolve escala 4; o valor continua exato.
        Assert.Equal(PluggyDisposition.BankMovement, PluggyFinancialMapper.Decide(Facts(amount: -10.2500m), Today).Disposition);
    }

    [Fact]
    public void MovementsBeforeTheImportStartDoNotFeedTheRealized() =>
        Assert.Equal(PluggyDisposition.OutOfRange, PluggyFinancialMapper.Decide(Facts(importFrom: new DateOnly(2026, 10, 6)), Today).Disposition);

    [Fact]
    public void TransferAndBillPaymentHintsAreCandidatesNeverPlainExpenses()
    {
        var transfer = PluggyFinancialMapper.Decide(Facts(operation: "TRANSFERENCIA_MESMA_INSTITUICAO", reason: "x", cls: PluggyTransactionClass.PotentialTransfer).WithReason("SameInstitutionTransfer"), Today);
        var hint = PluggyFinancialMapper.Decide(Facts(description: "PAGAMENTO DE FATURA CARTAO"), Today);
        var withdrawal = PluggyFinancialMapper.Decide(Facts(operation: "SAQUE"), Today);

        Assert.Equal(PluggyDisposition.TransferCandidate, transfer.Disposition);
        Assert.Equal(PluggyDisposition.BankDebitBillHint, hint.Disposition);
        Assert.Equal(("Review", "NoCounterpart:CashWithdrawal"), (withdrawal.Disposition.ToString(), withdrawal.Reason));
    }

    [Fact]
    public void PixTedAndDocAreTransferProbesAndNeverDependOnTheDescription()
    {
        foreach (var operation in new[] { "PIX", "TED", "DOC" })
        {
            Assert.Equal(PluggyDisposition.TransferProbe, PluggyFinancialMapper.Decide(Facts(type: "DEBIT", operation: operation, description: "COMPRA QUALQUER"), Today).Disposition);
            Assert.Equal(PluggyDisposition.TransferProbe, PluggyFinancialMapper.Decide(Facts(type: "CREDIT", operation: operation, description: "PARA OUTRA CONTA MINHA"), Today).Disposition);
        }
        Assert.Equal(PluggyDisposition.BankMovement, PluggyFinancialMapper.Decide(Facts(type: "DEBIT", operation: "BOLETO", description: "TRANSFERENCIA PARA MIM"), Today).Disposition);
    }

    [Fact]
    public void CardPurchasesAndInstallmentsAreConsumptionAndInstallmentsGetAStructuredSuffix()
    {
        var purchase = PluggyFinancialMapper.Decide(Facts(PluggyLinkedAccountKind.Card, "DEBIT", 150m, operation: "PAGAMENTO"), Today);
        var installment = PluggyFinancialMapper.Decide(Facts(PluggyLinkedAccountKind.Card, "DEBIT", 80m, operation: "PAGAMENTO", number: 2, total: 10, description: "TV"), Today);
        var noMetadata = PluggyFinancialMapper.Decide(Facts(PluggyLinkedAccountKind.Card, "DEBIT", 80m, operation: "PAGAMENTO", description: "TV 2/10"), Today);

        Assert.Equal(PluggyDisposition.CardPurchase, purchase.Disposition);
        Assert.Equal(("TV (2/10)", PluggyDisposition.CardPurchase), (installment.Description, installment.Disposition));
        Assert.Equal("TV 2/10", noMetadata.Description); // a descrição nunca é reinterpretada como parcela
    }

    [Fact]
    public void PendingCardPurchasesWaitAndCardBillPaymentIsEvidenceNotIncome()
    {
        Assert.Equal(PluggyDisposition.Waiting, PluggyFinancialMapper.Decide(Facts(PluggyLinkedAccountKind.Card, "DEBIT", 150m, status: "Pending", operation: "PAGAMENTO"), Today).Disposition);
        var payment = PluggyFinancialMapper.Decide(Facts(PluggyLinkedAccountKind.Card, "CREDIT", 1500m, operation: "PAGAMENTO_FATURA"), Today);
        Assert.Equal(PluggyDisposition.CardBillPayment, payment.Disposition);
    }

    [Fact]
    public void CardCreditsWithoutStructuredEvidenceAreNeverIncomeAndGoToReview()
    {
        var unknown = PluggyFinancialMapper.Decide(Facts(PluggyLinkedAccountKind.Card, "CREDIT", 40m, operation: "OUTRO"), Today);
        var transfers = PluggyFinancialMapper.Decide(Facts(PluggyLinkedAccountKind.Card, "CREDIT", 40m, operation: "OUTROS", category: "Transfers"), Today);

        Assert.Equal(PluggyDisposition.Review, unknown.Disposition);
        Assert.Equal(PluggyDisposition.Review, transfers.Disposition);
    }

    [Theory]
    [InlineData("ESTORNO", null, "CardRefund")]
    [InlineData("OUTROS", "Cashback", "CardCashback")]
    [InlineData("OUTROS", "Shopping", "CardPurchaseCredit")]
    public void CardRefundsCashbackAndPurchaseCreditsBecomeInvoiceCreditsNeverIncome(string operation, string? category, string reason)
    {
        var decision = PluggyFinancialMapper.Decide(Facts(PluggyLinkedAccountKind.Card, "CREDIT", 40m, operation: operation, category: category), Today);

        Assert.Equal((PluggyDisposition.CardCredit, reason), (decision.Disposition, decision.Reason));
        Assert.Equal(40m, decision.Amount);
    }

    [Theory]
    [InlineData("Tax on financial operations", "CardCharge")]
    [InlineData("Bank fees", "CardCharge")]
    [InlineData("Interests charged", "CardCharge")]
    [InlineData("Late payment and overdraft costs", "CardCharge")]
    [InlineData("Clothing", "CardPurchaseByCategory")]
    public void CardChargesAndGenericOperationsWithAConsumptionCategoryAreCardConsumption(string category, string reason)
    {
        var decision = PluggyFinancialMapper.Decide(Facts(PluggyLinkedAccountKind.Card, "DEBIT", 12m, operation: "OUTROS", category: category), Today);

        Assert.Equal((PluggyDisposition.CardPurchase, reason), (decision.Disposition, decision.Reason));
    }

    [Theory]
    [InlineData("OPERACOES_CREDITO_CONTRATADAS_CARTAO", "Transfers")]
    [InlineData("OUTROS", "Transfer - PIX")]
    [InlineData("OUTROS", null)]
    public void ContractedCreditOperationsAndTransfersOnTheCardAreNotConsumptionWithoutADecision(string operation, string? category)
    {
        var decision = PluggyFinancialMapper.Decide(Facts(PluggyLinkedAccountKind.Card, "DEBIT", 12m, operation: operation, category: category), Today);

        Assert.Equal(PluggyDisposition.Review, decision.Disposition);
    }

    [Fact]
    public void BankRefundsAndCreditOperationsNeedReview()
    {
        Assert.Equal("PossibleRefund", PluggyFinancialMapper.Decide(Facts(type: "CREDIT", amount: 30m, operation: "ESTORNO"), Today).Reason);
        Assert.Equal(PluggyDisposition.Review, PluggyFinancialMapper.Decide(Facts(type: "CREDIT", amount: 3000m, operation: "OPERACAO_CREDITO"), Today).Disposition);
    }

    [Fact]
    public void UserDecisionOverridesTheAutomaticOneOnlyInTheMatchingDirection()
    {
        var facts = Facts(operation: "SAQUE");
        Assert.Equal((PluggyDisposition.BankMovement, CategoryType.Expense), Pair(PluggyFinancialMapper.Decide(facts, Today, PluggyResolution.AsExpense)));
        Assert.Equal(PluggyDisposition.Review, PluggyFinancialMapper.Decide(facts, Today, PluggyResolution.AsIncome).Disposition);

        static (PluggyDisposition, CategoryType) Pair(PluggyMovement m) => (m.Disposition, m.Direction!.Value);
    }

    [Fact]
    public void CategoriesMapByNameOrKnownAliasOnlyWhenTheBudgetHasThem()
    {
        var food = new MappableCategory(Guid.NewGuid(), "Alimentação", CategoryType.Expense, true);
        var health = new MappableCategory(Guid.NewGuid(), "Saúde", CategoryType.Expense, false);
        var salary = new MappableCategory(Guid.NewGuid(), "Salário", CategoryType.Income, true);
        var categories = new[] { food, health, salary };

        Assert.Equal(food.Id, PluggyCategoryMapper.Map("Groceries", CategoryType.Expense, categories));
        Assert.Equal(food.Id, PluggyCategoryMapper.Map("alimentacao", CategoryType.Expense, categories));
        Assert.Equal(salary.Id, PluggyCategoryMapper.Map("SALARIO", CategoryType.Income, categories));
        Assert.Null(PluggyCategoryMapper.Map("Health", CategoryType.Expense, categories)); // inativa
        Assert.Null(PluggyCategoryMapper.Map("Groceries", CategoryType.Income, categories)); // tipo errado
        Assert.Null(PluggyCategoryMapper.Map("Algo desconhecido", CategoryType.Expense, categories));
        Assert.Null(PluggyCategoryMapper.Map(null, CategoryType.Expense, categories));
    }

    private static PairItem Item(decimal amount, string day, Guid? link = null, bool hint = false) => new(Guid.NewGuid(), link ?? Guid.NewGuid(), amount, DateOnly.Parse(day), hint);

    [Fact]
    public void UniqueEqualAmountWithinTheToleranceIsPaired()
    {
        var source = Item(100m, "2026-10-10"); var target = Item(100m, "2026-10-11");

        var result = PluggyPairing.Match([source], [target], 1, differentLinks: true);

        Assert.Equal([(source.Id, target.Id)], result.Pairs);
        Assert.Empty(result.Ambiguous);
    }

    [Fact]
    public void DifferentAmountOrFarDatesOrSameAccountAreNeverPaired()
    {
        var link = Guid.NewGuid();
        Assert.Empty(PluggyPairing.Match([Item(100m, "2026-10-10")], [Item(100.01m, "2026-10-10")], 3, true).Pairs);
        Assert.Empty(PluggyPairing.Match([Item(100m, "2026-10-10")], [Item(100m, "2026-10-14")], 3, true).Pairs);
        Assert.Empty(PluggyPairing.Match([Item(100m, "2026-10-10", link)], [Item(100m, "2026-10-10", link)], 3, true).Pairs);
    }

    [Fact]
    public void AmbiguousCandidatesAreNotPairedAndAreFlaggedForReview()
    {
        var source = Item(100m, "2026-10-10"); var a = Item(100m, "2026-10-10"); var b = Item(100m, "2026-10-11");

        var result = PluggyPairing.Match([source], [a, b], 3, true);

        Assert.Empty(result.Pairs);
        Assert.Equal(new[] { source.Id, a.Id, b.Id }.Order(), result.Ambiguous.Order());
    }

    [Fact]
    public void TwoSourcesCompetingForOneTargetAreAmbiguous()
    {
        var s1 = Item(100m, "2026-10-10"); var s2 = Item(100m, "2026-10-10"); var target = Item(100m, "2026-10-10");

        var result = PluggyPairing.Match([s1, s2], [target], 3, true);

        Assert.Empty(result.Pairs);
        Assert.Contains(target.Id, result.Ambiguous);
    }

    [Fact]
    public void ADescriptionHintOnlyBreaksTiesNeverCreatesAMatch()
    {
        var source = Item(100m, "2026-10-10"); var plain = Item(100m, "2026-10-10"); var hinted = Item(100m, "2026-10-10", hint: true);

        var tied = PluggyPairing.Match([source], [plain, hinted], 3, true);
        var onlyHint = PluggyPairing.Match([Item(100m, "2026-10-10")], [Item(250m, "2026-10-10", hint: true)], 3, true);

        Assert.Equal([(source.Id, hinted.Id)], tied.Pairs);
        Assert.Empty(onlyHint.Pairs);
    }

    // ---- Pareamento do mesmo dia, normalização de operações, datas e dica de devolução ---------------------------------------------

    [Theory]
    [InlineData("pix", "PIX")]
    [InlineData("  Pix ", "PIX")]
    [InlineData("TED", "TED")]
    [InlineData("Transferência Mesma-Instituição", "TRANSFERENCIA_MESMA_INSTITUICAO")]
    [InlineData("devolução", "DEVOLUCAO")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void OperationCodesAreNormalizedInOneConsistentWay(string? raw, string expected) => Assert.Equal(expected, PluggyOperationCodes.Normalize(raw));

    [Fact]
    public void TheMapperUsesTheNormalizedOperationEverywhere()
    {
        Assert.Equal(PluggyDisposition.TransferProbe, PluggyFinancialMapper.Decide(Facts(type: "CREDIT", amount: 10m, operation: " pix "), Today).Disposition);
        Assert.Equal("PossibleRefund", PluggyFinancialMapper.Decide(Facts(type: "CREDIT", amount: 30m, operation: "estorno"), Today).Reason);
    }

    [Fact]
    public void TheBusinessDateIsTheUtcCalendarDayOfTheBankInstantWithoutShiftingToSaoPaulo()
    {
        // A Pluggy entrega a data bancária à meia-noite UTC: converter para America/Sao_Paulo (UTC-3) a deslocaria para a véspera.
        Assert.Equal(new DateOnly(2026, 10, 9), PluggyFinancialMapper.BusinessDate(DateTimeOffset.Parse("2026-10-09T00:00:00.000Z")));
        Assert.Equal(new DateOnly(2026, 10, 9), PluggyFinancialMapper.BusinessDate(DateTimeOffset.Parse("2026-10-09T12:00:00Z")));
    }

    [Fact]
    public void ACreditWhoseDescriptionMentionsAReturnGoesToReviewAndIsNeverConfirmedAsARefundOrIncomeByItself()
    {
        var refund = PluggyFinancialMapper.Decide(Facts(type: "CREDIT", amount: 55.99m, operation: "PIX", description: "Pix devolvido Pix Marketplace"), Today);
        var plainPix = PluggyFinancialMapper.Decide(Facts(type: "CREDIT", amount: 320m, operation: "PIX", description: "Pix recebido DEIVID SILVA SILVA"), Today);
        var debitSameWords = PluggyFinancialMapper.Decide(Facts(type: "DEBIT", amount: -55.99m, operation: "PIX", description: "Pagamento de Pix QR Code Pix Marketplace"), Today);

        Assert.Equal((PluggyDisposition.Review, "PossibleRefundByDescription"), (refund.Disposition, refund.Reason));
        Assert.Equal(PluggyDisposition.TransferProbe, plainPix.Disposition); // sem dica: segue como recebimento de terceiro (receita da conta)
        Assert.Equal(PluggyDisposition.TransferProbe, debitSameWords.Disposition);
    }

    [Fact]
    public void AUserDecisionOverridesTheRefundDescriptionHint()
    {
        var facts = Facts(type: "CREDIT", amount: 55.99m, operation: "PIX", description: "Pix devolvido Pix Marketplace");

        Assert.Equal(PluggyDisposition.BankMovement, PluggyFinancialMapper.Decide(facts, Today, PluggyResolution.AsIncome).Disposition);
    }

    [Theory]
    [InlineData("Pix devolvido", true)]
    [InlineData("DEVOLUCAO PIX", true)]
    [InlineData("Estorno compra", true)]
    [InlineData("Reembolso viagem", true)]
    [InlineData("Pix recebido DEIVID SILVA SILVA", false)]
    [InlineData("Pagamento de Pix QR Code", false)]
    [InlineData(null, false)]
    public void TheRefundHintLooksOnlyAtWholeWords(string? description, bool expected) => Assert.Equal(expected, PluggyFinancialMapper.DescribesRefund(description, null));

    [Fact]
    public void PairsOnlyMatchOnTheSameDayWhenToleranceIsZeroAndDifferentDaysAreReportedForReviewInstead()
    {
        var debit = Item(100m, "2026-10-10"); var sameDay = Item(100m, "2026-10-10"); var otherDay = Item(100m, "2026-10-11");

        var matched = PluggyPairing.Match([debit], [sameDay], 0, differentLinks: true);
        var notMatched = PluggyPairing.Match([debit], [otherDay], 0, differentLinks: true);
        var mismatch = PluggyPairing.DateMismatches([debit], [otherDay], 1, differentLinks: true);

        Assert.Equal([(debit.Id, sameDay.Id)], matched.Pairs);
        Assert.Empty(notMatched.Pairs);
        Assert.Equal(new[] { debit.Id, otherDay.Id }.Order(), mismatch.Order());
    }

    [Fact]
    public void DateMismatchesIgnoreSameDayFarDatesDifferentAmountsSameAccountAndRejectedPairs()
    {
        var link = Guid.NewGuid();
        var source = Item(100m, "2026-10-10");
        Assert.Empty(PluggyPairing.DateMismatches([source], [Item(100m, "2026-10-10")], 1, true)); // mesmo dia não é divergência de data
        Assert.Empty(PluggyPairing.DateMismatches([source], [Item(100m, "2026-10-13")], 1, true));
        Assert.Empty(PluggyPairing.DateMismatches([source], [Item(100.01m, "2026-10-11")], 1, true));
        Assert.Empty(PluggyPairing.DateMismatches([Item(100m, "2026-10-10", link)], [Item(100m, "2026-10-11", link)], 1, true));
        var near = Item(100m, "2026-10-11");
        Assert.Empty(PluggyPairing.DateMismatches([source], [near], 1, true, (a, b) => a == source.Id && b == near.Id));
    }

    [Fact]
    public void ARejectedPairIsNeverMatchedButTheEndsStayFreeForOtherCounterparts()
    {
        var debit = Item(100m, "2026-10-10"); var rejectedCredit = Item(100m, "2026-10-10"); var otherDebit = Item(100m, "2026-10-10");

        var result = PluggyPairing.Match([debit, otherDebit], [rejectedCredit], 0, true, (a, b) => a == debit.Id && b == rejectedCredit.Id);

        Assert.Equal([(otherDebit.Id, rejectedCredit.Id)], result.Pairs);
    }

    private static TransferSide Side(bool debit, string operation = "PIX", string day = "2026-10-10", decimal amount = 100m, Guid? link = null, Guid? account = null,
        PluggyDisposition disposition = PluggyDisposition.TransferProbe, PluggyPostingState state = PluggyPostingState.NeedsReview, PluggyPostingKind kind = PluggyPostingKind.None,
        PluggyResolution? resolution = null, PluggyLinkedAccountKind linkKind = PluggyLinkedAccountKind.Bank) =>
        new(Guid.NewGuid(), link ?? Guid.NewGuid(), account ?? Guid.NewGuid(), linkKind, disposition, debit, amount, DateOnly.Parse(day), operation, state, kind, null, resolution);

    [Fact]
    public void EligibilityAcceptsOppositeSidesWithSameAmountDayAndOperationOnDistinctBankAccounts() =>
        Assert.Null(PluggyTransferEligibility.Check(Side(debit: true), Side(debit: false)));

    [Fact]
    public void EligibilityReportsTheFirstBlockingReason()
    {
        Assert.Equal(PluggyTransferEligibility.Reasons.SameDirection, PluggyTransferEligibility.Check(Side(true), Side(true)));
        var sharedLink = Guid.NewGuid();
        Assert.Equal(PluggyTransferEligibility.Reasons.SameAccount, PluggyTransferEligibility.Check(Side(true, link: sharedLink), Side(false, link: sharedLink)));
        Assert.Equal(PluggyTransferEligibility.Reasons.NotBankAccount, PluggyTransferEligibility.Check(Side(true), Side(false, linkKind: PluggyLinkedAccountKind.Card)));
        Assert.Equal(PluggyTransferEligibility.Reasons.ManualDecision, PluggyTransferEligibility.Check(Side(true), Side(false, resolution: PluggyResolution.AsIncome)));
        Assert.Equal(PluggyTransferEligibility.Reasons.NotTransferEvidence, PluggyTransferEligibility.Check(Side(true), Side(false, disposition: PluggyDisposition.BankMovement)));
        Assert.Equal(PluggyTransferEligibility.Reasons.EndUnavailable, PluggyTransferEligibility.Check(Side(true), Side(false, state: PluggyPostingState.Ignored)));
        Assert.Equal(PluggyTransferEligibility.Reasons.EndUsedElsewhere, PluggyTransferEligibility.Check(Side(true), Side(false, state: PluggyPostingState.Applied, kind: PluggyPostingKind.CardBillPayment)));
        Assert.Equal(PluggyTransferEligibility.Reasons.DifferentAmount, PluggyTransferEligibility.Check(Side(true), Side(false, amount: 100.01m)));
        Assert.Equal(PluggyTransferEligibility.Reasons.DifferentDate, PluggyTransferEligibility.Check(Side(true), Side(false, day: "2026-10-11")));
        Assert.Equal(PluggyTransferEligibility.Reasons.IncompatibleOperation, PluggyTransferEligibility.Check(Side(true, "PIX"), Side(false, "TED")));
        Assert.Equal(PluggyTransferEligibility.Reasons.IncompatibleOperation, PluggyTransferEligibility.Check(Side(true, "BOLETO"), Side(false, "BOLETO")));
    }
}

internal static class FactsExtensions
{
    public static ImportedFacts WithReason(this ImportedFacts facts, string reason) => facts with { ClassReason = reason };
}
