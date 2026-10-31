using System.Globalization;
using System.Text;
using Saldo360.Domain.Accounts;
using Saldo360.Domain.Categories;
using Saldo360.Domain.Pluggy;

namespace Saldo360.Application.Pluggy;

/// <summary>Fatos de uma transação importada que interessam à decisão financeira. Não depende do contrato da Pluggy.</summary>
public sealed record ImportedFacts(
    Guid Id, Guid LinkId, PluggyLinkedAccountKind LinkKind, DateOnly? ImportFrom, PluggyTransactionStatus Status, string? Type, decimal? Amount,
    string? Currency, DateTimeOffset? Date, string? Description, string? DescriptionRaw, string? OperationType, string? Category,
    int? InstallmentNumber, int? TotalInstallments, PluggyTransactionClass Class, string ClassReason)
{
    public static ImportedFacts From(PluggyImportedTransaction t, PluggyAccountLink link) => new(
        t.Id, link.Id, link.Kind, link.ImportFrom, t.Status, t.Type, t.Amount, t.Currency, t.Date, t.Description, t.DescriptionRaw, t.OperationType,
        t.Category, t.InstallmentNumber, t.TotalInstallments, t.Classification, t.ClassificationReason);
}

public enum PluggyDisposition
{
    /// <summary>Anterior à data de início da integração da conta: não alimenta o realizado.</summary>
    OutOfRange,
    /// <summary>PENDING (ou data futura): ainda não é movimento realizado.</summary>
    Waiting,
    /// <summary>Receita ou despesa bancária.</summary>
    BankMovement,
    /// <summary>Débito bancário que cita pagamento de fatura: só vira pagamento se a fatura do cartão confirmar; senão vai para revisão.</summary>
    BankDebitBillHint,
    /// <summary>Transferência entre contas da mesma instituição: só vira transferência se a outra ponta for uma conta integrada.</summary>
    TransferCandidate,
    /// <summary>
    /// PIX, TED ou DOC de conta bancária: na maioria das vezes é receita/despesa com terceiros, mas vira transferência quando existe a ponta
    /// oposta numa conta integrada (valor exato, mesma operação, sentidos opostos, correspondência única). Sem par, segue como movimento.
    /// </summary>
    TransferProbe,
    /// <summary>Compra ou parcela de cartão.</summary>
    CardPurchase,
    /// <summary>Crédito no cartão que paga a fatura: evidência do pagamento, nunca receita.</summary>
    CardBillPayment,
    /// <summary>
    /// Crédito no cartão com evidência estruturada de ser estorno, devolução ou cashback: vira crédito da fatura onde aparece (reduz a obrigação e o
    /// consumo da categoria), nunca receita. Sem evidência, o crédito vai para revisão.
    /// </summary>
    CardCredit,
    /// <summary>Sem classificação segura.</summary>
    Review,
}

/// <summary>Decisão normalizada sobre uma transação importada, antes de qualquer correspondência entre contas.</summary>
public sealed record PluggyMovement(PluggyDisposition Disposition, string? Reason, CategoryType? Direction, decimal Amount, DateOnly Date, string Description)
{
    public bool IsDebit => Direction == CategoryType.Expense;
}

/// <summary>
/// Camada de transformação Pluggy → Saldo360: converte os fatos importados em decisões do modelo financeiro interno. É pura (sem acesso
/// a banco ou rede). Usa campos estruturados (tipo, operação, metadados de cartão); a descrição serve apenas de dica fraca e nunca
/// sozinha para decidir transferência ou pagamento de fatura. O que não puder ser interpretado com segurança vira revisão.
/// </summary>
public static class PluggyFinancialMapper
{
    public const int DescriptionMaxLength = 200;
    public const string FallbackDescription = "Movimentação importada";

    public static PluggyLinkedAccountKind? KindOf(string? pluggyType) => pluggyType?.ToUpperInvariant() switch
    {
        "BANK" => PluggyLinkedAccountKind.Bank,
        "CREDIT" => PluggyLinkedAccountKind.Card,
        _ => null,
    };

    /// <summary>Tipo de conta do Saldo360 para o subtipo da Pluggy. <c>Recognized</c> falso = subtipo desconhecido (Corrente é o padrão e o usuário confirma).</summary>
    public static (AccountType Type, bool Recognized) AccountTypeOf(string? subtype) => subtype?.ToUpperInvariant() switch
    {
        "CHECKING_ACCOUNT" => (AccountType.Checking, true),
        "SAVINGS_ACCOUNT" => (AccountType.Savings, true),
        _ => (AccountType.Checking, false),
    };

    /// <summary>
    /// Dia bancário de uma data da Pluggy. A Pluggy entrega a data do lançamento bancário como instante à meia-noite UTC ("2026-10-09T00:00:00.000Z"
    /// nos contratos e fixtures observados): o dia civil já está na parte UTC, então converter para America/Sao_Paulo deslocaria o movimento para a
    /// véspera (00:00Z = 21:00 do dia anterior). Por isso a data UTC é a data bancária. Datas com horário real não foram observadas; se aparecerem,
    /// esta regra deve ser revista com evidência (dado real) antes de qualquer conversão.
    /// </summary>
    public static DateOnly BusinessDate(DateTimeOffset date) => DateOnly.FromDateTime(date.UtcDateTime);

    /// <summary>Evidência fraca de devolução na descrição (nunca decide sozinha: só leva o crédito à revisão).</summary>
    public static bool DescribesRefund(string? description, string? raw) =>
        RefundHint(Normalize(description)) || RefundHint(Normalize(raw));

    private static bool RefundHint(string normalized) =>
        normalized.Length > 0 && RefundPattern.IsMatch(normalized);

    private static readonly System.Text.RegularExpressions.Regex RefundPattern = new(@"(?<![a-z])(devolvid[oa]s?|devolucao|estorn\w*|reembolso)(?![a-z])",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.Compiled);

    public static string Describe(string? description, string? raw)
    {
        var text = !string.IsNullOrWhiteSpace(description) ? description : raw;
        text = string.IsNullOrWhiteSpace(text) ? FallbackDescription : string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return text.Length > DescriptionMaxLength ? text[..DescriptionMaxLength].TrimEnd() : text;
    }

    /// <summary>Descrição da compra de cartão; parcelas da Pluggy ganham o sufixo estruturado "(k/N)" para o usuário reconhecer a fatura.</summary>
    public static string DescribeCardPurchase(ImportedFacts f)
    {
        var text = Describe(f.Description, f.DescriptionRaw);
        if (f.TotalInstallments is > 1 && f.InstallmentNumber is >= 1 && f.InstallmentNumber <= f.TotalInstallments)
        {
            var suffix = $" ({f.InstallmentNumber}/{f.TotalInstallments})";
            if (text.Length + suffix.Length > DescriptionMaxLength) text = text[..(DescriptionMaxLength - suffix.Length)].TrimEnd();
            text += suffix;
        }
        return text;
    }

    /// <summary>Centavos exatos: valor com mais de duas casas significativas não é arredondado, é recusado (null).</summary>
    public static decimal? ExactCents(decimal amount)
    {
        var rounded = decimal.Round(amount, 2);
        return rounded == amount ? rounded : null;
    }

    public static PluggyMovement Decide(ImportedFacts f, DateOnly today, PluggyResolution? resolution = null)
    {
        var description = f.LinkKind == PluggyLinkedAccountKind.Card ? DescribeCardPurchase(f) : Describe(f.Description, f.DescriptionRaw);
        PluggyMovement Review(string reason, CategoryType? direction = null, decimal amount = 0m, DateOnly date = default) =>
            new(PluggyDisposition.Review, reason, direction, amount, date, description);

        if (f.Date is not { } rawDate) return Review("MissingDate");
        var date = BusinessDate(rawDate);
        var isDebit = string.Equals(f.Type, "DEBIT", StringComparison.OrdinalIgnoreCase);
        var isCredit = string.Equals(f.Type, "CREDIT", StringComparison.OrdinalIgnoreCase);
        if (!isDebit && !isCredit) return Review("MissingType", date: date);
        var direction = isDebit ? CategoryType.Expense : CategoryType.Income;
        if (f.Amount is not { } raw || raw == 0m) return Review("MissingAmount", direction, date: date);
        if (ExactCents(Math.Abs(raw)) is not { } amount) return Review("AmountPrecision", direction, date: date);
        if (f.Currency is { Length: > 0 } currency && !string.Equals(currency, "BRL", StringComparison.OrdinalIgnoreCase)) return Review("ForeignCurrency", direction, amount, date);

        PluggyMovement Result(PluggyDisposition disposition, string? reason = null, CategoryType? dir = null) => new(disposition, reason, dir ?? direction, amount, date, description);

        if (f.ImportFrom is { } start && date < start) return Result(PluggyDisposition.OutOfRange, "BeforeImportFrom");
        if (f.Status == PluggyTransactionStatus.Unknown) return Review("UnknownStatus", direction, amount, date);

        return f.LinkKind == PluggyLinkedAccountKind.Card ? DecideCard(f, resolution, Result) : DecideBank(f, today, resolution, isDebit, isCredit, Result);
    }

    private static readonly HashSet<string> CardChargeCategories = new(StringComparer.Ordinal)
        { "tax on financial operations", "bank fees", "interests charged", "late payment and overdraft costs" };

    private static PluggyMovement DecideCard(ImportedFacts f, PluggyResolution? resolution, Func<PluggyDisposition, string?, CategoryType?, PluggyMovement> result)
    {
        if (f.Status != PluggyTransactionStatus.Posted) return result(PluggyDisposition.Waiting, "Pending", null);
        var isDebit = string.Equals(f.Type, "DEBIT", StringComparison.OrdinalIgnoreCase);
        if (resolution == PluggyResolution.AsCardPurchase && isDebit) return result(PluggyDisposition.CardPurchase, "UserDecision", CategoryType.Expense);
        var category = Saldo360.Application.Classification.FinancialCategoryMatching.Key(f.Category);
        var operation = PluggyOperationCodes.Normalize(f.OperationType);
        var inCatalog = Saldo360.Application.Classification.FinancialExternalCategoryCatalog.Find(f.Category, CategoryType.Expense) is not null;
        return f.Class switch
        {
            PluggyTransactionClass.PotentialTransfer when f.ClassReason == "CardBillPayment" && !isDebit => result(PluggyDisposition.CardBillPayment, f.ClassReason, CategoryType.Income),
            PluggyTransactionClass.CardPurchase or PluggyTransactionClass.CardInstallment or PluggyTransactionClass.PotentialExpense when isDebit => result(PluggyDisposition.CardPurchase, f.ClassReason, CategoryType.Expense),
            // Encargos e tarifas da fatura (IOF, tarifas, juros, multa) são consumo do cartão: rótulo estruturado do banco, nunca descrição.
            PluggyTransactionClass.InsufficientInformation when isDebit && f.ClassReason == "UnclassifiedCardOperation" && CardChargeCategories.Contains(category) =>
                result(PluggyDisposition.CardPurchase, "CardCharge", CategoryType.Expense),
            // Débito de operação genérica, mas com categoria de consumo do banco: é compra comum (transferência e financiamento continuam em revisão).
            PluggyTransactionClass.InsufficientInformation when isDebit && f.ClassReason == "UnclassifiedCardOperation" && inCatalog =>
                result(PluggyDisposition.CardPurchase, "CardPurchaseByCategory", CategoryType.Expense),
            // Estornos, devoluções e cashback: crédito da fatura quando há evidência estruturada; senão revisão (nunca receita comum).
            PluggyTransactionClass.InsufficientInformation when !isDebit && f.ClassReason is "CardRefund" or "CardCredit" => CardCreditDecision(f, category, operation, result),
            // Operações de crédito contratadas e demais casos: sem decisão humana não são consumo nem receita.
            _ => result(PluggyDisposition.Review, "Card:" + f.ClassReason, null),
        };
    }

    private static PluggyMovement CardCreditDecision(ImportedFacts f, string category, string operation, Func<PluggyDisposition, string?, CategoryType?, PluggyMovement> result)
    {
        if (operation == "ESTORNO") return result(PluggyDisposition.CardCredit, "CardRefund", CategoryType.Income);
        if (category == "cashback") return result(PluggyDisposition.CardCredit, "CardCashback", CategoryType.Income);
        // Crédito com a categoria de consumo de uma compra (operação genérica): devolução dessa compra.
        if (Saldo360.Application.Classification.FinancialExternalCategoryCatalog.Find(f.Category, CategoryType.Expense) is not null)
            return result(PluggyDisposition.CardCredit, "CardPurchaseCredit", CategoryType.Income);
        return result(PluggyDisposition.Review, "Card:" + f.ClassReason, null);
    }

    private static PluggyMovement DecideBank(ImportedFacts f, DateOnly today, PluggyResolution? resolution, bool isDebit, bool isCredit,
        Func<PluggyDisposition, string?, CategoryType?, PluggyMovement> result)
    {
        if (f.Status != PluggyTransactionStatus.Posted) return result(PluggyDisposition.Waiting, "Pending", null);
        var date = BusinessDate(f.Date!.Value);
        if (date > today) return result(PluggyDisposition.Waiting, "FutureDated", null);
        if (resolution == PluggyResolution.AsIncome && isCredit) return result(PluggyDisposition.BankMovement, "UserDecision", CategoryType.Income);
        if (resolution == PluggyResolution.AsExpense && isDebit) return result(PluggyDisposition.BankMovement, "UserDecision", CategoryType.Expense);

        var operation = PluggyOperationCodes.Normalize(f.OperationType);
        switch (f.Class)
        {
            case PluggyTransactionClass.PotentialIncome or PluggyTransactionClass.PotentialExpense:
                // Estorno/devolução volta o valor de um gasto anterior: não é receita nova.
                if (isCredit && operation is "ESTORNO" or "DEVOLUCAO") return result(PluggyDisposition.Review, "PossibleRefund", null);
                // Descrição que cita devolução é só uma dica: leva o crédito à revisão (qual gasto foi devolvido é decisão do usuário), nunca o confirma.
                if (isCredit && f.ClassReason != "InvestmentYield" && DescribesRefund(f.Description, f.DescriptionRaw)) return result(PluggyDisposition.Review, "PossibleRefundByDescription", null);
                if (operation is "PIX" or "TED" or "DOC") return result(PluggyDisposition.TransferProbe, f.ClassReason, null);
                return result(PluggyDisposition.BankMovement, f.ClassReason, null);
            case PluggyTransactionClass.PotentialTransfer:
                return f.ClassReason switch
                {
                    "SameInstitutionTransfer" => result(PluggyDisposition.TransferCandidate, f.ClassReason, null),
                    "PossibleCardBillPayment" => result(PluggyDisposition.BankDebitBillHint, f.ClassReason, null),
                    _ => result(PluggyDisposition.Review, "NoCounterpart:" + f.ClassReason, null),
                };
            default:
                return result(PluggyDisposition.Review, f.ClassReason, null);
        }
    }

    /// <summary>Texto ASCII em minúsculas, sem acentos nem espaços sobrando.</summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var decomposed = text.Trim().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) builder.Append(char.ToLowerInvariant(c));
        return string.Join(' ', builder.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }
}

/// <summary>
/// Categoria do Saldo360 existente no orçamento (somente leitura para o mapeamento). Subcategoria traz a principal em <c>ParentId</c>/<c>ParentName</c>;
/// ela só está disponível para classificar algo novo quando ela e a principal estão ativas.
/// </summary>
public sealed record MappableCategory(Guid Id, string Name, CategoryType Type, bool IsActive, Guid? ParentId = null, string? ParentName = null, bool ParentIsActive = true)
{
    public bool IsSubcategory => ParentId is not null;
    public bool IsAvailable => IsActive && (ParentId is null || ParentIsActive);
    /// <summary>"Principal / Sub" para subcategorias; o nome simples para as principais.</summary>
    public string Label => ParentName is null ? Name : $"{ParentName} / {Name}";
}

/// <summary>
/// Mapeia a categoria textual da Pluggy para uma categoria do orçamento quando houver correspondência segura. Delega ao catálogo único
/// (<see cref="Saldo360.Application.Classification.FinancialExternalCategoryCatalog"/>): nome equivalente (sem acento, caixa, pontuação ou espaços) ou
/// apelido conhecido que aponte para uma categoria que o orçamento realmente tem. Não cria categorias; sem correspondência devolve null.
/// </summary>
public static class PluggyCategoryMapper
{
    public static Guid? Map(string? pluggyCategory, CategoryType type, IReadOnlyCollection<MappableCategory> categories) =>
        Saldo360.Application.Classification.FinancialExternalCategoryCatalog.ResolveExisting(pluggyCategory, type, categories);
}

public sealed record PairItem(Guid Id, Guid LinkId, decimal Amount, DateOnly Date, bool Hint);

public sealed record PairingResult(IReadOnlyList<(Guid Source, Guid Target)> Pairs, IReadOnlySet<Guid> Ambiguous);

/// <summary>
/// Correspondência entre duas pontas de um mesmo movimento (transferência entre contas, pagamento de fatura): valor idêntico, datas
/// próximas e pontas em contas distintas. Só confirma quando a correspondência é única nos dois sentidos; qualquer dúvida vira
/// ambiguidade e as pontas envolvidas vão para revisão. Nunca usa descrição.
/// </summary>
public static class PluggyPairing
{
    /// <param name="isRejected">Par (origem, alvo) que o usuário já rejeitou: nunca é candidato.</param>
    public static PairingResult Match(IReadOnlyList<PairItem> sources, IReadOnlyList<PairItem> targets, int toleranceDays, bool differentLinks, Func<Guid, Guid, bool>? isRejected = null)
    {
        bool Compatible(PairItem s, PairItem t) => t.Amount == s.Amount && Math.Abs(t.Date.DayNumber - s.Date.DayNumber) <= toleranceDays
            && (!differentLinks || t.LinkId != s.LinkId) && (isRejected is null || !isRejected(s.Id, t.Id));

        List<PairItem> Candidates(PairItem s, IReadOnlyList<PairItem> pool)
        {
            var list = pool.Where(t => Compatible(s, t)).ToList();
            // Dica (descrição citando fatura) só desempata quando há mais de uma opção; nunca cria correspondência sozinha.
            if (list.Count > 1 && list.Count(t => t.Hint) == 1) list = [list.First(t => t.Hint)];
            return list;
        }

        var forward = sources.ToDictionary(s => s.Id, s => Candidates(s, targets));
        var backward = targets.ToDictionary(t => t.Id, t => sources.Where(s => Compatible(s, t)).ToList());
        var pairs = new List<(Guid, Guid)>();
        var ambiguous = new HashSet<Guid>();
        foreach (var s in sources.OrderBy(x => x.Date).ThenBy(x => x.Id))
        {
            var options = forward[s.Id];
            if (options.Count == 0) continue;
            var t = options[0];
            if (options.Count == 1 && backward[t.Id].Count == 1) { pairs.Add((s.Id, t.Id)); continue; }
            ambiguous.Add(s.Id);
            foreach (var other in options) ambiguous.Add(other.Id);
        }
        foreach (var t in targets)
            if (backward[t.Id].Count > 1) { ambiguous.Add(t.Id); foreach (var s in backward[t.Id]) ambiguous.Add(s.Id); }
        // Uma ponta já confirmada num par não pode ficar marcada como ambígua por outra.
        var paired = pairs.SelectMany(p => new[] { p.Item1, p.Item2 }).ToHashSet();
        ambiguous.ExceptWith(paired);
        return new PairingResult(pairs, ambiguous);
    }

    /// <summary>
    /// Pontas que só teriam contraparte com data diferente (mesmo valor, outra conta, exatamente <paramref name="maxDays"/> dia(s) de distância ou menos, nunca o
    /// mesmo dia). Não é correspondência: uma transferência tem uma única data, então esses casos vão para revisão em vez de receber uma data comum forçada.
    /// </summary>
    public static IReadOnlySet<Guid> DateMismatches(IReadOnlyList<PairItem> sources, IReadOnlyList<PairItem> targets, int maxDays, bool differentLinks, Func<Guid, Guid, bool>? isRejected = null)
    {
        var result = new HashSet<Guid>();
        foreach (var s in sources)
            foreach (var t in targets)
            {
                var distance = Math.Abs(t.Date.DayNumber - s.Date.DayNumber);
                if (distance == 0 || distance > maxDays || t.Amount != s.Amount || (differentLinks && t.LinkId == s.LinkId) || (isRejected is not null && isRejected(s.Id, t.Id))) continue;
                result.Add(s.Id); result.Add(t.Id);
            }
        return result;
    }
}
