using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Saldo360.Tests.Support;

namespace Saldo360.Tests.Api;

[Collection(PostgresSecondaryCollection.Name)]
[Trait("Category", "CORE")]
public sealed class CreditCardCsvPreviewEndpointsTests(PostgresFixture postgres) : PostgresApiTestBase(postgres)
{
    [Fact]
    public async Task PreviewsCsvWithoutCreatingFinancialRecords()
    {
        var session = await SignInAsync("csv-preview-owner");
        var family = await CreateWorkspaceAsync(session, "Família CSV");
        var created = await SendWithCsrfAsync(HttpMethod.Post, $"/api/financial-workspaces/{family.Id}/credit-cards", session,
            new { name = "Cartão", issuer = "Banco", creditLimit = "1000.00", closingDay = 10, dueDay = 20, defaultPaymentAccountId = (Guid?)null });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var cardId = (await ReadAsync<JsonElement>(created)).GetProperty("id").GetGuid();

        var response = await UploadAsync(session, family.Id, cardId, "date;description;amount;external\n20/10/2026;\"Compra; loja\";1.234,56;ext-1\n",
            new Dictionary<string, string> { ["delimiter"] = ";", ["dateFormat"] = "dd/MM/yyyy", ["decimalFormat"] = "PtBr", ["dateColumn"] = "date", ["descriptionColumn"] = "description", ["amountColumn"] = "amount", ["externalIdColumn"] = "external" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var preview = await ReadAsync<JsonElement>(response);
        Assert.Equal(1, preview.GetProperty("totalRows").GetInt32());
        Assert.Equal("1234.56", preview.GetProperty("rows")[0].GetProperty("amount").GetString());
        Assert.Equal("ext-1", preview.GetProperty("rows")[0].GetProperty("externalId").GetString());

        await WithDbAsync(async db =>
        {
            Assert.Empty(db.FinancialCreditCardPurchases);
            Assert.Empty(db.FinancialTransactions);
        });
    }

    [Fact]
    public async Task RejectsArchivedCardAndUnauthenticatedUpload()
    {
        var session = await SignInAsync("csv-preview-archived");
        var family = await CreateWorkspaceAsync(session, "Família CSV arquivado");
        var created = await SendWithCsrfAsync(HttpMethod.Post, $"/api/financial-workspaces/{family.Id}/credit-cards", session,
            new { name = "Cartão", issuer = "Banco", creditLimit = "1000.00", closingDay = 10, dueDay = 20, defaultPaymentAccountId = (Guid?)null });
        var cardId = (await ReadAsync<JsonElement>(created)).GetProperty("id").GetGuid();
        var (csrfCookie, csrfToken) = await GetCsrfAsync(session);
        var archive = await SendAsync(HttpMethod.Delete, $"/api/financial-workspaces/{family.Id}/credit-cards/{cardId}", $"{session}; {csrfCookie}", csrfToken);
        Assert.Equal(HttpStatusCode.NoContent, archive.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await UploadAsync(session, family.Id, cardId, "date,description,amount\n2026-10-20,Compra,1.00\n")).StatusCode);
        using var unauthenticated = new HttpRequestMessage(HttpMethod.Post, $"/api/financial-workspaces/{family.Id}/credit-cards/{cardId}/imports/preview") { Content = new MultipartFormDataContent() };
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client.SendAsync(unauthenticated)).StatusCode);
    }

    private async Task<HttpResponseMessage> UploadAsync(string session, Guid workspaceId, Guid cardId, string csv, IReadOnlyDictionary<string, string>? fields = null)
    {
        var (csrfCookie, csrfToken) = await GetCsrfAsync(session);
        using var content = new MultipartFormDataContent();
        content.Add(new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(csv))) { Headers = { ContentType = new MediaTypeHeaderValue("text/csv") } }, "file", "movimentacoes.csv");
        if (fields is not null) foreach (var item in fields) content.Add(new StringContent(item.Value), item.Key);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/financial-workspaces/{workspaceId}/credit-cards/{cardId}/imports/preview") { Content = content };
        request.Headers.Add("Cookie", $"{session}; {csrfCookie}");
        request.Headers.Add("X-CSRF-TOKEN", csrfToken);
        return await Client.SendAsync(request);
    }
}
