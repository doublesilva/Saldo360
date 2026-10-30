using System.Globalization;
using Saldo360.Domain.Common;
using Saldo360.Domain.Planning;

namespace Saldo360.Application.MonthlyPlan;

public sealed partial class MonthlyPlanService
{
    // ---- Calendário de dias úteis e receita por hora -----------------------------------------------------------------------------------------
    // O cálculo é sempre do backend: o navegador apenas pede e exibe. Dia útil = segunda a sexta que não esteja cadastrado como feriado ou dia não trabalhado.

    public async Task<MonthlyPlanResult<WorkingDaysDto>> WorkingDaysAsync(Guid workspaceId, Guid userId, string? competence, CancellationToken ct)
    {
        if (!await IsParticipantAsync(workspaceId, userId, ct)) return MonthlyPlanResult<WorkingDaysDto>.NotFound;
        var month = new DateOnly(Today.Year, Today.Month, 1);
        if (!string.IsNullOrWhiteSpace(competence) && !TryMonth(competence, out month)) return MonthlyPlanResult<WorkingDaysDto>.Invalid("competence", "Use uma competência válida no formato yyyy-MM.");
        var days = await repository.ListNonWorkingDaysAsync(workspaceId, month, month.AddMonths(1).AddDays(-1), ct);
        var counts = MonthlyPlanCalculator.CountDays(month, days.Select(d => d.Date).ToHashSet());
        return MonthlyPlanResult<WorkingDaysDto>.Ok(new(Key(month), counts.CalendarDays, counts.Weekdays, counts.NonWorkingWeekdays, counts.BusinessDays,
            days.Select(d => new NonWorkingDayDto(d.Id, d.Date, d.Kind.ToString(), d.Description, d.Date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)).ToList()));
    }

    /// <summary>Calcula o valor esperado de uma receita por hora sem gravar nada (pré-visualização do formulário).</summary>
    public async Task<MonthlyPlanResult<HourlyIncomeDto>> HourlyIncomeAsync(Guid workspaceId, Guid userId, string? competence, string? hourlyRate, string? hoursPerDay, int? billableDays, CancellationToken ct)
    {
        if (!await IsParticipantAsync(workspaceId, userId, ct)) return MonthlyPlanResult<HourlyIncomeDto>.NotFound;
        if (!TryMonth(competence, out var month)) return MonthlyPlanResult<HourlyIncomeDto>.Invalid("competence", "Use uma competência válida no formato yyyy-MM.");
        decimal? rate = TryDecimal(hourlyRate, out var parsedRate) ? parsedRate : null;
        decimal? hours = TryDecimal(hoursPerDay, out var parsedHours) ? parsedHours : null;
        var business = MonthlyPlanCalculator.CountDays(month, await NonWorkingAsync(workspaceId, month, ct)).BusinessDays;
        var errors = MonthlyPlanCalculator.ValidateHourly(rate, hours, billableDays, business);
        if (errors.Count > 0) return MonthlyPlanResult<HourlyIncomeDto>.Invalid(errors);
        var amount = MonthlyPlanCalculator.HourlyAmount(rate!.Value, hours!.Value, billableDays!.Value);
        if (Money.Validate(amount, "O valor esperado") is { } error) return MonthlyPlanResult<HourlyIncomeDto>.Invalid("hourlyRate", error);
        return MonthlyPlanResult<HourlyIncomeDto>.Ok(new(Key(month), MonthlyPlanCalculator.Cents(rate.Value), hours.Value.ToString("0.##", CultureInfo.InvariantCulture), billableDays.Value, business, MonthlyPlanCalculator.Cents(amount)));
    }

    public async Task<MonthlyPlanResult<NonWorkingDayDto>> AddNonWorkingDayAsync(Guid workspaceId, Guid userId, NonWorkingDayRequest request, CancellationToken ct)
    {
        if (!await IsParticipantAsync(workspaceId, userId, ct)) return MonthlyPlanResult<NonWorkingDayDto>.NotFound;
        if (request.Date is not { } date || date.Year is < 2000 or > 2100) return MonthlyPlanResult<NonWorkingDayDto>.Invalid("date", "Informe uma data entre 2000 e 2100.");
        if (!TryEnum<NonWorkingDayKind>(request.Kind, out var kind)) return MonthlyPlanResult<NonWorkingDayDto>.Invalid("kind", "Informe o tipo: Holiday ou NonWorking.");
        var description = request.Description?.Trim() ?? string.Empty;
        if (description.Length is 0 or > FinancialNonWorkingDay.DescriptionMaxLength) return MonthlyPlanResult<NonWorkingDayDto>.Invalid("description", $"Informe a descrição, com até {FinancialNonWorkingDay.DescriptionMaxLength} caracteres.");
        var day = FinancialNonWorkingDay.Create(workspaceId, date, kind, description, userId, time.GetUtcNow());
        if (!await repository.AddNonWorkingDayAsync(day, ct)) return MonthlyPlanResult<NonWorkingDayDto>.Conflict("Esta data já está cadastrada no calendário.");
        return MonthlyPlanResult<NonWorkingDayDto>.Ok(new(day.Id, day.Date, day.Kind.ToString(), day.Description, day.Date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday));
    }

    public async Task<MonthlyPlanResult<WorkingDaysDto>> RemoveNonWorkingDayAsync(Guid workspaceId, Guid userId, Guid id, string? competence, CancellationToken ct)
    {
        if (!await IsParticipantAsync(workspaceId, userId, ct) || !await repository.RemoveNonWorkingDayAsync(workspaceId, id, ct)) return MonthlyPlanResult<WorkingDaysDto>.NotFound;
        return await WorkingDaysAsync(workspaceId, userId, competence, ct);
    }
}
