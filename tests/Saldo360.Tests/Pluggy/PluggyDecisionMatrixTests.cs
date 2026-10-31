using Saldo360.Application.Pluggy;
using Saldo360.Domain.Pluggy;

namespace Saldo360.Tests.Pluggy;

/// <summary>Decisao exata do classificador e do mapper para um debito/credito efetivado de conta bancaria, por operationType (Pluggy simulada).</summary>
[Trait("Category", "CORE")]
[Trait("Area", "Pluggy")]
public sealed class PluggyDecisionMatrixTests
{
    private static (PluggyTransactionClass Class, string ClassReason, PluggyDisposition Disposition, string? Reason) Run(string type, string? operation)
    {
        var date = new DateTimeOffset(2026, 10, 8, 0, 0, 0, TimeSpan.Zero);
        var amount = type == "DEBIT" ? -748.31m : 1960.49m;
        var snapshot = new PluggyTransactionSnapshot("x", "a", "BANK", "Itau", date, amount, "BRL", "Entrada", "ENTRADA", type, PluggyTransactionStatus.Posted, operation, null, null, null, null, null, null);
        var cls = PluggyTransactionClassifier.Classify(snapshot);
        var facts = new ImportedFacts(Guid.NewGuid(), Guid.NewGuid(), PluggyLinkedAccountKind.Bank, null, PluggyTransactionStatus.Posted, type, amount, "BRL", date, "Entrada", "ENTRADA", operation, null, null, null, cls.Class, cls.Reason);
        var movement = PluggyFinancialMapper.Decide(facts, new DateOnly(2026, 10, 31));
        return (cls.Class, cls.Reason, movement.Disposition, movement.Reason);
    }

    [Theory]
    [InlineData("CREDIT", "TRANSFERENCIA_MESMA_INSTITUICAO", PluggyTransactionClass.PotentialTransfer, PluggyDisposition.TransferCandidate)] // sem par: revisao TransferWithoutCounterpart
    [InlineData("CREDIT", "RESGATE_APLIC_FINANCEIRA", PluggyTransactionClass.PotentialTransfer, PluggyDisposition.Review)]
    [InlineData("CREDIT", "SAQUE", PluggyTransactionClass.PotentialTransfer, PluggyDisposition.Review)]
    [InlineData("CREDIT", "PIX", PluggyTransactionClass.PotentialIncome, PluggyDisposition.TransferProbe)] // sem par vira receita
    [InlineData("CREDIT", "TED", PluggyTransactionClass.PotentialIncome, PluggyDisposition.TransferProbe)]
    [InlineData("CREDIT", null, PluggyTransactionClass.PotentialIncome, PluggyDisposition.BankMovement)]
    [InlineData("DEBIT", "OPERACAO_CREDITO", PluggyTransactionClass.InsufficientInformation, PluggyDisposition.Review)]
    [InlineData("DEBIT", null, PluggyTransactionClass.PotentialExpense, PluggyDisposition.BankMovement)]
    [InlineData("DEBIT", "BOLETO", PluggyTransactionClass.PotentialExpense, PluggyDisposition.BankMovement)]
    [InlineData("UNKNOWN", "PIX", PluggyTransactionClass.InsufficientInformation, PluggyDisposition.Review)] // sem tipo: MissingType; so "Nao contabilizar" e possivel
    public void EachOperationTypeGetsAnExactDecision(string type, string? operation, PluggyTransactionClass expectedClass, PluggyDisposition expected)
    {
        var result = Run(type, operation);
        Assert.Equal((expectedClass, expected), (result.Class, result.Disposition));
    }
}
