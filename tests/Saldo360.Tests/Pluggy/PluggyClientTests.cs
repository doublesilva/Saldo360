using System.Net;
using System.Text;
using Saldo360.Application.Pluggy;
using Saldo360.Infrastructure.Pluggy;

namespace Saldo360.Tests.Pluggy;

[Trait("Category", "CORE")]
[Trait("Area", "Pluggy")]
public sealed class PluggyClientTests
{
    private const string Secret = "segredo-sintetico-da-pluggy";
    private const string ItemId = "11111111-1111-4111-8111-111111111111";
    private const string AccountId = "22222222-2222-4222-8222-222222222222";

    [Fact]
    public async Task AuthenticatesOnceAndReusesApiKeyUntilItIsNearExpiry()
    {
        var http = new ScriptedHandler(ItemJson);
        var time = new ManualTime();
        var client = NewClient(http, time);

        await client.GetItemAsync(ItemId, default);
        await client.GetItemAsync(ItemId, default);
        Assert.Equal(1, http.AuthCalls);

        time.Advance(TimeSpan.FromMinutes(111));
        await client.GetItemAsync(ItemId, default);
        Assert.Equal(2, http.AuthCalls);
        Assert.Equal(new[] { "key-1", "key-1", "key-2" }, http.ApiKeysUsed);
    }

    [Fact]
    public async Task AuthRequestSendsCredentialsOnlyInBodyOfPostAuth()
    {
        var http = new ScriptedHandler(ItemJson);
        await NewClient(http).GetItemAsync(ItemId, default);

        var auth = http.Requests.First();
        Assert.Equal(HttpMethod.Post, auth.Method);
        Assert.Equal("/auth", auth.Uri.AbsolutePath);
        Assert.DoesNotContain(Secret, auth.Uri.ToString());
        Assert.Contains("\"clientSecret\"", auth.Body);
        Assert.All(http.Requests.Skip(1), r => Assert.Equal(HttpMethod.Get, r.Method));
    }

    [Fact]
    public async Task ConcurrentCallsShareASingleAuthentication()
    {
        var http = new ScriptedHandler(ItemJson) { Delay = TimeSpan.FromMilliseconds(50) };
        var client = NewClient(http);

        await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => client.GetItemAsync(ItemId, default)));
        Assert.Equal(1, http.AuthCalls);
    }

    [Fact]
    public async Task RenewsRevokedApiKeyOnceAndRetries()
    {
        var rejected = false;
        var http = new ScriptedHandler(request =>
        {
            if (rejected) return ItemJson(request);
            rejected = true;
            return new(HttpStatusCode.Unauthorized);
        });

        var item = await NewClient(http).GetItemAsync(ItemId, default);
        Assert.Equal("Itaú", item.ConnectorName);
        Assert.Equal(2, http.AuthCalls);
    }

    [Fact]
    public async Task PersistentUnauthorizedBecomesAuthenticationFailureWithoutLoop()
    {
        var http = new ScriptedHandler(_ => new(HttpStatusCode.Unauthorized));
        var error = await Assert.ThrowsAsync<PluggyException>(() => NewClient(http).GetItemAsync(ItemId, default));
        Assert.Equal(PluggyFailureKind.Authentication, error.Kind);
        Assert.Equal(2, http.AuthCalls);
        Assert.Equal(2, http.Requests.Count(r => r.Method == HttpMethod.Get));
    }

    [Fact]
    public async Task RejectedCredentialsFailAsAuthenticationAndDoNotLeakSecret()
    {
        var http = new ScriptedHandler(ItemJson) { AuthStatus = HttpStatusCode.Forbidden };
        var error = await Assert.ThrowsAsync<PluggyException>(() => NewClient(http).GetItemAsync(ItemId, default));
        Assert.Equal(PluggyFailureKind.Authentication, error.Kind);
        Assert.DoesNotContain(Secret, error.ToString());
        Assert.DoesNotContain(Secret, error.Message);
    }

    [Fact]
    public async Task MissingCredentialsFailBeforeAnyHttpCall()
    {
        var http = new ScriptedHandler(ItemJson);
        var client = NewClient(http, options: new PluggyOptions { Enabled = true });
        var error = await Assert.ThrowsAsync<PluggyException>(() => client.GetItemAsync(ItemId, default));
        Assert.Equal(PluggyFailureKind.NotConfigured, error.Kind);
        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task TransactionsUseV2CursorPaginationAndNeverTheDeprecatedEndpoint()
    {
        var http = new ScriptedHandler(request => Json(request.RequestUri!.Query.Contains("after=", StringComparison.Ordinal)
            ? """{"results":[{"id":"t3","date":"2026-10-03T00:00:00.000Z","amount":-10.5,"status":"PENDING","type":"DEBIT"}],"next":null}"""
            : """{"results":[{"id":"t1","date":"2026-10-01T00:00:00.000Z","amount":-1234.56,"status":"POSTED","type":"DEBIT"},{"id":"t2","date":"2026-10-02T00:00:00.000Z","amount":0.10,"status":"POSTED","type":"CREDIT"}],"next":"?accountId=x&after=abc%3D%3D"}"""));
        var client = NewClient(http);

        var first = await client.ListTransactionsAsync(AccountId, new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31), null, default);
        Assert.Equal("abc==", first.NextCursor);
        var second = await client.ListTransactionsAsync(AccountId, new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31), first.NextCursor, default);
        Assert.Null(second.NextCursor);

        Assert.Equal(new[] { -1234.56m, 0.10m, -10.5m }, first.Items.Concat(second.Items).Select(t => t.Amount!.Value));
        var calls = http.Requests.Where(r => r.Uri.AbsolutePath != "/auth").ToArray();
        Assert.All(calls, c => Assert.Equal("/v2/transactions", c.Uri.AbsolutePath));
        Assert.Contains($"accountId={AccountId}", calls[0].Uri.Query);
        Assert.Contains("dateFrom=2026-10-01T00:00:00.000Z", calls[0].Uri.Query);
        Assert.Contains("dateTo=2026-10-31T23:59:59.999Z", calls[0].Uri.Query);
        Assert.Contains("after=abc%3D%3D", calls[1].Uri.Query);
    }

    [Theory]
    [InlineData("?after=next-cursor", "next-cursor")]
    [InlineData("/v2/transactions?accountId=x&after=a%2Fb", "a/b")]
    [InlineData("raw-cursor", "raw-cursor")]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void ExtractsOpaqueCursorFromNextQueryString(string? next, string? expected) =>
        Assert.Equal(expected, PluggyClient.ExtractCursor(next));

    [Fact]
    public async Task OptionalAndNullFieldsAreNotInventedAndMonetaryValuesStayDecimal()
    {
        var http = new ScriptedHandler(_ => Json("""{"results":[{"id":"t1","amount":0.1,"balance":null,"description":null,"paymentData":null,"creditCardMetadata":{"installmentNumber":2,"totalInstallments":10,"totalAmount":1999.99,"cardNumber":"5555 4444 3333 2222"},"merchant":{"name":"Loja","cnpj":"12.345.678/0001-90"}}],"next":null}"""));
        var page = await NewClient(http).ListTransactionsAsync(AccountId, null, null, null, default);

        var t = Assert.Single(page.Items);
        Assert.Equal(0.1m, t.Amount);
        Assert.Null(t.Balance);
        Assert.Null(t.Date);
        Assert.Null(t.Description);
        Assert.Null(t.Payment);
        Assert.Equal(1999.99m, t.Card!.TotalAmount);
        Assert.Equal("2222", t.Card.CardLast4);
        Assert.True(t.Merchant!.HasCnpj);
        Assert.DoesNotContain("12.345", t.ToString());
        Assert.DoesNotContain("5555", t.ToString());
    }

    [Fact]
    public async Task BillsArePaginatedAndMapFinanceCharges()
    {
        var http = new ScriptedHandler(request => Json(request.RequestUri!.Query.Contains("page=2", StringComparison.Ordinal)
            ? """{"page":2,"total":3,"totalPages":2,"results":[{"id":"b3","dueDate":"2026-12-10T00:00:00.000Z","totalAmount":10}]}"""
            : """{"page":1,"total":3,"totalPages":2,"results":[{"id":"b1","dueDate":"2026-10-10T00:00:00.000Z","totalAmount":1500.25,"minimumPaymentAmount":225.04,"allowsInstallments":true,"billClosingDate":"2026-10-03T00:00:00.000Z","payments":[{"id":"p1","amount":1500.25,"currencyCode":"BRL","paymentDate":"2026-10-09T00:00:00.000Z","paymentMode":"PIX","valueType":"TOTAL"}],"financeCharges":[{"type":"IOF","amount":3.45,"currencyCode":"BRL","additionalInfo":"x"}]},{"id":"b2","dueDate":"2026-11-10T00:00:00.000Z"}]}"""));
        var client = NewClient(http);

        var first = await client.ListBillsAsync(AccountId, 1, 2, default);
        var second = await client.ListBillsAsync(AccountId, 2, 2, default);

        Assert.Equal((3, 2), (first.Total, first.TotalPages));
        Assert.Equal(1500.25m, first.Items[0].TotalAmount);
        Assert.Equal(3.45m, first.Items[0].FinanceCharges.Single().Amount);
        Assert.Equal(new DateTimeOffset(2026, 10, 3, 0, 0, 0, TimeSpan.Zero), first.Items[0].ClosingDate);
        var payment = Assert.Single(first.Items[0].Payments);
        Assert.Equal((1500.25m, "PIX"), (payment.Amount, payment.PaymentMode));
        Assert.Null(first.Items[1].ClosingDate);
        Assert.Empty(first.Items[1].Payments);
        Assert.Null(first.Items[1].TotalAmount);
        Assert.Equal("b3", Assert.Single(second.Items).Id);
        var calls = http.Requests.Where(r => r.Uri.AbsolutePath == "/bills").ToArray();
        Assert.Contains("page=1", calls[0].Uri.Query);
        Assert.Contains("pageSize=2", calls[0].Uri.Query);
        Assert.Contains("page=2", calls[1].Uri.Query);
    }

    [Fact]
    public async Task AccountsFollowAllPagesAndMapBankAndCreditData()
    {
        var http = new ScriptedHandler(request => Json(request.RequestUri!.Query.Contains("page=2", StringComparison.Ordinal)
            ? """{"page":2,"total":2,"totalPages":2,"results":[{"id":"a2","type":"CREDIT","subtype":"CREDIT_CARD","balance":300.1,"creditData":{"creditLimit":5000,"availableCreditLimit":4699.9,"balanceDueDate":"2026-11-10T00:00:00.000Z"}}]}"""
            : """{"page":1,"total":2,"totalPages":2,"results":[{"id":"a1","type":"BANK","subtype":"CHECKING_ACCOUNT","name":"Conta","number":"1234-5","balance":10.5,"currencyCode":"BRL","bankData":{"transferNumber":"0001/1234-5","overdraftContractedLimit":1000}}]}"""));
        var accounts = await NewClient(http).ListAccountsAsync(ItemId, default);

        Assert.Equal(new[] { "a1", "a2" }, accounts.Select(a => a.Id));
        Assert.Equal(1000m, accounts[0].Bank!.OverdraftContractedLimit);
        Assert.Null(accounts[0].Credit);
        Assert.Equal(4699.9m, accounts[1].Credit!.AvailableCreditLimit);
        Assert.Null(accounts[1].Name);
    }

    [Theory]
    [InlineData(429, PluggyFailureKind.RateLimited)]
    [InlineData(403, PluggyFailureKind.Forbidden)]
    [InlineData(404, PluggyFailureKind.NotFound)]
    [InlineData(400, PluggyFailureKind.Rejected)]
    [InlineData(500, PluggyFailureKind.Unavailable)]
    [InlineData(503, PluggyFailureKind.Unavailable)]
    [InlineData(504, PluggyFailureKind.Timeout)]
    public async Task HttpErrorsAreClassifiedWithoutExposingBodyOrUrl(int status, PluggyFailureKind expected)
    {
        var http = new ScriptedHandler(_ => new((HttpStatusCode)status) { Content = new StringContent("corpo-do-banco itemId cpf 123.456.789-09") });
        var error = await Assert.ThrowsAsync<PluggyException>(() => NewClient(http).GetItemAsync(ItemId, default));

        Assert.Equal(expected, error.Kind);
        Assert.DoesNotContain("corpo-do-banco", error.ToString());
        Assert.DoesNotContain(ItemId, error.ToString());
    }

    [Fact]
    public async Task TimeoutAndConnectionFailuresAreTranslated()
    {
        var timeout = new ScriptedHandler(_ => throw new TaskCanceledException("timeout"));
        Assert.Equal(PluggyFailureKind.Timeout, (await Assert.ThrowsAsync<PluggyException>(() => NewClient(timeout).GetItemAsync(ItemId, default))).Kind);

        var offline = new ScriptedHandler(_ => throw new HttpRequestException("dns " + ItemId));
        var error = await Assert.ThrowsAsync<PluggyException>(() => NewClient(offline).GetItemAsync(ItemId, default));
        Assert.Equal(PluggyFailureKind.Unavailable, error.Kind);
        Assert.DoesNotContain(ItemId, error.ToString());
    }

    [Fact]
    public async Task CallerCancellationIsNotReportedAsTimeout()
    {
        using var cancelled = new CancellationTokenSource();
        var http = new ScriptedHandler(_ => { cancelled.Cancel(); throw new OperationCanceledException(cancelled.Token); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NewClient(http).GetItemAsync(ItemId, cancelled.Token));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"total":0}""")]
    public async Task UnexpectedBodiesBecomeInvalidResponse(string body)
    {
        var http = new ScriptedHandler(_ => Json(body));
        var error = await Assert.ThrowsAsync<PluggyException>(() => NewClient(http).ListAccountsAsync(ItemId, default));
        Assert.Equal(PluggyFailureKind.InvalidResponse, error.Kind);
    }

    [Fact]
    public async Task ItemMapsConsentExpirationAndProviderErrorWithoutPresumingMissingFields()
    {
        var http = new ScriptedHandler(_ => Json("""{"id":"i","status":"LOGIN_ERROR","executionStatus":"ERROR","consentExpiresAt":"2026-10-01T00:00:00.000Z","error":{"code":"INVALID_CREDENTIALS","message":"Reautorize"},"connector":{"id":601,"name":"Itaú","products":["ACCOUNTS","LOANS"]}}"""));
        var item = await NewClient(http).GetItemAsync(ItemId, default);

        Assert.Equal("LOGIN_ERROR", item.Status);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), item.ConsentExpiresAt);
        Assert.Equal("INVALID_CREDENTIALS", item.ErrorCode);
        Assert.Null(item.LastUpdatedAt);
        Assert.Null(item.IsOpenFinance);
        Assert.Equal(new[] { "ACCOUNTS", "LOANS" }, item.Products);
    }

    // ---- helpers -------------------------------------------------------------------------------------------------

    private static PluggyClient NewClient(ScriptedHandler http, ManualTime? time = null, PluggyOptions? options = null)
    {
        options ??= new PluggyOptions { Enabled = true, ClientId = "client-sintetico", ClientSecret = Secret };
        var factory = new FakeFactory(http);
        return new PluggyClient(factory, new PluggyApiKeyProvider(factory, options, time ?? new ManualTime()));
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static HttpResponseMessage ItemJson(HttpRequestMessage _) => Json("""{"id":"11111111-1111-4111-8111-111111111111","status":"UPDATED","executionStatus":"SUCCESS","connector":{"id":601,"name":"Itaú"}}""");

    private sealed class FakeFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, false) { BaseAddress = new("https://api.pluggy.ai/") };
    }

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset now = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan span) => now += span;
    }

    private sealed class ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        private int auth;
        public TimeSpan Delay { get; init; }
        public HttpStatusCode AuthStatus { get; init; } = HttpStatusCode.OK;
        public List<(HttpMethod Method, Uri Uri, string Body)> Requests { get; } = [];
        public List<string> ApiKeysUsed { get; } = [];
        public int AuthCalls => auth;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Delay > TimeSpan.Zero) await Task.Delay(Delay, cancellationToken);
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            lock (Requests) Requests.Add((request.Method, request.RequestUri!, body));
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/auth")
            {
                var n = Interlocked.Increment(ref auth);
                return AuthStatus == HttpStatusCode.OK ? Json($$"""{"apiKey":"key-{{n}}"}""") : new(AuthStatus);
            }
            lock (ApiKeysUsed) ApiKeysUsed.Add(request.Headers.GetValues("X-API-KEY").Single());
            return respond(request);
        }
    }
}
