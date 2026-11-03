using System.Globalization;

namespace Saldo360.Application.Pluggy;

public sealed record PluggyCardPreviewBill(DateOnly? DueDate, DateOnly? ClosingDate, string Total, string? Minimum, bool IsCredit, bool HasFractionalCents);

/// <summary>Cobranças já conhecidas (pendentes ou com data futura) agrupadas pelo mês da data informada pelo banco. Não é fatura: a fatura só existe quando o banco a fecha.</summary>
public sealed record PluggyCardPreviewMonth(string Month, int Entries, int Installments, string Amount);

public sealed record PluggyCardPreviewView(string AccountName, string? Brand, string? CreditLimit, string? AvailableLimit, string? UsedLimit, int? DueDay,
    int BillsRead, DateOnly? LastBillDue, string? LastBillTotal, int TransactionsRead, int Posted, int Pending, int WithInstallments,
    IReadOnlyList<PluggyCardPreviewBill> Bills, IReadOnlyList<PluggyCardPreviewMonth> Upcoming, IReadOnlyList<string> Flags);

/// <summary>
/// Prévia somente leitura de um cartão da Pluggy antes do vínculo (PROJ-02): resume o que o banco devolve (limite, faturas e cobranças futuras) para que o usuário
/// confira antes de vincular. Função pura: não grava nada, não cria cartão, compra ou fatura e não expõe descrições, identificadores ou número do cartão.
/// </summary>
public static class PluggyCardPreview
{
    public const int BillsShown = 12;

    public static class Flags
    {
        public const string NoBillsReturned = "NoBillsReturned";
        /// <summary>A última fatura devolvida já venceu: a fatura em aberto ainda não existe no banco, então o ciclo atual é só projeção.</summary>
        public const string AwaitingBankBill = "AwaitingBankBill";
        public const string NoClosingDate = "NoClosingDate";
        public const string FractionalBillCents = "FractionalBillCents";
        public const string CreditBill = "CreditBill";
        public const string TransactionsTruncated = "TransactionsTruncated";
    }

    public static PluggyCardPreviewView Build(PluggyAccount account, IReadOnlyList<PluggyBill> bills, IReadOnlyList<PluggyTransaction> transactions, bool transactionsTruncated, DateOnly today)
    {
        var ordered = bills.Where(b => b.DueDate is not null).OrderBy(b => b.DueDate).ToList();
        var shown = ordered.TakeLast(BillsShown).Select(b => new PluggyCardPreviewBill(DateOnly.FromDateTime(b.DueDate!.Value.UtcDateTime), b.ClosingDate is { } c ? DateOnly.FromDateTime(c.UtcDateTime) : null,
            Money(b.TotalAmount) ?? "0.00", Money(b.MinimumPaymentAmount), b.TotalAmount < 0, HasFractionalCents(b.TotalAmount))).ToList();
        var last = shown.Count > 0 ? shown[^1] : null;

        var upcoming = transactions.Where(t => t.Date is not null && t.Amount is > 0 && (t.Status == "PENDING" || DateOnly.FromDateTime(t.Date.Value.UtcDateTime) > today))
            .GroupBy(t => DateOnly.FromDateTime(t.Date!.Value.UtcDateTime).ToString("yyyy-MM", CultureInfo.InvariantCulture)).OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new PluggyCardPreviewMonth(g.Key, g.Count(), g.Count(t => t.Card?.TotalInstallments > 1), Money(g.Sum(t => t.Amount!.Value))!)).ToList();

        var flags = new List<string>();
        if (shown.Count == 0) flags.Add(Flags.NoBillsReturned);
        else if (last!.DueDate < today) flags.Add(Flags.AwaitingBankBill);
        if (shown.Count > 0 && shown.All(b => b.ClosingDate is null)) flags.Add(Flags.NoClosingDate);
        if (shown.Any(b => b.HasFractionalCents)) flags.Add(Flags.FractionalBillCents);
        if (shown.Any(b => b.IsCredit)) flags.Add(Flags.CreditBill);
        if (transactionsTruncated) flags.Add(Flags.TransactionsTruncated);

        var credit = account.Credit;
        var used = credit?.CreditLimit is { } limit && credit.AvailableCreditLimit is { } available ? limit - available : (decimal?)null;
        return new(PluggyAccountLinkService.SuggestedName(account), credit?.Brand, Money(credit?.CreditLimit), Money(credit?.AvailableCreditLimit), Money(used), credit?.BalanceDueDate?.Day,
            ordered.Count, last?.DueDate, last?.Total, transactions.Count, transactions.Count(t => t.Status == "POSTED"), transactions.Count(t => t.Status == "PENDING"),
            transactions.Count(t => t.Card?.TotalInstallments > 1), shown, upcoming, flags);
    }

    /// <summary>Texto decimal exato com duas casas; o valor do banco nunca é arredondado em silêncio: frações de centavo são sinalizadas por <see cref="Flags.FractionalBillCents"/>.</summary>
    private static string? Money(decimal? value) => value is { } v ? decimal.Round(v, 2, MidpointRounding.AwayFromZero).ToString("F2", CultureInfo.InvariantCulture) : null;

    private static bool HasFractionalCents(decimal? value) => value is { } v && decimal.Round(v, 2) != v;
}
