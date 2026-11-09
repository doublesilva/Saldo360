using System.Globalization;
using Saldo360.Domain.Common;

namespace Saldo360.Application.Entries;

/// <summary>
/// T13.4: resumo de UM grupo (cartão ou conta) ou do conjunto inteiro, calculado no servidor com <see cref="decimal"/> sobre TODOS os lançamentos filtrados (nunca só os da página), depois da precedência.
/// A soma é das LINHAS exibidas: não é o total oficial de uma fatura (que pode ter componentes não individualizados).
/// <list type="bullet">
/// <item><c>Outflow</c> = despesas ativas (inclui as compras de cartão e o financiamento de fatura, que são saídas/obrigações); <c>Inflow</c> = receitas ativas (inclui estornos e créditos).</item>
/// <item><c>PaymentsOut</c> = pagamentos de fatura e de dívida: caixa, nunca consumo, e fora de <c>Outflow</c> para não contar duas vezes.</item>
/// <item><c>BudgetConsumed</c> − <c>BudgetReduced</c> = <c>BudgetNet</c> (impacto líquido no orçamento); <c>BudgetPlanned</c> = previsões que ainda consumirão.</item>
/// <item><c>InvoiceFinancing</c> = parcelas de financiamento de fatura: obrigação de pagamento, sem novo consumo (impacto no orçamento "None").</item>
/// <item>Transferências são contadas (<c>TransferCount</c>) mas nunca somadas como receita ou despesa. Cancelados são contados à parte e não entram em nenhuma soma.</item>
/// </list>
/// </summary>
public sealed record EntryGroupSummary(
    string Key, string Kind, Guid? Id, string Name, string? Scope, int Count, int ActiveCount, int CancelledCount, int TransferCount,
    string Outflow, string Inflow, string PaymentsOut, string BudgetConsumed, string BudgetReduced, string BudgetNet, string BudgetPlanned, string InvoiceFinancing, int StartsOnPage);

/// <summary>Identidade do grupo de um lançamento.</summary>
public sealed record EntryGroupKey(string Key, string Kind, Guid? Id, string Name, string? Scope, int KindOrder);

public static class FinancialEntryGrouping
{
    public const string ByCard = "card", ByAccount = "account", ByNone = "none";
    public const string UnidentifiedKey = "none", UnidentifiedName = "Sem conta identificada";
    public static readonly string[] Modes = [ByCard, ByAccount, ByNone];

    /// <summary>
    /// Por cartão: movimento com cartão vai para o cartão (pelo ID); movimento bancário vai para a conta; sem vínculo, "Sem conta identificada". Por conta: tudo pela conta (a pagadora, no caso do cartão).
    /// Um lançamento pertence a UM grupo, então nada se repete e uma transferência consolidada aparece uma só vez (na conta de origem).
    /// </summary>
    public static EntryGroupKey? KeyOf(FinancialEntryDto e, string mode)
    {
        if (mode == ByNone) return null;
        if (mode == ByCard && e.Card is { Id: { } cardId }) return new($"card:{cardId}", "Card", cardId, e.Card.Name, e.Account?.Scope ?? e.Card.Scope, 0);
        if (e.Account is { Id: { } accountId }) return new($"account:{accountId}", "Account", accountId, e.Account.Name, e.Account.Scope, 1);
        return new(UnidentifiedKey, "None", null, UnidentifiedName, null, 2);
    }

    /// <summary>Ordem determinística dos grupos: cartões, depois contas (por nome), depois o grupo sem identificação.</summary>
    public static IReadOnlyDictionary<string, int> GroupOrder(IEnumerable<EntryGroupKey> keys) =>
        keys.DistinctBy(k => k.Key).OrderBy(k => k.KindOrder).ThenBy(k => k.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(k => k.Key, StringComparer.Ordinal)
            .Select((k, index) => (k.Key, index)).ToDictionary(x => x.Key, x => x.index);

    /// <summary>Resumo de um conjunto de lançamentos (já filtrados e resolvidos). <paramref name="firstIndex"/> = posição do primeiro item do grupo na lista ordenada, para localizar a página inicial.</summary>
    public static EntryGroupSummary Summarize(EntryGroupKey identity, IReadOnlyList<FinancialEntryDto> entries, int firstIndex, int pageSize)
    {
        decimal outflow = 0, inflow = 0, payments = 0, consumed = 0, reduced = 0, planned = 0, financing = 0;
        int active = 0, cancelled = 0, transfers = 0;
        foreach (var e in entries)
        {
            if (e.Status == "Cancelled") { cancelled++; continue; }
            active++;
            if (e.Type == "Transfer") { transfers++; continue; }
            var amount = decimal.Parse(e.Amount, CultureInfo.InvariantCulture);
            switch (e.Type)
            {
                case "Expense": outflow += amount; break;
                case "Income": inflow += amount; break;
                case "Payment": payments += amount; break;
            }
            if (e.Origin == "InvoiceFinancing") financing += amount;
            if (e.Type == "Payment") continue;   // pagamento de fatura/dívida nunca é consumo nem previsão de consumo
            if (e.BudgetImpact == "Consumes") consumed += amount;
            else if (e.BudgetImpact == "Reduces") reduced += amount;
            else if (e.BudgetImpact == "Planned" && e.Type == "Expense") planned += amount;
        }
        return new(identity.Key, identity.Kind, identity.Id, identity.Name, identity.Scope, entries.Count, active, cancelled, transfers,
            Money.Format(outflow), Money.Format(inflow), Money.Format(payments), Money.Format(consumed), Money.Format(reduced), Money.Format(consumed - reduced), Money.Format(planned), Money.Format(financing),
            pageSize <= 0 ? 1 : firstIndex / pageSize + 1);
    }
}
