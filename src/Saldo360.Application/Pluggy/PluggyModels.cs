namespace Saldo360.Application.Pluggy;

/// <summary>Configuração local da POC. ClientId/ClientSecret vêm de user-secrets ou variáveis de ambiente.</summary>
public sealed class PluggyOptions
{
    public const string SectionName = "Pluggy";
    public bool Enabled { get; set; }
    public string BaseUrl { get; set; } = "https://api.pluggy.ai";
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
    public int TimeoutSeconds { get; set; } = 30;
    /// <summary>A API Key da Pluggy dura 2 horas; renova-se com esta folga.</summary>
    public int ApiKeyLifetimeMinutes { get; set; } = 110;
    public int MaxTransactionPages { get; set; } = 20;
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);
}

public enum PluggyFailureKind { NotConfigured, Authentication, Forbidden, NotFound, RateLimited, Timeout, Unavailable, InvalidResponse, Rejected }

/// <summary>Falha de integração com mensagem controlada: nunca carrega corpo de resposta, URL, token ou segredo.</summary>
public sealed class PluggyException(PluggyFailureKind kind, int? statusCode = null) : Exception(Describe(kind))
{
    public PluggyFailureKind Kind { get; } = kind;
    public int? StatusCode { get; } = statusCode;
    public static string Describe(PluggyFailureKind kind) => kind switch
    {
        PluggyFailureKind.NotConfigured => "A integração Pluggy não está configurada neste ambiente.",
        PluggyFailureKind.Authentication => "A Pluggy recusou as credenciais configuradas.",
        PluggyFailureKind.Forbidden => "A Pluggy negou o acesso ao recurso solicitado.",
        PluggyFailureKind.NotFound => "O recurso não foi encontrado na Pluggy.",
        PluggyFailureKind.RateLimited => "A Pluggy limitou as requisições. Tente novamente em instantes.",
        PluggyFailureKind.Timeout => "A Pluggy não respondeu a tempo.",
        PluggyFailureKind.Unavailable => "A Pluggy está indisponível no momento.",
        PluggyFailureKind.InvalidResponse => "A Pluggy retornou uma resposta inesperada.",
        _ => "A Pluggy rejeitou a consulta.",
    };
}

public sealed record PluggyItem(string Id, int? ConnectorId, string? ConnectorName, string? ConnectorType, bool? IsOpenFinance,
    string? Status, string? ExecutionStatus, DateTimeOffset? CreatedAt, DateTimeOffset? LastUpdatedAt, DateTimeOffset? ConsentExpiresAt,
    string? ErrorCode, string? ErrorMessage, IReadOnlyList<string> Products);

public sealed record PluggyBankData(string? TransferNumber, decimal? ClosingBalance, decimal? AutomaticallyInvestedBalance,
    decimal? OverdraftContractedLimit, decimal? OverdraftUsedLimit, decimal? UnarrangedOverdraftAmount);

public sealed record PluggyCreditData(string? Level, string? Brand, DateTimeOffset? BalanceCloseDate, DateTimeOffset? BalanceDueDate,
    decimal? AvailableCreditLimit, decimal? CreditLimit, decimal? MinimumPayment, decimal? BalanceForeignCurrency, string? Status, string? HolderType);

/// <summary>Conta como retornada pela Pluggy. <c>Number</c> é bruto e nunca deve sair do backend sem máscara.</summary>
public sealed record PluggyAccount(string Id, string? ItemId, string? Type, string? Subtype, string? Name, string? MarketingName, string? Number,
    decimal? Balance, string? CurrencyCode, DateTimeOffset? UpdatedAt, PluggyBankData? Bank, PluggyCreditData? Credit);

public sealed record PluggyPaymentData(bool HasPayer, bool HasReceiver, string? PaymentMethod, bool HasReferenceNumber, string? Reason);
public sealed record PluggyCardMetadata(int? InstallmentNumber, int? TotalInstallments, decimal? TotalAmount, DateTimeOffset? PurchaseDate,
    string? BillId, string? CardLast4, string? PayeeMcc, DateTimeOffset? BillForecastDate);
public sealed record PluggyMerchant(string? Name, string? BusinessName, bool HasCnpj, string? Category);

public sealed record PluggyTransaction(string Id, string? AccountId, DateTimeOffset? Date, string? Description, string? DescriptionRaw,
    decimal? Amount, decimal? AmountInAccountCurrency, string? CurrencyCode, string? Type, string? Status, string? Category, string? CategoryId,
    decimal? Balance, string? ProviderCode, string? OperationType, PluggyPaymentData? Payment, PluggyCardMetadata? Card, PluggyMerchant? Merchant);

public sealed record PluggyFinanceCharge(string? Type, decimal? Amount, string? CurrencyCode, string? AdditionalInfo);
public sealed record PluggyBillPayment(decimal? Amount, string? CurrencyCode, DateTimeOffset? PaymentDate, string? PaymentMode, string? ValueType);
public sealed record PluggyBill(string Id, DateTimeOffset? DueDate, decimal? TotalAmount, string? TotalAmountCurrencyCode,
    decimal? MinimumPaymentAmount, bool? AllowsInstallments, IReadOnlyList<PluggyFinanceCharge> FinanceCharges,
    DateTimeOffset? ClosingDate, IReadOnlyList<PluggyBillPayment> Payments);

public sealed record PluggyLoan(string Id, string? Kind, string? ProductName, string? Type, string? CurrencyCode, decimal? ContractAmount,
    DateTimeOffset? ContractDate, DateTimeOffset? DueDate, int? TotalInstallments, int? PaidInstallments, int? DueInstallments,
    int? PastDueInstallments, decimal? OutstandingBalance);

public sealed record PluggyInvestment(string Id, string? Type, string? Subtype, string? Name, string? Status, decimal? Balance,
    string? CurrencyCode, DateTimeOffset? Date, decimal? AmountProfit);

public sealed record PluggyPage<T>(IReadOnlyList<T> Items, int? Total, int? TotalPages, int Page);
/// <summary>Página por cursor. <c>NextCursor</c> é o valor opaco a enviar como <c>after</c>.</summary>
public sealed record PluggyCursorPage<T>(IReadOnlyList<T> Items, string? NextCursor);

public interface IPluggyClient
{
    Task<PluggyItem> GetItemAsync(string itemId, CancellationToken ct);
    Task<IReadOnlyList<PluggyAccount>> ListAccountsAsync(string itemId, CancellationToken ct);
    Task<PluggyAccount> GetAccountAsync(string accountId, CancellationToken ct);
    Task<PluggyCursorPage<PluggyTransaction>> ListTransactionsAsync(string accountId, DateOnly? from, DateOnly? to, string? after, CancellationToken ct);
    Task<PluggyPage<PluggyBill>> ListBillsAsync(string accountId, int page, int pageSize, CancellationToken ct);
    Task<IReadOnlyList<PluggyLoan>> ListLoansAsync(string itemId, CancellationToken ct);
    Task<IReadOnlyList<PluggyInvestment>> ListInvestmentsAsync(string itemId, CancellationToken ct);
}
