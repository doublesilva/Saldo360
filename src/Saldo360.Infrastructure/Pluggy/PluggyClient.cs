using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Web;
using Saldo360.Application.Pluggy;

namespace Saldo360.Infrastructure.Pluggy;

/// <summary>
/// Cliente somente leitura da API Pluggy. Mensagens de erro nunca contêm URL, corpo de resposta ou token.
/// Usa <c>GET /v2/transactions</c> (paginação por cursor); o endpoint legado <c>/transactions</c> não é usado.
/// </summary>
internal sealed class PluggyClient(IHttpClientFactory clients, PluggyApiKeyProvider keys) : IPluggyClient
{
    private const int MaxPages = 10;

    public async Task<PluggyItem> GetItemAsync(string itemId, CancellationToken ct)
    {
        using var doc = await GetAsync($"items/{Uri.EscapeDataString(itemId)}", ct);
        var item = doc.RootElement;
        Require(item, JsonValueKind.Object);
        var connector = Obj(item, "connector");
        var error = Obj(item, "error");
        var products = new List<string>();
        if (connector is { } c && c.TryGetProperty("products", out var list) && list.ValueKind == JsonValueKind.Array)
            products.AddRange(list.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!));
        return new(Str(item, "id") ?? itemId, connector is { } c1 ? Int(c1, "id") : null, connector is { } c2 ? Str(c2, "name") : null,
            connector is { } c3 ? Str(c3, "type") : null, connector is { } c4 ? Bool(c4, "isOpenFinance") : null,
            Str(item, "status"), Str(item, "executionStatus"), Date(item, "createdAt"), Date(item, "lastUpdatedAt"), Date(item, "consentExpiresAt"),
            error is { } e1 ? Str(e1, "code") : null, error is { } e2 ? Truncate(Str(e2, "message"), 300) : null, products);
    }

    public async Task<IReadOnlyList<PluggyAccount>> ListAccountsAsync(string itemId, CancellationToken ct) =>
        await ListAllAsync($"accounts?itemId={Uri.EscapeDataString(itemId)}", ReadAccount, ct);

    public async Task<PluggyAccount> GetAccountAsync(string accountId, CancellationToken ct)
    {
        using var doc = await GetAsync($"accounts/{Uri.EscapeDataString(accountId)}", ct);
        Require(doc.RootElement, JsonValueKind.Object);
        return ReadAccount(doc.RootElement);
    }

    public async Task<PluggyCursorPage<PluggyTransaction>> ListTransactionsAsync(string accountId, DateOnly? from, DateOnly? to, string? after, CancellationToken ct)
    {
        var url = $"v2/transactions?accountId={Uri.EscapeDataString(accountId)}";
        if (from is { } f) url += $"&dateFrom={f.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}T00:00:00.000Z";
        if (to is { } t) url += $"&dateTo={t.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}T23:59:59.999Z";
        if (!string.IsNullOrEmpty(after)) url += $"&after={Uri.EscapeDataString(after)}";
        using var doc = await GetAsync(url, ct);
        Require(doc.RootElement, JsonValueKind.Object);
        var items = Results(doc.RootElement).Select(ReadTransaction).ToArray();
        return new(items, ExtractCursor(Str(doc.RootElement, "next")));
    }

    public async Task<PluggyPage<PluggyBill>> ListBillsAsync(string accountId, int page, int pageSize, CancellationToken ct)
    {
        using var doc = await GetAsync($"bills?accountId={Uri.EscapeDataString(accountId)}&page={Math.Max(1, page)}&pageSize={Math.Clamp(pageSize, 1, 500)}", ct);
        Require(doc.RootElement, JsonValueKind.Object);
        return new(Results(doc.RootElement).Select(ReadBill).ToArray(), Int(doc.RootElement, "total"), Int(doc.RootElement, "totalPages"), Int(doc.RootElement, "page") ?? page);
    }

    public async Task<IReadOnlyList<PluggyLoan>> ListLoansAsync(string itemId, CancellationToken ct) =>
        await ListAllAsync($"loans?itemId={Uri.EscapeDataString(itemId)}", ReadLoan, ct);

    public async Task<IReadOnlyList<PluggyInvestment>> ListInvestmentsAsync(string itemId, CancellationToken ct) =>
        await ListAllAsync($"investments?itemId={Uri.EscapeDataString(itemId)}", ReadInvestment, ct);

    // ---- HTTP ----------------------------------------------------------------------------------------------------

    private async Task<List<T>> ListAllAsync<T>(string url, Func<JsonElement, T> read, CancellationToken ct)
    {
        var all = new List<T>();
        for (var page = 1; page <= MaxPages; page++)
        {
            using var doc = await GetAsync($"{url}&page={page}&pageSize=500", ct);
            Require(doc.RootElement, JsonValueKind.Object);
            all.AddRange(Results(doc.RootElement).Select(read));
            if (page >= (Int(doc.RootElement, "totalPages") ?? 1)) return all;
        }
        throw new PluggyException(PluggyFailureKind.InvalidResponse);
    }

    private async Task<JsonDocument> GetAsync(string url, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var key = await keys.GetAsync(ct);
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Add("X-API-KEY", key);
                request.Headers.Accept.ParseAdd("application/json");
                using var response = await clients.CreateClient(PluggyApiKeyProvider.ClientName).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                // Chave expirada ou revogada: renova uma única vez e repete a consulta.
                if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0) { await keys.InvalidateAsync(key, ct); continue; }
                if (!response.IsSuccessStatusCode)
                    throw new PluggyException(PluggyClientErrors.FromStatus(response.StatusCode), (int)response.StatusCode,
                        response.StatusCode == HttpStatusCode.TooManyRequests && response.Headers.RetryAfter?.Delta is { } wait ? wait : null);
                return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            }
            catch (PluggyException) { throw; }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new PluggyException(PluggyFailureKind.Timeout); }
            catch (HttpRequestException) { throw new PluggyException(PluggyFailureKind.Unavailable); }
            catch (JsonException) { throw new PluggyException(PluggyFailureKind.InvalidResponse); }
        }
    }

    /// <summary>A Pluggy devolve <c>next</c> como query string pronta; o valor útil é o <c>after</c> decodificado.</summary>
    internal static string? ExtractCursor(string? next)
    {
        if (string.IsNullOrWhiteSpace(next)) return null;
        var query = next.Contains('?') ? next[(next.IndexOf('?') + 1)..] : next;
        if (!query.Contains("after=", StringComparison.Ordinal)) return next;
        var after = HttpUtility.ParseQueryString(query)["after"];
        return string.IsNullOrEmpty(after) ? null : after;
    }

    // ---- Mapeamento tolerante: nenhum campo é presumido ----------------------------------------------------------

    private static PluggyAccount ReadAccount(JsonElement e)
    {
        var bank = Obj(e, "bankData");
        var credit = Obj(e, "creditData");
        return new(Req(e, "id"), Str(e, "itemId"), Str(e, "type"), Str(e, "subtype"), Str(e, "name"), Str(e, "marketingName"), Str(e, "number"),
            Dec(e, "balance"), Str(e, "currencyCode"), Date(e, "updatedAt"),
            bank is { } b ? new PluggyBankData(Str(b, "transferNumber"), Dec(b, "closingBalance"), Dec(b, "automaticallyInvestedBalance"),
                Dec(b, "overdraftContractedLimit"), Dec(b, "overdraftUsedLimit"), Dec(b, "unarrangedOverdraftAmount")) : null,
            credit is { } c ? new PluggyCreditData(Str(c, "level"), Str(c, "brand"), Date(c, "balanceCloseDate"), Date(c, "balanceDueDate"),
                Dec(c, "availableCreditLimit"), Dec(c, "creditLimit"), Dec(c, "minimumPayment"), Dec(c, "balanceForeignCurrency"), Str(c, "status"), Str(c, "holderType")) : null);
    }

    private static PluggyTransaction ReadTransaction(JsonElement e)
    {
        var payment = Obj(e, "paymentData");
        var card = Obj(e, "creditCardMetadata");
        var merchant = Obj(e, "merchant");
        return new(Req(e, "id"), Str(e, "accountId"), Date(e, "date"), Str(e, "description"), Str(e, "descriptionRaw"),
            Dec(e, "amount"), Dec(e, "amountInAccountCurrency"), Str(e, "currencyCode"), Str(e, "type"), Str(e, "status"),
            Str(e, "category"), Str(e, "categoryId"), Dec(e, "balance"), Str(e, "providerCode"), Str(e, "operationType"),
            payment is { } p ? new PluggyPaymentData(Obj(p, "payer") is not null, Obj(p, "receiver") is not null, Str(p, "paymentMethod"),
                !string.IsNullOrWhiteSpace(Str(p, "referenceNumber")), Truncate(Str(p, "reason"), 120)) : null,
            card is { } c ? new PluggyCardMetadata(Int(c, "installmentNumber"), Int(c, "totalInstallments"), Dec(c, "totalAmount"), Date(c, "purchaseDate"),
                Str(c, "billId"), Last4(Str(c, "cardNumber")), Str(c, "payeeMCC"), Date(c, "billForecastDate")) : null,
            merchant is { } m ? new PluggyMerchant(Str(m, "name"), Str(m, "businessName"), !string.IsNullOrWhiteSpace(Str(m, "cnpj")), Str(m, "category")) : null);
    }

    private static PluggyBill ReadBill(JsonElement e)
    {
        var charges = new List<PluggyFinanceCharge>();
        if (e.TryGetProperty("financeCharges", out var list) && list.ValueKind == JsonValueKind.Array)
            charges.AddRange(list.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object)
                .Select(x => new PluggyFinanceCharge(Str(x, "type"), Dec(x, "amount"), Str(x, "currencyCode"), Truncate(Str(x, "additionalInfo"), 160))));
        var payments = new List<PluggyBillPayment>();
        if (e.TryGetProperty("payments", out var paid) && paid.ValueKind == JsonValueKind.Array)
            payments.AddRange(paid.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object)
                .Select(x => new PluggyBillPayment(Dec(x, "amount"), Str(x, "currencyCode"), Date(x, "paymentDate"), Str(x, "paymentMode"), Str(x, "valueType"))));
        return new(Req(e, "id"), Date(e, "dueDate"), Dec(e, "totalAmount"), Str(e, "totalAmountCurrencyCode"), Dec(e, "minimumPaymentAmount"),
            Bool(e, "allowsInstallments"), charges, Date(e, "billClosingDate"), payments);
    }

    private static PluggyLoan ReadLoan(JsonElement e)
    {
        var installments = Obj(e, "installments");
        var payments = Obj(e, "payments");
        return new(Req(e, "id"), Str(e, "kind"), Str(e, "productName"), Str(e, "type"), Str(e, "currencyCode"), Dec(e, "contractAmount"),
            Date(e, "contractDate"), Date(e, "dueDate"),
            installments is { } i1 ? Int(i1, "totalNumberOfInstallments") : null, installments is { } i2 ? Int(i2, "paidInstallments") : null,
            installments is { } i3 ? Int(i3, "dueInstallments") : null, installments is { } i4 ? Int(i4, "pastDueInstallments") : null,
            payments is { } p ? Dec(p, "contractOutstandingBalance") : null);
    }

    private static PluggyInvestment ReadInvestment(JsonElement e) =>
        new(Req(e, "id"), Str(e, "type"), Str(e, "subtype"), Str(e, "name"), Str(e, "status"), Dec(e, "balance"), Str(e, "currencyCode"), Date(e, "date"), Dec(e, "amountProfit"));

    private static IEnumerable<JsonElement> Results(JsonElement root) =>
        root.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array
            ? results.EnumerateArray().Select(x => { Require(x, JsonValueKind.Object); return x; })
            : throw new PluggyException(PluggyFailureKind.InvalidResponse);

    private static void Require(JsonElement e, JsonValueKind kind) { if (e.ValueKind != kind) throw new PluggyException(PluggyFailureKind.InvalidResponse); }
    private static string Req(JsonElement e, string name) => Str(e, name) is { Length: > 0 } v ? v : throw new PluggyException(PluggyFailureKind.InvalidResponse);

    private static JsonElement? Obj(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;
    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(v.GetString()) ? v.GetString() : null;
    private static decimal? Dec(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d) ? d : null;
    private static int? Int(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;
    private static bool? Bool(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;
    private static DateTimeOffset? Date(JsonElement e, string name) =>
        Str(e, name) is { } s && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d : null;
    private static string? Truncate(string? value, int max) => value is { } v && v.Length > max ? v[..max] : value;
    private static string? Last4(string? value)
    {
        var digits = value is null ? "" : new string(value.Where(char.IsDigit).ToArray());
        return digits.Length >= 4 ? digits[^4..] : null;
    }
}
