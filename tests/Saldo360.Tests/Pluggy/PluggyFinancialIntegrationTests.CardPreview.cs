using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Saldo360.Tests.Pluggy;

/// <summary>PROJ-02: a prévia do cartão consulta a Pluggy (simulada) e nunca grava vínculo, cartão, compra, fatura ou lançamento.</summary>
public sealed partial class PluggyFinancialIntegrationTests
{
    private string Preview(Ctx c, string account) => $"{Integration(c.Workspace)}/connections/{c.Connection}/accounts/{account}/card-preview";

    [Fact]
    public async Task TheCardPreviewShowsLimitBillsAndUpcomingChargesWithoutWritingAnything()
    {
        var c = await ArrangeAsync("proj02-preview");
        SeedCard(Tx("c3", CardA, -20, 100m, "PENDING", "PAGAMENTO", "SOFA 03/04", card: new(3, 4, null, null, null, "2222", null, null), type: "DEBIT", category: null));
        await using var before = Db();
        var counts = async () => (await before.PluggyAccountLinks.CountAsync(), await before.FinancialCreditCards.CountAsync(), await before.FinancialCreditCardPurchases.CountAsync(), await before.FinancialCreditCardInvoices.CountAsync(),
            await before.FinancialTransactions.CountAsync(), await before.PluggyPostings.CountAsync(), await before.PluggyCardBills.CountAsync());
        var snapshot = await counts();

        var response = await GetAsync(c, Preview(c, CardA));
        var text = await response.Content.ReadAsStringAsync();
        var preview = JsonDocument.Parse(text).RootElement;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(("5000.00", 1, 4, 2), (preview.GetProperty("creditLimit").GetString(), preview.GetProperty("billsRead").GetInt32(), preview.GetProperty("transactionsRead").GetInt32(), preview.GetProperty("pending").GetInt32()));
        Assert.Equal("230.00", preview.GetProperty("lastBillTotal").GetString());
        Assert.Contains("AwaitingBankBill", preview.GetProperty("flags").EnumerateArray().Select(f => f.GetString()));
        Assert.DoesNotContain("SOFA", text);
        Assert.DoesNotContain("5555444433332222", text);
        Assert.Equal(snapshot, await counts());
    }

    [Fact]
    public async Task TheCardPreviewRejectsBankAccountsUnknownAccountsAndOtherWorkspaces()
    {
        var c = await ArrangeAsync("proj02-preview-guards");
        Assert.Equal(HttpStatusCode.BadRequest, (await GetAsync(c, Preview(c, BankA))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(c, Preview(c, "unknown-account"))).StatusCode);
        var outsider = await SignInAsync("proj02-preview-outsider");
        var outsiderWorkspace = await CreateWorkspaceAsync(outsider);
        var other = c with { Session = outsider, Workspace = outsiderWorkspace.Id };
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(other, Preview(other, CardA))).StatusCode);
    }
}
