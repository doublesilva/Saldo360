using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Saldo360.Domain.Common;

namespace Saldo360.Domain.Pluggy;

public enum PluggyTransactionStatus { Pending, Posted, Unknown }

/// <summary>Classificação puramente informativa: nenhuma classe gera lançamento, previsão ou saldo.</summary>
public enum PluggyTransactionClass { InsufficientInformation, PotentialIncome, PotentialExpense, PotentialTransfer, CardPurchase, CardInstallment }

public enum PluggySyncStatus { Running, Completed, CompletedWithErrors, Failed }

/// <summary>Retrato normalizado de uma transação como a Pluggy a devolveu numa coleta. Não carrega pagador, recebedor nem documentos.</summary>
public sealed record PluggyTransactionSnapshot(string PluggyTransactionId, string PluggyAccountId, string? AccountType, string? AccountLabel,
    DateTimeOffset? Date, decimal? Amount, string? Currency, string? Description, string? DescriptionRaw, string? Type, PluggyTransactionStatus Status,
    string? OperationType, string? Category, int? InstallmentNumber, int? TotalInstallments, string? BillId, string? CardLast4, string? PaymentMethod);

public sealed record PluggyClassification(PluggyTransactionClass Class, string Reason);

/// <summary>
/// Regras de classificação inicial. Usam apenas campos estruturados do banco (tipo, operação, metadados de cartão);
/// o texto da descrição só entra na dica "possível pagamento de fatura" no lado da conta, nunca para ligar parcelas.
/// </summary>
public static partial class PluggyTransactionClassifier
{
    public static PluggyClassification Classify(PluggyTransactionSnapshot t)
    {
        var isCredit = string.Equals(t.Type, "CREDIT", StringComparison.OrdinalIgnoreCase);
        var isDebit = string.Equals(t.Type, "DEBIT", StringComparison.OrdinalIgnoreCase);
        if (t.Amount is null or 0m || (!isCredit && !isDebit)) return new(PluggyTransactionClass.InsufficientInformation, "MissingTypeOrAmount");
        var operation = PluggyOperationCodes.Normalize(t.OperationType) is { Length: > 0 } normalized ? normalized : null;

        if (string.Equals(t.AccountType, "CREDIT", StringComparison.OrdinalIgnoreCase)) return ClassifyCard(t, operation, isCredit);

        // Conta bancária.
        switch (operation)
        {
            case "TRANSFERENCIA_MESMA_INSTITUICAO": return new(PluggyTransactionClass.PotentialTransfer, "SameInstitutionTransfer");
            case "RESGATE_APLIC_FINANCEIRA": return new(PluggyTransactionClass.PotentialTransfer, "InvestmentRedemption");
            case "SAQUE": return new(PluggyTransactionClass.PotentialTransfer, "CashWithdrawal");
            case "OPERACAO_CREDITO": return new(PluggyTransactionClass.InsufficientInformation, "CreditOperation");
            case "RENDIMENTO_APLIC_FINANCEIRA": return isCredit ? new(PluggyTransactionClass.PotentialIncome, "InvestmentYield") : new(PluggyTransactionClass.PotentialExpense, "InvestmentCharge");
        }
        // Dica fraca: só quando a própria descrição cita pagamento de fatura. Nunca vira despesa de consumo.
        if (isDebit && PossibleBillPayment().IsMatch(t.DescriptionRaw ?? t.Description ?? string.Empty))
            return new(PluggyTransactionClass.PotentialTransfer, "PossibleCardBillPayment");
        return isCredit ? new(PluggyTransactionClass.PotentialIncome, operation is null ? "BankCredit" : "BankCredit:" + operation)
                        : new(PluggyTransactionClass.PotentialExpense, operation is null ? "BankDebit" : "BankDebit:" + operation);
    }

    private static PluggyClassification ClassifyCard(PluggyTransactionSnapshot t, string? operation, bool isCredit)
    {
        // Pagamento da própria fatura move dinheiro entre conta e cartão; não é consumo.
        if (operation is "PAGAMENTO_FATURA") return new(PluggyTransactionClass.PotentialTransfer, "CardBillPayment");
        if (isCredit) return new(PluggyTransactionClass.InsufficientInformation, operation is "ESTORNO" ? "CardRefund" : "CardCredit");
        if (operation is "TARIFA") return new(PluggyTransactionClass.PotentialExpense, "CardFee");
        if (operation is "OPERACOES_CREDITO_CONTRATADAS_CARTAO") return new(PluggyTransactionClass.InsufficientInformation, "CardCreditOperation");
        // Parcela só quando os metadados estruturados dizem; a descrição nunca é usada para ligar parcelas.
        if (t.TotalInstallments is > 1) return new(PluggyTransactionClass.CardInstallment, "InstallmentMetadata");
        if (operation is "PAGAMENTO") return new(PluggyTransactionClass.CardPurchase, "CardPurchase");
        return new(PluggyTransactionClass.InsufficientInformation, "UnclassifiedCardOperation");
    }

    [GeneratedRegex(@"\b(PAG(AMENTO|TO)?|PGTO)\s+(DE\s+|DA\s+)?FATURA\b|\bFATURA\s+(DO\s+)?CART", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PossibleBillPayment();
}

/// <summary>Resultado de comparar uma observação com o que já estava guardado.</summary>
public sealed record PluggyObservation(bool Changed, IReadOnlyList<string> ChangedFields, PluggyTransactionStatus PreviousStatus, decimal? PreviousAmount);

/// <summary>
/// Transação bancária observada na Pluggy e guardada à parte do núcleo financeiro. Identidade = conexão + conta + ID externo.
/// Nunca é removida por ausência numa coleta; só acumula <see cref="MissingSyncCount"/>.
/// </summary>
public sealed class PluggyImportedTransaction : Entity
{
    public const int TextMaxLength = 500;
    private PluggyImportedTransaction() { PluggyAccountId = PluggyTransactionId = ClassificationReason = string.Empty; }

    public Guid WorkspaceId { get; private init; }
    public Guid ConnectionId { get; private init; }
    public string PluggyAccountId { get; private init; }
    public string PluggyTransactionId { get; private init; }
    public string? AccountType { get; private set; }
    public string? AccountLabel { get; private set; }
    public DateTimeOffset? Date { get; private set; }
    public decimal? Amount { get; private set; }
    public string? Currency { get; private set; }
    public string? Description { get; private set; }
    public string? DescriptionRaw { get; private set; }
    public string? Type { get; private set; }
    public PluggyTransactionStatus Status { get; private set; }
    public string? OperationType { get; private set; }
    public string? Category { get; private set; }
    public int? InstallmentNumber { get; private set; }
    public int? TotalInstallments { get; private set; }
    public string? BillId { get; private set; }
    public string? CardLast4 { get; private set; }
    public string? PaymentMethod { get; private set; }
    public PluggyTransactionClass Classification { get; private set; }
    public string ClassificationReason { get; private set; }
    /// <summary>Hash de conta + dia + valor + descrição original normalizada. Chave igual em IDs diferentes indica possível duplicidade (ou repetição legítima).</summary>
    public string? PossibleDuplicateKey { get; private set; }
    public DateTimeOffset FirstImportedAt { get; private init; }
    public Guid FirstSeenRunId { get; private init; }
    public DateTimeOffset LastObservedAt { get; private set; }
    public Guid LastSeenRunId { get; private set; }
    public DateTimeOffset? LastChangedAt { get; private set; }
    public Guid? LastChangedRunId { get; private set; }
    public int MissingSyncCount { get; private set; }
    public DateTimeOffset? FirstMissingAt { get; private set; }

    public static PluggyImportedTransaction Create(Guid workspaceId, Guid connectionId, PluggyTransactionSnapshot s, Guid runId, DateTimeOffset now)
    {
        if (workspaceId == Guid.Empty || connectionId == Guid.Empty || runId == Guid.Empty) throw new ArgumentException("Orçamento, conexão ou coleta inválidos.");
        if (string.IsNullOrWhiteSpace(s.PluggyTransactionId) || string.IsNullOrWhiteSpace(s.PluggyAccountId)) throw new ArgumentException("Identificadores externos obrigatórios.");
        var entity = new PluggyImportedTransaction
        {
            WorkspaceId = workspaceId, ConnectionId = connectionId, PluggyAccountId = s.PluggyAccountId, PluggyTransactionId = s.PluggyTransactionId,
            FirstImportedAt = now, FirstSeenRunId = runId, LastObservedAt = now, LastSeenRunId = runId,
        };
        entity.Apply(s);
        return entity;
    }

    /// <summary>Registra uma nova observação. Mudança de dados do banco atualiza a linha; reclassificação sozinha não conta como mudança.</summary>
    public PluggyObservation Observe(PluggyTransactionSnapshot s, Guid runId, DateTimeOffset now)
    {
        var previousStatus = Status;
        var previousAmount = Amount;
        var changed = Diff(s);
        if (changed.Count > 0) { Apply(s); LastChangedAt = now; LastChangedRunId = runId; }
        else Reclassify(s);
        LastObservedAt = now;
        LastSeenRunId = runId;
        MissingSyncCount = 0;
        FirstMissingAt = null;
        return new(changed.Count > 0, changed, previousStatus, previousAmount);
    }

    public IReadOnlyList<string> Diff(PluggyTransactionSnapshot s)
    {
        var fields = new List<string>();
        void Check<T>(string name, T current, T next) { if (!EqualityComparer<T>.Default.Equals(current, next)) fields.Add(name); }
        Check("date", Date, s.Date);
        Check("amount", Amount, s.Amount);
        Check("currency", Currency, Clip(s.Currency, 8));
        Check("description", Description, Clip(s.Description, TextMaxLength));
        Check("descriptionRaw", DescriptionRaw, Clip(s.DescriptionRaw, TextMaxLength));
        Check("type", Type, Clip(s.Type, 16));
        Check("status", Status, s.Status);
        Check("operationType", OperationType, Clip(s.OperationType, 80));
        Check("category", Category, Clip(s.Category, 120));
        Check("installmentNumber", InstallmentNumber, s.InstallmentNumber);
        Check("totalInstallments", TotalInstallments, s.TotalInstallments);
        Check("billId", BillId, Clip(s.BillId, 80));
        Check("cardLast4", CardLast4, Clip(s.CardLast4, 4));
        Check("paymentMethod", PaymentMethod, Clip(s.PaymentMethod, 40));
        return fields;
    }

    /// <summary>Ausente de uma coleta completa da conta. Não apaga nem altera dados; só conta a ausência.</summary>
    public void MarkMissing(DateTimeOffset now)
    {
        MissingSyncCount++;
        FirstMissingAt ??= now;
    }

    private void Apply(PluggyTransactionSnapshot s)
    {
        AccountType = Clip(s.AccountType, 16);
        AccountLabel = Clip(s.AccountLabel, 160);
        Date = s.Date;
        Amount = s.Amount;
        Currency = Clip(s.Currency, 8);
        Description = Clip(s.Description, TextMaxLength);
        DescriptionRaw = Clip(s.DescriptionRaw, TextMaxLength);
        Type = Clip(s.Type, 16);
        Status = s.Status;
        OperationType = Clip(s.OperationType, 80);
        Category = Clip(s.Category, 120);
        InstallmentNumber = s.InstallmentNumber;
        TotalInstallments = s.TotalInstallments;
        BillId = Clip(s.BillId, 80);
        CardLast4 = Clip(s.CardLast4, 4);
        PaymentMethod = Clip(s.PaymentMethod, 40);
        Reclassify(s);
    }

    private void Reclassify(PluggyTransactionSnapshot s)
    {
        AccountType = Clip(s.AccountType, 16);
        AccountLabel = Clip(s.AccountLabel, 160);
        var classification = PluggyTransactionClassifier.Classify(s);
        Classification = classification.Class;
        ClassificationReason = classification.Reason;
        PossibleDuplicateKey = DuplicateKey(s);
    }

    public static string? DuplicateKey(PluggyTransactionSnapshot s)
    {
        if (s.Date is null || s.Amount is null) return null;
        var description = Regex.Replace((s.DescriptionRaw ?? s.Description ?? string.Empty).Trim().ToUpperInvariant(), @"\s+", " ");
        var text = $"{s.PluggyAccountId}|{s.Date.Value.UtcDateTime:yyyy-MM-dd}|{s.Amount.Value:0.0000}|{description}";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    private static string? Clip(string? value, int max) => string.IsNullOrWhiteSpace(value) ? null : value.Length > max ? value[..max] : value;
}

/// <summary>Histórico mínimo de alterações: quais campos mudaram e, para status e valor, o antes e o depois.</summary>
public sealed class PluggyTransactionChange : Entity
{
    private PluggyTransactionChange() { ChangedFields = string.Empty; }
    public Guid WorkspaceId { get; private init; }
    public Guid TransactionId { get; private init; }
    public Guid SyncRunId { get; private init; }
    public DateTimeOffset ObservedAt { get; private init; }
    public string ChangedFields { get; private init; }
    public PluggyTransactionStatus PreviousStatus { get; private init; }
    public PluggyTransactionStatus NewStatus { get; private init; }
    public decimal? PreviousAmount { get; private init; }
    public decimal? NewAmount { get; private init; }

    public static PluggyTransactionChange Create(PluggyImportedTransaction transaction, PluggyObservation observation, Guid runId, DateTimeOffset now) => new()
    {
        WorkspaceId = transaction.WorkspaceId, TransactionId = transaction.Id, SyncRunId = runId, ObservedAt = now,
        ChangedFields = string.Join(',', observation.ChangedFields), PreviousStatus = observation.PreviousStatus, NewStatus = transaction.Status,
        PreviousAmount = observation.PreviousAmount, NewAmount = transaction.Amount,
    };
}

/// <summary>Uma execução de sincronização (manual ou agendada). No máximo uma <see cref="PluggySyncStatus.Running"/> por conexão.</summary>
public sealed class PluggySyncRun : Entity
{
    public const int ErrorSummaryMaxLength = 300;
    private PluggySyncRun() { }
    public Guid WorkspaceId { get; private init; }
    public Guid ConnectionId { get; private init; }
    public Guid UserId { get; private init; }
    public DateTimeOffset StartedAt { get; private init; }
    public DateTimeOffset? FinishedAt { get; private set; }
    public PluggySyncStatus Status { get; private set; }
    public int AccountsTotal { get; private set; }
    public int AccountsSynced { get; private set; }
    public int Seen { get; private set; }
    public int Created { get; private set; }
    public int Updated { get; private set; }
    public int Unchanged { get; private set; }
    public int Missing { get; private set; }
    public int Errors { get; private set; }
    public string? ErrorSummary { get; private set; }
    /// <summary>Manual (usuário) ou Scheduled (agendador). Execuções anteriores à T09.6 são manuais.</summary>
    public PluggySyncTrigger Trigger { get; private init; }

    public static PluggySyncRun Start(Guid workspaceId, Guid connectionId, Guid userId, DateTimeOffset now, PluggySyncTrigger trigger = PluggySyncTrigger.Manual) =>
        new() { WorkspaceId = workspaceId, ConnectionId = connectionId, UserId = userId, StartedAt = now, Status = PluggySyncStatus.Running, Trigger = trigger };

    public void SetAccounts(int total) => AccountsTotal = total;

    public void AddAccount(int seen, int created, int updated, int unchanged, int missing)
    {
        AccountsSynced++;
        Seen += seen; Created += created; Updated += updated; Unchanged += unchanged; Missing += missing;
    }

    public void AddError(string summary)
    {
        Errors++;
        var next = string.IsNullOrEmpty(ErrorSummary) ? summary : ErrorSummary + "; " + summary;
        ErrorSummary = next.Length > ErrorSummaryMaxLength ? next[..ErrorSummaryMaxLength] : next;
    }

    public void Finish(DateTimeOffset now, bool failed)
    {
        FinishedAt = now;
        Status = failed ? PluggySyncStatus.Failed : Errors > 0 ? PluggySyncStatus.CompletedWithErrors : PluggySyncStatus.Completed;
    }
}
