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

    [Fact]
    public async Task TheCutoffDateIsEchoedWithItsImpactAndAFutureDateIsRejected()
    {
        var c = await ArrangeAsync("proj02-cutoff");
        SeedCard();

        var response = await GetAsync(c, Preview(c, CardA) + "?importFrom=" + D(19));
        var cutoff = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("cutoff");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(D(19), cutoff.GetProperty("importFrom").GetString());
        Assert.Equal(1, cutoff.GetProperty("excludedTransactions").GetInt32()); // só a compra de 20 dias atrás fica antes do corte
        Assert.Equal(HttpStatusCode.BadRequest, (await GetAsync(c, Preview(c, CardA) + "?importFrom=" + Today.AddDays(3).ToString("yyyy-MM-dd"))).StatusCode);
    }

    [Fact]
    public async Task LinkingANewCardRequiresAnActiveBankPayerAccountOfTheSameWorkspace()
    {
        var c = await ArrangeAsync("proj02-payer");
        var other = await SignInAsync("proj02-payer-outsider");
        var otherWorkspace = await CreateWorkspaceAsync(other);
        var foreign = (await JsonOf(await PostAsync(c with { Session = other, Workspace = otherWorkspace.Id }, $"/api/financial-workspaces/{otherWorkspace.Id}/accounts", new { name = "Alheia", type = "Checking", financialScope = "PF" }))).GetProperty("id").GetGuid();
        var inactive = await PayerAsync(c);
        var edit = await Send(c.Client, HttpMethod.Put, $"/api/financial-workspaces/{c.Workspace}/accounts/{inactive}", c.Session, new { name = "Inativa", type = "Checking", financialScope = "PF", isActive = false, openingBalance = 0m, openingBalanceDate = D(60) });
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        Task<HttpResponseMessage> Link(Guid? payer) => PostAsync(c, $"{Integration(c.Workspace)}/links", new { connectionId = c.Connection, pluggyAccountId = CardA, mode = "CreateNew", name = "Cartão", closingDay = 8, dueDay = 15, paymentAccountId = payer });

        foreach (var payer in new Guid?[] { null, Guid.Empty, Guid.NewGuid(), foreign, inactive })
            Assert.Equal(HttpStatusCode.BadRequest, (await Link(payer)).StatusCode);
        await using (var db = Db()) Assert.Equal((0, 0), (await db.PluggyAccountLinks.CountAsync(l => l.CreditCardId != null), await db.FinancialCreditCards.CountAsync())); // nada foi criado

        var created = await Link(await PayerAsync(c));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        await using var check = Db();
        Assert.NotNull((await check.FinancialCreditCards.SingleAsync()).DefaultPaymentAccountId);
    }
}
