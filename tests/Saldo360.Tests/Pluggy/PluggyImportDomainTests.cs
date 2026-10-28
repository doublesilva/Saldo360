using Saldo360.Domain.Pluggy;

namespace Saldo360.Tests.Pluggy;

[Trait("Category", "CORE")]
[Trait("Area", "Pluggy")]
public sealed class PluggyImportDomainTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Workspace = Guid.NewGuid();
    private static readonly Guid Connection = Guid.NewGuid();
    private static readonly Guid Run1 = Guid.NewGuid();
    private static readonly Guid Run2 = Guid.NewGuid();

    private static PluggyTransactionSnapshot Bank(string type = "DEBIT", string? operation = "PIX", string? description = "MERCADO", decimal? amount = -10m,
        PluggyTransactionStatus status = PluggyTransactionStatus.Posted) =>
        new("tx-1", "acc-1", "BANK", "Conta ••••6789", Now.AddDays(-1), amount, "BRL", description, description, type, status, operation, "Groceries", null, null, null, null, null);

    private static PluggyTransactionSnapshot Card(string type = "DEBIT", string? operation = "PAGAMENTO", int? installments = null, int? number = null, decimal? amount = 50m) =>
        new("tx-2", "acc-2", "CREDIT", "Cartão ••••2222", Now.AddDays(-1), amount, "BRL", "LOJA", "LOJA", type, PluggyTransactionStatus.Posted, operation, "Shopping",
            number, installments, "bill-1", "2222", null);

    [Theory]
    [InlineData("DEBIT", "PIX", "MERCADO", PluggyTransactionClass.PotentialExpense)]
    [InlineData("CREDIT", "PIX", "SALARIO", PluggyTransactionClass.PotentialIncome)]
    [InlineData("DEBIT", "BOLETO", "CONTA LUZ", PluggyTransactionClass.PotentialExpense)]
    [InlineData("DEBIT", "TRANSFERENCIA_MESMA_INSTITUICAO", "PARA POUPANCA", PluggyTransactionClass.PotentialTransfer)]
    [InlineData("CREDIT", "RESGATE_APLIC_FINANCEIRA", "RESGATE CDB", PluggyTransactionClass.PotentialTransfer)]
    [InlineData("DEBIT", "SAQUE", "SAQUE 24H", PluggyTransactionClass.PotentialTransfer)]
    [InlineData("CREDIT", "RENDIMENTO_APLIC_FINANCEIRA", "REND PAGO", PluggyTransactionClass.PotentialIncome)]
    [InlineData("CREDIT", "OPERACAO_CREDITO", "EMPRESTIMO", PluggyTransactionClass.InsufficientInformation)]
    [InlineData("DEBIT", "OUTROS", "PAGAMENTO FATURA CARTAO", PluggyTransactionClass.PotentialTransfer)]
    [InlineData("DEBIT", "BOLETO", "PGTO FATURA ITAU", PluggyTransactionClass.PotentialTransfer)]
    [InlineData("DEBIT", "OUTROS", "FATURA DE ENERGIA", PluggyTransactionClass.PotentialExpense)]
    public void ClassifiesBankAccountRecordsFromStructuredFields(string type, string operation, string description, PluggyTransactionClass expected) =>
        Assert.Equal(expected, PluggyTransactionClassifier.Classify(Bank(type, operation, description)).Class);

    [Fact]
    public void CardBillPaymentIsATransferNeverAConsumptionExpense()
    {
        var payment = PluggyTransactionClassifier.Classify(Card("CREDIT", "PAGAMENTO_FATURA"));
        Assert.Equal((PluggyTransactionClass.PotentialTransfer, "CardBillPayment"), (payment.Class, payment.Reason));
        // Mesmo se o banco a marcar como débito.
        Assert.Equal(PluggyTransactionClass.PotentialTransfer, PluggyTransactionClassifier.Classify(Card("DEBIT", "PAGAMENTO_FATURA")).Class);
    }

    [Theory]
    [InlineData("DEBIT", "PAGAMENTO", null, null, PluggyTransactionClass.CardPurchase)]
    [InlineData("DEBIT", "PAGAMENTO", 1, 1, PluggyTransactionClass.CardPurchase)]
    [InlineData("DEBIT", "PAGAMENTO", 10, 3, PluggyTransactionClass.CardInstallment)]
    [InlineData("DEBIT", "TARIFA", null, null, PluggyTransactionClass.PotentialExpense)]
    [InlineData("CREDIT", "ESTORNO", null, null, PluggyTransactionClass.InsufficientInformation)]
    [InlineData("CREDIT", "OUTROS", null, null, PluggyTransactionClass.InsufficientInformation)]
    [InlineData("DEBIT", "OPERACOES_CREDITO_CONTRATADAS_CARTAO", null, null, PluggyTransactionClass.InsufficientInformation)]
    [InlineData("DEBIT", "OUTROS", null, null, PluggyTransactionClass.InsufficientInformation)]
    public void ClassifiesCardRecords(string type, string operation, int? installments, int? number, PluggyTransactionClass expected) =>
        Assert.Equal(expected, PluggyTransactionClassifier.Classify(Card(type, operation, installments, number)).Class);

    [Fact]
    public void InstallmentsAreNeverInferredFromTheDescription()
    {
        var noMetadata = Card() with { Description = "LOJA PARC 03/10", DescriptionRaw = "LOJA PARC 03/10" };
        Assert.Equal(PluggyTransactionClass.CardPurchase, PluggyTransactionClassifier.Classify(noMetadata).Class);
    }

    [Theory]
    [InlineData(null, "DEBIT")]
    [InlineData(0, "DEBIT")]
    [InlineData(10, null)]
    [InlineData(10, "WHATEVER")]
    public void MissingTypeOrAmountIsInsufficientInformation(int? amount, string? type) =>
        Assert.Equal(PluggyTransactionClass.InsufficientInformation, PluggyTransactionClassifier.Classify(Bank(type ?? "", amount: amount, operation: "PIX") with { Type = type }).Class);

    [Fact]
    public void CreateStoresIdentityAndFirstObservation()
    {
        var tx = PluggyImportedTransaction.Create(Workspace, Connection, Bank(), Run1, Now);
        Assert.Equal(("tx-1", "acc-1", Run1, Run1, Now), (tx.PluggyTransactionId, tx.PluggyAccountId, tx.FirstSeenRunId, tx.LastSeenRunId, tx.FirstImportedAt));
        Assert.Equal(PluggyTransactionClass.PotentialExpense, tx.Classification);
        Assert.Equal(-10m, tx.Amount);
        Assert.Null(tx.LastChangedAt);
        Assert.Equal(0, tx.MissingSyncCount);
    }

    [Fact]
    public void ObservingTheSameDataIsUnchangedAndOnlyRefreshesObservation()
    {
        var tx = PluggyImportedTransaction.Create(Workspace, Connection, Bank(), Run1, Now);
        var later = Now.AddHours(2);
        var observation = tx.Observe(Bank(), Run2, later);

        Assert.False(observation.Changed);
        Assert.Empty(observation.ChangedFields);
        Assert.Equal((Run2, later), (tx.LastSeenRunId, tx.LastObservedAt));
        Assert.Null(tx.LastChangedAt);
        Assert.Null(tx.LastChangedRunId);
    }

    [Fact]
    public void ChangesAreDetectedPerFieldAndKeepPreviousStatusAndAmount()
    {
        var tx = PluggyImportedTransaction.Create(Workspace, Connection, Bank(status: PluggyTransactionStatus.Pending), Run1, Now);
        var later = Now.AddHours(2);
        var observation = tx.Observe(Bank(status: PluggyTransactionStatus.Posted, amount: -10.5m, description: "MERCADO LTDA"), Run2, later);

        Assert.True(observation.Changed);
        Assert.Equal(new[] { "amount", "description", "descriptionRaw", "status" }, observation.ChangedFields.Order());
        Assert.Equal((PluggyTransactionStatus.Pending, -10m), (observation.PreviousStatus, observation.PreviousAmount));
        Assert.Equal((PluggyTransactionStatus.Posted, -10.5m, later, Run2), (tx.Status, tx.Amount, tx.LastChangedAt, tx.LastChangedRunId));
        // Identidade preservada.
        Assert.Equal((Run1, "tx-1"), (tx.FirstSeenRunId, tx.PluggyTransactionId));
        var change = PluggyTransactionChange.Create(tx, observation, Run2, later);
        Assert.Equal((PluggyTransactionStatus.Pending, PluggyTransactionStatus.Posted, -10m, -10.5m), (change.PreviousStatus, change.NewStatus, change.PreviousAmount, change.NewAmount));
    }

    [Fact]
    public void MissingIsCountedWithoutChangingDataAndResetsWhenTheRecordReturns()
    {
        var tx = PluggyImportedTransaction.Create(Workspace, Connection, Bank(), Run1, Now);
        tx.MarkMissing(Now.AddDays(1));
        tx.MarkMissing(Now.AddDays(2));
        Assert.Equal((2, Now.AddDays(1)), (tx.MissingSyncCount, tx.FirstMissingAt));
        Assert.Equal(-10m, tx.Amount);

        tx.Observe(Bank(), Run2, Now.AddDays(3));
        Assert.Equal(0, tx.MissingSyncCount);
        Assert.Null(tx.FirstMissingAt);
    }

    [Fact]
    public void PossibleDuplicateKeyIgnoresIdAndWhitespaceAndCaseButNotAccountDayOrAmount()
    {
        var a = Bank(description: "Mercado   Central");
        var b = a with { PluggyTransactionId = "tx-other", DescriptionRaw = "MERCADO CENTRAL", Description = "x" };
        Assert.Equal(PluggyImportedTransaction.DuplicateKey(a), PluggyImportedTransaction.DuplicateKey(b));
        Assert.NotEqual(PluggyImportedTransaction.DuplicateKey(a), PluggyImportedTransaction.DuplicateKey(a with { PluggyAccountId = "acc-9" }));
        Assert.NotEqual(PluggyImportedTransaction.DuplicateKey(a), PluggyImportedTransaction.DuplicateKey(a with { Amount = -10.01m }));
        Assert.NotEqual(PluggyImportedTransaction.DuplicateKey(a), PluggyImportedTransaction.DuplicateKey(a with { Date = a.Date!.Value.AddDays(1) }));
        Assert.Null(PluggyImportedTransaction.DuplicateKey(a with { Amount = null }));
    }

    [Fact]
    public void RunTracksTotalsErrorsAndFinalStatus()
    {
        var run = PluggySyncRun.Start(Workspace, Connection, Guid.NewGuid(), Now);
        Assert.Equal(PluggySyncStatus.Running, run.Status);
        run.SetAccounts(2);
        run.AddAccount(10, 4, 3, 3, 1);
        run.AddAccount(5, 5, 0, 0, 0);
        Assert.Equal((2, 15, 9, 3, 3, 1), (run.AccountsSynced, run.Seen, run.Created, run.Updated, run.Unchanged, run.Missing));
        run.AddError("RateLimited:transactions");
        run.Finish(Now.AddMinutes(1), failed: false);
        Assert.Equal((PluggySyncStatus.CompletedWithErrors, 1), (run.Status, run.Errors));
        Assert.NotNull(run.FinishedAt);

        var clean = PluggySyncRun.Start(Workspace, Connection, Guid.NewGuid(), Now);
        clean.Finish(Now, failed: false);
        Assert.Equal(PluggySyncStatus.Completed, clean.Status);
        var failed = PluggySyncRun.Start(Workspace, Connection, Guid.NewGuid(), Now);
        failed.Finish(Now, failed: true);
        Assert.Equal(PluggySyncStatus.Failed, failed.Status);
    }

    [Fact]
    public void SnapshotCarriesNoPayerReceiverOrDocumentFields()
    {
        var properties = typeof(PluggyTransactionSnapshot).GetProperties().Select(p => p.Name.ToLowerInvariant()).ToArray();
        Assert.DoesNotContain(properties, p => p.Contains("payer") || p.Contains("receiver") || p.Contains("document") || p.Contains("cnpj") || p.Contains("cpf") || p.Contains("token"));
        var entity = typeof(PluggyImportedTransaction).GetProperties().Select(p => p.Name.ToLowerInvariant()).ToArray();
        Assert.DoesNotContain(entity, p => p.Contains("payer") || p.Contains("receiver") || p.Contains("document") || p.Contains("token") || p.Contains("secret") || p.Contains("payload"));
    }
}
