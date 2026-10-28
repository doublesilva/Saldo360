namespace Saldo360.Application.Pluggy;

/// <summary>
/// Executa o diagnóstico de cobertura de um Item. Só registra o que a Pluggy de fato retornou; campo ausente em
/// todos os registros consultados é reportado como ausente, sem presumir disponibilidade.
/// </summary>
internal sealed class CoverageRun(IPluggyClient client, PluggyOptions options, DateTimeOffset now)
{
    private bool rateLimited;
    private PluggyConnectionState itemState;

    public async Task<PluggyDiagnosticsView> RunAsync(Domain.Pluggy.PluggyItemLink link, CancellationToken ct)
    {
        var item = await client.GetItemAsync(link.PluggyItemId, ct);
        itemState = PluggyPocService.StateOf(item, now);
        var notes = new List<string>
        {
            "Retrato único: mudanças de status PENDING→POSTED só podem ser comprovadas comparando sincronizações sucessivas.",
            "Campos ausentes significam ausentes em todos os registros consultados, não necessariamente indisponíveis no conector.",
        };

        var itemResources = new List<PluggyResourceCoverage>();
        IReadOnlyList<PluggyAccount> accounts = [];
        var accountsResult = await QueryAsync("Contas", () => client.ListAccountsAsync(link.PluggyItemId, ct), a => a.UpdatedAt, ct,
            Field<PluggyAccount>("name", a => a.Name is not null), Field<PluggyAccount>("type", a => a.Type is not null), Field<PluggyAccount>("subtype", a => a.Subtype is not null),
            Field<PluggyAccount>("number", a => a.Number is not null), Field<PluggyAccount>("currencyCode", a => a.CurrencyCode is not null));
        itemResources.Add(accountsResult.Coverage);
        accounts = accountsResult.Items;

        itemResources.Add(await OptionalProductAsync("Empréstimos", item, "LOANS", () => client.ListLoansAsync(link.PluggyItemId, ct), l => l.ContractDate ?? l.DueDate, ct,
            Field<PluggyLoan>("productName", l => l.ProductName is not null), Field<PluggyLoan>("contractAmount", l => l.ContractAmount is not null),
            Field<PluggyLoan>("totalInstallments", l => l.TotalInstallments is not null), Field<PluggyLoan>("paidInstallments", l => l.PaidInstallments is not null),
            Field<PluggyLoan>("outstandingBalance", l => l.OutstandingBalance is not null)));
        itemResources.Add(await OptionalProductAsync("Investimentos", item, "INVESTMENTS", () => client.ListInvestmentsAsync(link.PluggyItemId, ct), i => i.Date, ct,
            Field<PluggyInvestment>("name", i => i.Name is not null), Field<PluggyInvestment>("type", i => i.Type is not null),
            Field<PluggyInvestment>("balance", i => i.Balance is not null), Field<PluggyInvestment>("amountProfit", i => i.AmountProfit is not null)));

        var perAccount = new List<PluggyAccountCoverage>();
        foreach (var account in accounts) perAccount.Add(await DiagnoseAccountAsync(account, ct));
        return new(link.Id, item.ConnectorName, itemState, item.LastUpdatedAt, now, item.Products, itemResources, perAccount, notes);
    }

    private async Task<PluggyAccountCoverage> DiagnoseAccountAsync(PluggyAccount account, CancellationToken ct)
    {
        var isCredit = string.Equals(account.Type, "CREDIT", StringComparison.OrdinalIgnoreCase) || account.Credit is not null;
        var resources = new List<PluggyResourceCoverage>
        {
            Snapshot("Saldo", account, account.Balance is not null, new[]
            {
                Field<PluggyAccount>("balance", a => a.Balance is not null), Field<PluggyAccount>("currencyCode", a => a.CurrencyCode is not null),
                Field<PluggyAccount>("updatedAt", a => a.UpdatedAt is not null), Field<PluggyAccount>("bankData.closingBalance", a => a.Bank?.ClosingBalance is not null),
                Field<PluggyAccount>("bankData.automaticallyInvestedBalance", a => a.Bank?.AutomaticallyInvestedBalance is not null),
            }, account.UpdatedAt),
            Limits(account, isCredit),
            isCredit ? Snapshot("Cartões", account, true, new[]
            {
                Field<PluggyAccount>("creditData.brand", a => a.Credit?.Brand is not null), Field<PluggyAccount>("creditData.level", a => a.Credit?.Level is not null),
                Field<PluggyAccount>("number", a => a.Number is not null), Field<PluggyAccount>("creditData.balanceCloseDate", a => a.Credit?.BalanceCloseDate is not null),
                Field<PluggyAccount>("creditData.balanceDueDate", a => a.Credit?.BalanceDueDate is not null), Field<PluggyAccount>("creditData.status", a => a.Credit?.Status is not null),
            }, account.UpdatedAt) : NotSupported("Cartões", "Conta não é de cartão de crédito."),
        };

        var transactions = await AllTransactionsAsync(account, ct);
        resources.Add(transactions.Coverage);
        if (isCredit)
        {
            resources.Add(await BillsAsync(account, ct));
            resources.Add(InstallmentsFrom(transactions));
        }
        else
        {
            resources.Add(NotSupported("Faturas", "Conta não é de cartão de crédito."));
            resources.Add(NotSupported("Parcelas", "Conta não é de cartão de crédito."));
        }
        return new(account.Id, account.Name, account.Type, account.Subtype, account.UpdatedAt, resources);
    }

    private PluggyResourceCoverage Limits(PluggyAccount account, bool isCredit)
    {
        if (account.Bank is null && account.Credit is null) return NotSupported("Limites", "A Pluggy não retornou bankData nem creditData para esta conta.");
        var fields = isCredit
            ? new[] { Field<PluggyAccount>("creditData.creditLimit", a => a.Credit?.CreditLimit is not null), Field<PluggyAccount>("creditData.availableCreditLimit", a => a.Credit?.AvailableCreditLimit is not null),
                Field<PluggyAccount>("creditData.minimumPayment", a => a.Credit?.MinimumPayment is not null) }
            : new[] { Field<PluggyAccount>("bankData.overdraftContractedLimit", a => a.Bank?.OverdraftContractedLimit is not null), Field<PluggyAccount>("bankData.overdraftUsedLimit", a => a.Bank?.OverdraftUsedLimit is not null),
                Field<PluggyAccount>("bankData.unarrangedOverdraftAmount", a => a.Bank?.UnarrangedOverdraftAmount is not null) };
        return Snapshot("Limites", account, fields.Any(f => f.Has(account)), fields, account.UpdatedAt);
    }

    private sealed record TransactionRun(PluggyResourceCoverage Coverage, IReadOnlyList<PluggyTransaction> Items);

    private async Task<TransactionRun> AllTransactionsAsync(PluggyAccount account, CancellationToken ct)
    {
        var all = new List<PluggyTransaction>();
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        string? after = null;
        var truncated = false;
        try
        {
            for (var page = 1; ; page++)
            {
                if (rateLimited) throw new PluggyException(PluggyFailureKind.RateLimited);
                var result = await client.ListTransactionsAsync(account.Id, null, null, after, ct);
                all.AddRange(result.Items);
                if (result.NextCursor is null) break;
                // Cursor repetido ou limite de páginas: interrompe em vez de percorrer indefinidamente.
                if (!cursors.Add(result.NextCursor) || page >= options.MaxTransactionPages) { truncated = true; break; }
                after = result.NextCursor;
            }
        }
        catch (PluggyException error)
        {
            if (error.Kind == PluggyFailureKind.RateLimited) rateLimited = true;
            if (all.Count == 0) return new(Failed("Transações", error), []);
            truncated = true;
        }

        var limitations = new List<string>();
        if (truncated) limitations.Add($"Histórico possivelmente incompleto: consulta interrompida após {all.Count} registros.");
        limitations.Add($"{all.Count(t => Is(t.Status, "PENDING"))} pendentes e {all.Count(t => Is(t.Status, "POSTED"))} efetivadas.");
        var duplicateIds = all.GroupBy(t => t.Id).Count(g => g.Count() > 1);
        var possible = all.GroupBy(t => (t.Date?.Date, t.Amount, t.DescriptionRaw ?? t.Description)).Count(g => g.Count() > 1);
        limitations.Add($"{duplicateIds} IDs repetidos e {possible} grupos com mesma data, valor e descrição (possíveis duplicidades legítimas ou não).");
        if (all.Count(t => t.Date > now) is > 0 and var future) limitations.Add($"{future} transações com data futura.");

        var fields = Fields(all,
            Field<PluggyTransaction>("id", t => true), Field<PluggyTransaction>("date", t => t.Date is not null), Field<PluggyTransaction>("description", t => t.Description is not null),
            Field<PluggyTransaction>("descriptionRaw", t => t.DescriptionRaw is not null), Field<PluggyTransaction>("amount", t => t.Amount is not null),
            Field<PluggyTransaction>("currencyCode", t => t.CurrencyCode is not null), Field<PluggyTransaction>("type", t => t.Type is not null),
            Field<PluggyTransaction>("status", t => t.Status is not null), Field<PluggyTransaction>("category", t => t.Category is not null),
            Field<PluggyTransaction>("balance", t => t.Balance is not null), Field<PluggyTransaction>("operationType", t => t.OperationType is not null),
            Field<PluggyTransaction>("paymentData", t => t.Payment is not null), Field<PluggyTransaction>("paymentData.paymentMethod", t => t.Payment?.PaymentMethod is not null),
            Field<PluggyTransaction>("paymentData.referenceNumber", t => t.Payment?.HasReferenceNumber == true), Field<PluggyTransaction>("merchant", t => t.Merchant is not null),
            Field<PluggyTransaction>("creditCardMetadata", t => t.Card is not null));
        var dates = all.Where(t => t.Date is not null).Select(t => t.Date!.Value).ToArray();
        var coverage = new PluggyResourceCoverage("Transações", all.Count == 0 ? PluggyResourceState.Empty : PluggyResourceState.Available, all.Count,
            dates.Length == 0 ? null : dates.Min(), dates.Length == 0 ? null : dates.Max(), fields, limitations, null);
        return new(coverage, all);
    }

    private PluggyResourceCoverage InstallmentsFrom(TransactionRun run)
    {
        if (run.Coverage.State is not (PluggyResourceState.Available or PluggyResourceState.Empty))
            return run.Coverage with { Resource = "Parcelas", Count = 0, EarliestDate = null, LatestDate = null, Fields = [] };
        var installments = run.Items.Where(t => t.Card?.TotalInstallments is > 1).ToArray();
        var limitations = new List<string>();
        var future = installments.Count(t => t.Date > now);
        limitations.Add($"{future} parcelas com data futura entre as transações retornadas.");
        var dates = installments.Where(t => t.Date is not null).Select(t => t.Date!.Value).ToArray();
        return new("Parcelas", installments.Length == 0 ? PluggyResourceState.Empty : PluggyResourceState.Available, installments.Length,
            dates.Length == 0 ? null : dates.Min(), dates.Length == 0 ? null : dates.Max(),
            Fields(installments, Field<PluggyTransaction>("creditCardMetadata.installmentNumber", t => t.Card?.InstallmentNumber is not null),
                Field<PluggyTransaction>("creditCardMetadata.totalInstallments", t => t.Card?.TotalInstallments is not null),
                Field<PluggyTransaction>("creditCardMetadata.totalAmount", t => t.Card?.TotalAmount is not null),
                Field<PluggyTransaction>("creditCardMetadata.billId", t => t.Card?.BillId is not null)), limitations, null);
    }

    private async Task<PluggyResourceCoverage> BillsAsync(PluggyAccount account, CancellationToken ct)
    {
        var all = new List<PluggyBill>();
        try
        {
            if (rateLimited) throw new PluggyException(PluggyFailureKind.RateLimited);
            for (var page = 1; ; page++)
            {
                var result = await client.ListBillsAsync(account.Id, page, 100, ct);
                all.AddRange(result.Items);
                if (page >= (result.TotalPages ?? 1)) break;
                if (page >= options.MaxTransactionPages) return Done(all, ["Histórico de faturas truncado pelo limite de páginas."]);
            }
        }
        catch (PluggyException error)
        {
            if (error.Kind == PluggyFailureKind.RateLimited) rateLimited = true;
            return Failed("Faturas", error);
        }
        return Done(all, []);

        PluggyResourceCoverage Done(List<PluggyBill> bills, string[] extra)
        {
            var dates = bills.Where(b => b.DueDate is not null).Select(b => b.DueDate!.Value).ToArray();
            var limitations = extra.ToList();
            limitations.Add($"{bills.Count(b => b.DueDate > now)} faturas com vencimento futuro (abertas).");
            limitations.Add("IOF, juros e multas só aparecem se vierem em financeCharges (não há campo próprio).");
            return new("Faturas", bills.Count == 0 ? PluggyResourceState.Empty : PluggyResourceState.Available, bills.Count,
                dates.Length == 0 ? null : dates.Min(), dates.Length == 0 ? null : dates.Max(),
                Fields(bills, Field<PluggyBill>("id", b => true), Field<PluggyBill>("dueDate", b => b.DueDate is not null), Field<PluggyBill>("totalAmount", b => b.TotalAmount is not null),
                    Field<PluggyBill>("minimumPaymentAmount", b => b.MinimumPaymentAmount is not null), Field<PluggyBill>("allowsInstallments", b => b.AllowsInstallments is not null),
                    Field<PluggyBill>("financeCharges", b => b.FinanceCharges.Count > 0), Field<PluggyBill>("billClosingDate", b => b.ClosingDate is not null),
                    Field<PluggyBill>("payments", b => b.Payments.Count > 0)), limitations, null);
        }
    }

    private async Task<PluggyResourceCoverage> OptionalProductAsync<T>(string name, PluggyItem item, string product, Func<Task<IReadOnlyList<T>>> query,
        Func<T, DateTimeOffset?> date, CancellationToken ct, params (string Name, Func<T, bool> Has)[] fields)
    {
        if (item.Products.Count == 0) return NotSupported(name, "O conector não informou a lista de produtos; consulta não realizada.");
        if (!item.Products.Contains(product, StringComparer.OrdinalIgnoreCase)) return NotSupported(name, $"O conector não declara o produto {product}.");
        return (await QueryAsync(name, query, date, ct, fields)).Coverage;
    }

    private sealed record Queried<T>(PluggyResourceCoverage Coverage, IReadOnlyList<T> Items);

    private async Task<Queried<T>> QueryAsync<T>(string name, Func<Task<IReadOnlyList<T>>> query, Func<T, DateTimeOffset?> date, CancellationToken ct,
        params (string Name, Func<T, bool> Has)[] fields)
    {
        try
        {
            if (rateLimited) throw new PluggyException(PluggyFailureKind.RateLimited);
            var items = await query();
            var dates = items.Select(date).Where(d => d is not null).Select(d => d!.Value).ToArray();
            return new(new(name, items.Count == 0 ? PluggyResourceState.Empty : PluggyResourceState.Available, items.Count,
                dates.Length == 0 ? null : dates.Min(), dates.Length == 0 ? null : dates.Max(), Fields(items, fields), [], null), items);
        }
        catch (PluggyException error)
        {
            if (error.Kind == PluggyFailureKind.RateLimited) rateLimited = true;
            return new(Failed(name, error), []);
        }
    }

    /// <summary>Distingue produto indisponível, autorização expirada e erro de integração.</summary>
    private PluggyResourceCoverage Failed(string name, PluggyException error)
    {
        var state = error.Kind switch
        {
            PluggyFailureKind.NotFound or PluggyFailureKind.Forbidden or PluggyFailureKind.Rejected =>
                itemState is PluggyConnectionState.ConsentExpired or PluggyConnectionState.ActionRequired ? PluggyResourceState.ConsentExpired : PluggyResourceState.Unavailable,
            _ => PluggyResourceState.Error,
        };
        return new(name, state, 0, null, null, [], [error.Message], error.Kind.ToString());
    }

    private static PluggyResourceCoverage NotSupported(string name, string reason) =>
        new(name, PluggyResourceState.NotSupported, 0, null, null, [], [reason], null);

    /// <summary>Recurso descrito por um único registro (a própria conta): conta como presente só se algum valor veio.</summary>
    private static PluggyResourceCoverage Snapshot(string name, PluggyAccount account, bool hasValue, (string Name, Func<PluggyAccount, bool> Has)[] fields, DateTimeOffset? date) =>
        new(name, hasValue ? PluggyResourceState.Available : PluggyResourceState.Empty, hasValue ? 1 : 0, date, date, Fields([account], fields), [], null);

    private static (string Name, Func<T, bool> Has) Field<T>(string name, Func<T, bool> has) => (name, has);

    private static IReadOnlyList<PluggyFieldCoverage> Fields<T>(IReadOnlyList<T> items, params (string Name, Func<T, bool> Has)[] fields) =>
        fields.Select(f => new PluggyFieldCoverage(f.Name, items.Count(f.Has), items.Count)).ToArray();

    private static bool Is(string? value, string expected) => string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);
}
