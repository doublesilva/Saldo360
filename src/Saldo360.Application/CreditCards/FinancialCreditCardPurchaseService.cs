using Saldo360.Domain.Categories;
using Saldo360.Application.Workspaces;
using Saldo360.Domain.CreditCards;

namespace Saldo360.Application.CreditCards;

public sealed class FinancialCreditCardPurchaseService(IFinancialWorkspaceRepository workspaces, IFinancialCreditCardPurchaseRepository purchases, TimeProvider timeProvider)
{
    public async Task<CreditCardPurchaseResult> CreateAsync(Guid workspaceId, Guid cardId, Guid userId, CreditCardPurchaseInput input, CancellationToken cancellationToken)
        => await purchases.InWorkspaceTransactionAsync(workspaceId, () => SaveAsync(workspaceId, cardId, null, userId, input, cancellationToken), cancellationToken);

    public async Task<CreditCardPurchaseResult> UpdateAsync(Guid workspaceId, Guid cardId, Guid purchaseId, Guid userId, CreditCardPurchaseInput input, CancellationToken ct)
        => await purchases.InWorkspaceTransactionAsync(workspaceId, () => SaveAsync(workspaceId, cardId, purchaseId, userId, input, ct), ct);

    public async Task<CreditCardPurchasePreview?> PreviewAsync(Guid workspaceId, Guid cardId, Guid userId, CreditCardPurchaseInput input, CancellationToken ct)
    {
        if (!await IsParticipant(workspaceId, userId, ct) || !await purchases.IsActiveCardAsync(workspaceId, cardId, ct)) return null;
        var calendar = await purchases.GetCardCalendarAsync(workspaceId, cardId, ct);
        return calendar is null ? null : BuildPreview(input, calendar);
    }

    private static CreditCardPurchasePreview BuildPreview(CreditCardPurchaseInput input, CreditCardCalendarConfig calendar)
    {
        decimal? total = input.TotalAmount;
        if (input.AmountMode == "Installment" && total is not null && input.InstallmentCount is not null)
        {
            try { total = checked(total.Value * input.InstallmentCount.Value); }
            catch (OverflowException) { throw new ArgumentException("Valor total fora do limite permitido."); }
        }
        if (input.AmountMode is not ("Total" or "Installment")) throw new ArgumentException("Modo de valor inválido.");
        if (input.TotalAmount is <= 0m) throw new ArgumentException("O valor deve ser maior que zero.");
        if (input.TotalAmount is { } amount && Saldo360.Domain.Common.Money.Validate(amount, "Valor") is { } moneyError) throw new ArgumentException(moneyError);
        if (CreditCardPurchaseDetails.Validate(input.Description, total, input.InstallmentCount, out _) is { } error) throw new ArgumentException(error);
        if (input.PurchaseDate is null) throw new ArgumentException("Informe a data da compra.");
        var p = FinancialCreditCardPurchase.CreateOngoing(workspaceId: Guid.NewGuid(), cardId: Guid.NewGuid(), categoryId: Guid.NewGuid(),
            input.Description!, input.PurchaseDate.Value, total!.Value, input.InstallmentCount!.Value, input.PreviouslyPaidInstallments, input.FirstPendingCompetence, Guid.NewGuid(), DateTimeOffset.UnixEpoch);
        var rows = p.Installments.Select(i => {
            var cycle = Cycle(p, i, calendar);
            return new CreditCardPurchasePreviewRow(i.InstallmentNumber, Saldo360.Domain.Common.Money.Format(i.Amount), cycle.Competence, cycle.DueDate);
        }).OrderBy(i => i.Number).ToList();
        if (rows.Any(r => r.DueDate < input.PurchaseDate.Value)) throw new ArgumentException("A competência pendente não pode vencer antes da compra.");
        if (p.Installments.Any(i => Cycle(p, i, calendar).ClosingDate < input.PurchaseDate.Value)) throw new ArgumentException("A fatura pendente não pode fechar antes da data da compra.");
        return new(Saldo360.Domain.Common.Money.Format(total.Value), input.InstallmentCount.Value, input.PreviouslyPaidInstallments,
            Saldo360.Domain.Common.Money.Format(p.Installments.Sum(i => i.Amount)), rows);
    }

    private static CreditCardInvoiceCalendar Cycle(FinancialCreditCardPurchase purchase, FinancialCreditCardInstallment installment, CreditCardCalendarConfig calendar)
    {
        if (purchase.FirstPendingCompetence is not { } first) return CreditCardInvoiceCalendarCalculator.ForInstallment(purchase.PurchaseDate, installment.InstallmentNumber, calendar.ClosingDay, calendar.DueDay);
        var competence = first.AddMonths(installment.InstallmentNumber - purchase.PreviouslyPaidInstallments - 1);
        var closingMonth = calendar.ClosingDay < calendar.DueDay ? competence : competence.AddMonths(-1);
        var closing = CreditCardInvoiceCalendarCalculator.AddMonths(closingMonth, 0, calendar.ClosingDay);
        var cycle = CreditCardInvoiceCalendarCalculator.ForInstallment(closing, 1, calendar.ClosingDay, calendar.DueDay);
        if (cycle.Competence != competence.ToString("yyyy-MM")) throw new ArgumentException("A competência informada não é representável pelo calendário do cartão após o ajuste dos dias do mês.");
        return cycle;
    }

    private async Task<CreditCardPurchaseResult> SaveAsync(Guid workspaceId, Guid cardId, Guid? purchaseId, Guid userId, CreditCardPurchaseInput input, CancellationToken cancellationToken)
    {
        if (!await IsParticipant(workspaceId, userId, cancellationToken) || !await purchases.IsActiveCardAsync(workspaceId, cardId, cancellationToken)) return CreditCardPurchaseResult.NotFound;
        var calendar = await purchases.GetCardCalendarAsync(workspaceId, cardId, cancellationToken);
        if (calendar is null) return CreditCardPurchaseResult.NotFound;
        CreditCardPurchasePreview preview;
        try { preview = BuildPreview(input, calendar); }
        catch (ArgumentException ex) { return CreditCardPurchaseResult.Invalid(new Dictionary<string, string[]> { ["purchase"] = [ex.Message] }); }
        var errors = new Dictionary<string, string[]>();
        Guid? currentCategory = null;
        if (purchaseId is { } editing && await purchases.FindAsync(workspaceId, cardId, editing, cancellationToken) is { } existing) currentCategory = existing.CategoryId;
        if (input.CategoryId is null || !await purchases.IsExpenseCategoryAsync(workspaceId, input.CategoryId.Value, currentCategory, cancellationToken)) errors["categoryId"] = ["A categoria deve ser uma despesa ativa do orçamento (e, numa subcategoria, a categoria principal também ativa)."];
        if (!ExpenseNatures.TryResolve(input.ExpenseNature, CategoryType.Expense, out var nature, out var natureError)) errors["expenseNature"] = [natureError!];
        if (errors.Count > 0) return CreditCardPurchaseResult.Invalid(errors);
        var total = decimal.Parse(preview.TotalAmount, System.Globalization.CultureInfo.InvariantCulture);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(timeProvider.GetUtcNow(), TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo")).DateTime);
        FinancialCreditCardPurchase purchase;
        if (purchaseId is { } id)
        {
            var found = await purchases.FindAsync(workspaceId, cardId, id, cancellationToken);
            if (found is null) return CreditCardPurchaseResult.NotFound;
            if (!await purchases.CanEditAsync(workspaceId, cardId, id, today, cancellationToken))
                return new(CreditCardPurchaseAccess.Conflict, null, new Dictionary<string, string[]> { ["purchase"] = ["Compra vinculada a fatura fechada, pagamento ou conciliação. Histórico protegido."] });
            purchase = found;
            try { purchase.UpdateSchedule(input.CategoryId!.Value, input.Description!, input.PurchaseDate!.Value, total, input.InstallmentCount!.Value, input.PreviouslyPaidInstallments, input.FirstPendingCompetence, userId, timeProvider.GetUtcNow(), input.ExpenseNature is null ? null : nature); }
            catch (InvalidOperationException ex) { return new(CreditCardPurchaseAccess.Conflict, null, new Dictionary<string, string[]> { ["purchase"] = [ex.Message] }); }
        }
        else
        {
            purchase = FinancialCreditCardPurchase.CreateOngoing(workspaceId, cardId, input.CategoryId!.Value, input.Description!, input.PurchaseDate!.Value, total, input.InstallmentCount!.Value, input.PreviouslyPaidInstallments, input.FirstPendingCompetence, userId, timeProvider.GetUtcNow(), nature);
            await purchases.AddAsync(purchase, cancellationToken);
        }
        foreach (var installment in purchase.Installments.Where(i => i.Status == CreditCardInstallmentStatus.Active))
        {
            var cycle = Cycle(purchase, installment, calendar);
            var invoice = await purchases.FindInvoiceAsync(workspaceId, cardId, cycle.ClosingDate, cancellationToken);
            if (invoice is not null)
            {
                var details = await purchases.GetInvoiceAsync(workspaceId, cardId, invoice.Id, today, cancellationToken);
                if (details is not null && (details.Status == FinancialCreditCardInvoiceStatus.Closed || details.PaidAmount != "0.00"))
                    return new(CreditCardPurchaseAccess.Conflict, null, new Dictionary<string, string[]> { ["purchase"] = ["A fatura de destino está fechada ou possui pagamentos."] });
            }
            if (invoice is null)
            {
                invoice = FinancialCreditCardInvoice.Create(workspaceId, cardId, cycle.Competence, cycle.CycleStartDate, cycle.ClosingDate, cycle.DueDate, timeProvider.GetUtcNow());
                await purchases.AddInvoiceAsync(invoice, cancellationToken);
            }
            installment.AssignInvoice(invoice.Id);
        }
        await purchases.SaveChangesAsync(cancellationToken);
        return CreditCardPurchaseResult.Ok((await purchases.GetAsync(workspaceId, cardId, purchase.Id, cancellationToken))!);
    }

    /// <summary>
    /// Corrige só o tipo de despesa (fixa/variável/não definida) da compra e, por herança, de todas as suas parcelas. Vale mesmo com a fatura fechada ou paga:
    /// valor, parcelas, fatura e categoria não mudam. Compra cancelada não é editável.
    /// </summary>
    public async Task<CreditCardPurchaseResult> ChangeNatureAsync(Guid workspaceId, Guid cardId, Guid purchaseId, Guid userId, string? expenseNature, CancellationToken cancellationToken)
    {
        if (!await IsParticipant(workspaceId, userId, cancellationToken) || await purchases.FindAsync(workspaceId, cardId, purchaseId, cancellationToken) is not { } purchase) return CreditCardPurchaseResult.NotFound;
        var nature = ExpenseNature.Undefined;
        string? error = null;
        if (expenseNature is null || !ExpenseNatures.TryResolve(expenseNature, CategoryType.Expense, out nature, out error))
            return CreditCardPurchaseResult.Invalid(new Dictionary<string, string[]> { ["expenseNature"] = [error ?? "Informe o tipo de despesa."] });
        if (purchase.Status != CreditCardPurchaseStatus.Active)
            return new(CreditCardPurchaseAccess.Conflict, null, new Dictionary<string, string[]> { ["purchase"] = ["Compra cancelada não pode ser editada."] });
        purchase.ChangeNature(nature, userId, timeProvider.GetUtcNow());
        await purchases.SaveChangesAsync(cancellationToken);
        return CreditCardPurchaseResult.Ok((await purchases.GetAsync(workspaceId, cardId, purchaseId, cancellationToken))!);
    }

    public async Task<IReadOnlyList<FinancialCreditCardPurchaseDto>?> ListAsync(Guid workspaceId, Guid cardId, Guid userId, DateOnly? from, DateOnly? through, CancellationToken cancellationToken) =>
        await IsParticipant(workspaceId, userId, cancellationToken) ? await purchases.ListAsync(workspaceId, cardId, from, through, cancellationToken) : null;

    public async Task<FinancialCreditCardPurchaseDto?> GetAsync(Guid workspaceId, Guid cardId, Guid purchaseId, Guid userId, CancellationToken cancellationToken) =>
        await IsParticipant(workspaceId, userId, cancellationToken) ? await purchases.GetAsync(workspaceId, cardId, purchaseId, cancellationToken) : null;

    public async Task<bool> CancelAsync(Guid workspaceId, Guid cardId, Guid purchaseId, Guid userId, CancellationToken cancellationToken)
        => await purchases.InWorkspaceTransactionAsync(workspaceId, () => CancelCoreAsync(workspaceId, cardId, purchaseId, userId, cancellationToken), cancellationToken);

    private async Task<bool> CancelCoreAsync(Guid workspaceId, Guid cardId, Guid purchaseId, Guid userId, CancellationToken cancellationToken)
    {
        if (!await IsParticipant(workspaceId, userId, cancellationToken) || await purchases.FindAsync(workspaceId, cardId, purchaseId, cancellationToken) is not { } purchase) return false;
        if (!await purchases.CanCancelAsync(workspaceId, cardId, purchaseId, cancellationToken)) return false;
        purchase.Cancel(userId, timeProvider.GetUtcNow());
        await purchases.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<FinancialCreditCardInvoiceDto>?> ListInvoicesAsync(Guid workspaceId, Guid cardId, Guid userId, string from, string through, DateOnly today, CancellationToken cancellationToken) =>
        await IsParticipant(workspaceId, userId, cancellationToken) && await purchases.GetCardCalendarAsync(workspaceId, cardId, cancellationToken) is not null
            ? await purchases.ListInvoicesAsync(workspaceId, cardId, from, through, today, cancellationToken) : null;

    public async Task<FinancialCreditCardInvoiceDto?> GetInvoiceAsync(Guid workspaceId, Guid cardId, Guid invoiceId, Guid userId, DateOnly today, CancellationToken cancellationToken) =>
        await IsParticipant(workspaceId, userId, cancellationToken) ? await purchases.GetInvoiceAsync(workspaceId, cardId, invoiceId, today, cancellationToken) : null;

    public async Task<IReadOnlyList<FinancialCreditCardInvoiceDto>?> ProjectInvoicesAsync(Guid workspaceId, Guid cardId, Guid userId, string from, int months, DateOnly today, CancellationToken cancellationToken) =>
        await IsParticipant(workspaceId, userId, cancellationToken) ? await purchases.ProjectInvoicesAsync(workspaceId, cardId, from, months, today, cancellationToken) : null;

    public async Task<FinancialCreditCardLimitDto?> GetLimitAsync(Guid workspaceId, Guid cardId, Guid userId, CancellationToken cancellationToken) =>
        await IsParticipant(workspaceId, userId, cancellationToken) ? await purchases.GetLimitAsync(workspaceId, cardId, cancellationToken) : null;

    public async Task<CreditCardPortfolioDto?> GetPortfolioAsync(Guid workspaceId, Guid userId, string competence, DateOnly today, CancellationToken ct) =>
        await IsParticipant(workspaceId, userId, ct) ? await purchases.GetPortfolioAsync(workspaceId, competence, today, ct) : null;

    private async Task<bool> IsParticipant(Guid workspaceId, Guid userId, CancellationToken cancellationToken) => await workspaces.GetOwnerRoleAsync(workspaceId, userId, cancellationToken) is not null;
}

public sealed record CreditCardPurchasePreview(string TotalAmount, int InstallmentCount, int PreviouslyPaidInstallments, string RemainingAmount, IReadOnlyList<CreditCardPurchasePreviewRow> Installments);
public sealed record CreditCardPurchasePreviewRow(int Number, string Amount, string Competence, DateOnly DueDate);
