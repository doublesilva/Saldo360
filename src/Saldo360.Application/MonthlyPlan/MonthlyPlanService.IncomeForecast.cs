using System.Globalization;
using Saldo360.Application.Accounts;
using Saldo360.Domain.Accounts;
using Saldo360.Domain.Common;

namespace Saldo360.Application.MonthlyPlan;

public sealed partial class MonthlyPlanService
{
    // ---- Previsão de receita PJ (PROJ-01) ---------------------------------------------------------------------------------------------------------
    // Configuração e ajustes por competência. Nada aqui cria lançamento, previsão avulsa ou transferência: a estimativa só entra no fluxo projetado quando a configuração
    // está ATIVA (aprovada), e é calculada na leitura da projeção.

    private const int ForecastMonths = 6;

    private static string Decimal(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>Conta PJ sugerida para recebimento: a Itaú PJ, ou a primeira conta PJ ativa.</summary>
    private static FinancialAccountDto? DefaultPjAccount(IEnumerable<FinancialAccountDto> all)
    {
        var pj = all.Where(a => a.IsActive && a.FinancialScope == FinancialScope.PJ).ToList();
        return pj.FirstOrDefault(a => a.Name.Contains("Itaú", StringComparison.OrdinalIgnoreCase)) ?? pj.FirstOrDefault();
    }

    public async Task<MonthlyPlanResult<IncomeForecastSettingsDto>> IncomeForecastAsync(Guid workspaceId, Guid userId, string? from, CancellationToken ct)
    {
        if (!await IsParticipantAsync(workspaceId, userId, ct)) return MonthlyPlanResult<IncomeForecastSettingsDto>.NotFound;
        var first = new DateOnly(Today.Year, Today.Month, 1);
        if (!string.IsNullOrWhiteSpace(from) && !TryMonth(from, out first)) return MonthlyPlanResult<IncomeForecastSettingsDto>.Invalid("from", "Use uma competência válida no formato yyyy-MM.");
        return MonthlyPlanResult<IncomeForecastSettingsDto>.Ok(await BuildForecastDtoAsync(workspaceId, first, ct));
    }

    private async Task<IncomeForecastSettingsDto> BuildForecastDtoAsync(Guid workspaceId, DateOnly first, CancellationToken ct)
    {
        var all = await accounts.ListAsync(workspaceId, true, ct);
        var options = all.Where(a => a.IsActive).Select(a => new IncomeForecastAccountOptionDto(a.Id, a.Name, a.FinancialScope.ToString())).ToList();
        var view = await repository.LoadIncomeForecastAsync(workspaceId, first, first.AddMonths(ForecastMonths).AddDays(-1), Today, ct);
        if (view.Settings is not { } s)
        {
            var suggested = DefaultPjAccount(all);
            return new(false, false, suggested?.Id, suggested?.Name, "100.00", "8", 10, 1, "11.00", "0.00", [], options);
        }
        var months = view.Occurrences.OrderBy(o => o.PaymentDate).Select(o => new IncomeForecastMonthDto(Key(o.WorkedMonth), o.PaymentDate, o.BusinessDays, o.BillableDays, Decimal(o.GrossAmount),
            Decimal(o.TaxReserve), o.Status, Decimal(o.RealizedAmount), o.DaysAdjusted, o.AmountAdjusted, o.DateAdjusted)).ToList();
        return new(true, s.IsActive, s.AccountId, all.FirstOrDefault(a => a.Id == s.AccountId)?.Name, Decimal(s.HourlyRate), s.HoursPerDay.ToString("0.##", CultureInfo.InvariantCulture),
            s.PaymentDay, s.PaymentOffsetMonths, Decimal(s.TaxReservePercent), Decimal(s.OperationalReserve), months, options);
    }

    public async Task<MonthlyPlanResult<IncomeForecastSettingsDto>> SaveIncomeForecastAsync(Guid workspaceId, Guid userId, IncomeForecastRequest request, CancellationToken ct)
    {
        if (!await IsParticipantAsync(workspaceId, userId, ct)) return MonthlyPlanResult<IncomeForecastSettingsDto>.NotFound;
        var errors = new Dictionary<string, string[]>();
        var all = await accounts.ListAsync(workspaceId, true, ct);
        if (request.AccountId is not { } accountId || all.FirstOrDefault(a => a.Id == accountId) is not { IsActive: true }) errors["accountId"] = ["Informe uma conta ativa para o recebimento."];
        decimal rate = 0m, hours = 0m, tax = 0m, reserve = 0m;
        if (!TryDecimal(request.HourlyRate, out rate) || rate <= 0m) errors["hourlyRate"] = ["Informe o valor por hora, maior que zero."];
        else if (Money.Validate(rate, "O valor por hora") is { } rateError) errors["hourlyRate"] = [rateError];
        if (!TryDecimal(request.HoursPerDay, out hours) || hours <= 0m || hours > 24m || decimal.Round(hours, 2) != hours) errors["hoursPerDay"] = ["Informe as horas por dia, maiores que zero e até 24, com no máximo duas casas."];
        if (request.PaymentDay is not (>= 1 and <= 31)) errors["paymentDay"] = ["Informe o dia esperado de pagamento, de 1 a 31."];
        if (request.PaymentOffsetMonths is not (>= 0 and <= 3)) errors["paymentOffsetMonths"] = ["Informe o deslocamento do recebimento, de 0 a 3 meses."];
        if (!TryDecimal(request.TaxReservePercent, out tax) || tax < 0m || tax > 100m || decimal.Round(tax, 2) != tax) errors["taxReservePercent"] = ["Informe a reserva de tributos, de 0 a 100%, com no máximo duas casas."];
        if (!TryDecimal(request.OperationalReserve ?? "0", out reserve) || reserve < 0m) errors["operationalReserve"] = ["Informe a reserva operacional, zero ou maior."];
        else if (Money.Validate(reserve, "A reserva operacional") is { } reserveError) errors["operationalReserve"] = [reserveError];
        if (errors.Count > 0) return MonthlyPlanResult<IncomeForecastSettingsDto>.Invalid(errors);

        await repository.UpsertIncomeForecastAsync(workspaceId, new(request.AccountId!.Value, rate, hours, request.PaymentDay!.Value, request.PaymentOffsetMonths!.Value, tax, reserve, request.IsActive),
            userId, time.GetUtcNow(), ct);
        return MonthlyPlanResult<IncomeForecastSettingsDto>.Ok(await BuildForecastDtoAsync(workspaceId, new DateOnly(Today.Year, Today.Month, 1), ct));
    }

    public async Task<MonthlyPlanResult<IncomeForecastSettingsDto>> SaveForecastMonthAsync(Guid workspaceId, Guid userId, string? workedCompetence, IncomeForecastMonthRequest request, CancellationToken ct)
    {
        if (!await IsParticipantAsync(workspaceId, userId, ct)) return MonthlyPlanResult<IncomeForecastSettingsDto>.NotFound;
        if (!TryMonth(workedCompetence, out var month)) return MonthlyPlanResult<IncomeForecastSettingsDto>.Invalid("competence", "Use uma competência válida no formato yyyy-MM.");
        var errors = new Dictionary<string, string[]>();
        decimal? amount = null;
        if (!string.IsNullOrWhiteSpace(request.ExpectedAmount))
        {
            if (!TryDecimal(request.ExpectedAmount, out var parsed) || parsed < 0m) errors["expectedAmount"] = ["Informe o valor esperado, zero ou maior."];
            else if (Money.Validate(parsed, "O valor esperado") is { } amountError) errors["expectedAmount"] = [amountError];
            else amount = parsed;
        }
        if (request.BillableDays is < 0 or > 31 || request.BillableDays > DateTime.DaysInMonth(month.Year, month.Month)) errors["billableDays"] = ["Informe os dias faturáveis, dentro dos dias do mês."];
        if (request.PaymentDate is { } date && date.Year is < 2000 or > 2100) errors["paymentDate"] = ["Informe uma data de recebimento válida."];
        if (errors.Count > 0) return MonthlyPlanResult<IncomeForecastSettingsDto>.Invalid(errors);
        var view = await repository.LoadIncomeForecastAsync(workspaceId, month, month, Today, ct);
        if (view.Settings is null) return MonthlyPlanResult<IncomeForecastSettingsDto>.Conflict("Salve a configuração da previsão antes de ajustar uma competência.");

        await repository.UpsertForecastMonthAsync(workspaceId, new(month, request.BillableDays, amount, request.PaymentDate, request.IsDismissed), userId, time.GetUtcNow(), ct);
        return MonthlyPlanResult<IncomeForecastSettingsDto>.Ok(await BuildForecastDtoAsync(workspaceId, new DateOnly(Today.Year, Today.Month, 1), ct));
    }

    public async Task<MonthlyPlanResult<IncomeForecastSettingsDto>> RemoveForecastMonthAsync(Guid workspaceId, Guid userId, string? workedCompetence, CancellationToken ct)
    {
        if (!await IsParticipantAsync(workspaceId, userId, ct)) return MonthlyPlanResult<IncomeForecastSettingsDto>.NotFound;
        if (!TryMonth(workedCompetence, out var month)) return MonthlyPlanResult<IncomeForecastSettingsDto>.Invalid("competence", "Use uma competência válida no formato yyyy-MM.");
        if (!await repository.RemoveForecastMonthAsync(workspaceId, month, ct)) return MonthlyPlanResult<IncomeForecastSettingsDto>.NotFound;
        return MonthlyPlanResult<IncomeForecastSettingsDto>.Ok(await BuildForecastDtoAsync(workspaceId, new DateOnly(Today.Year, Today.Month, 1), ct));
    }
}
