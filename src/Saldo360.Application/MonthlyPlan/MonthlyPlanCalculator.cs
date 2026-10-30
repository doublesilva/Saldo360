using Saldo360.Application.Dashboard.PlanActual;
using Saldo360.Domain.Common;
using Saldo360.Domain.Planning;

namespace Saldo360.Application.MonthlyPlan;

/// <summary>
/// Fórmulas do planejamento mensal (T10.2). Tudo é puro (sem banco, rede ou relógio) e é o único lugar onde o planejado é somado:
/// o Dashboard (T10.1), a visão mensal e a de seis meses usam estas mesmas funções, então não há dois totais para o mesmo mês.
/// </summary>
public static class MonthlyPlanCalculator
{
    public static class Sources
    {
        public const string MonthlyPlan = "MonthlyPlan";
    }

    public static class RiskReasons
    {
        public const string NegativeProjectedBalance = "NegativeProjectedBalance";
        public const string CategoryLimitExceeded = "CategoryLimitExceeded";
        public const string ProjectedExpenseAbovePlan = "ProjectedExpenseAbovePlan";
        public const string ResultBelowPlan = "ResultBelowPlan";
        public const string BalanceUnavailable = "BalanceUnavailable";
        public const string IncompleteCoverage = "IncompleteCoverage";
    }

    public static class Pendencies
    {
        public const string MovementsInReview = "MovementsInReview";
        public const string ItemizedAboveLimit = "ItemizedAboveLimit";
        public const string LimitsNotAttributable = "LimitsNotAttributable";
        public const string HourlyIncomeAboveCalendar = "HourlyIncomeAboveCalendar";
        public const string NoPlan = "NoPlan";
        public const string LineOnInactiveAccount = "LineOnInactiveAccount";
    }

    public static string Cents(decimal value) => PlanActualCalculator.Cents(value);
    public static string? CentsOrNull(decimal? value) => value is { } v ? PlanActualCalculator.Cents(v) : null;

    // ---- Planejado ---------------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Totais planejados do mês no escopo. Regras:
    /// <list type="bullet">
    /// <item>Receita = soma das linhas de receita (na conta escolhida: as da conta; em todas as contas: todas, inclusive as sem conta).</item>
    /// <item>Despesa (base de consumo) em todas as contas: por categoria vale o LIMITE quando existir (as linhas da categoria o detalham e não somam de novo);
    /// categorias sem limite e linhas sem categoria somam pelas linhas. Na conta escolhida: só as linhas da conta, pois um limite por categoria é
    /// consolidado (compras de cartão não têm atribuição inequívoca a uma conta) e é sinalizado em <c>LimitsNotAttributable</c>.</item>
    /// <item>Reservas e provisões nunca entram na base de consumo (não são saída bancária): ficam em <c>Reserve</c>.</item>
    /// <item>Transferências planejadas nunca entram em receita nem despesa consolidada.</item>
    /// </list>
    /// Nulo significa "não definido", nunca zero.
    /// </summary>
    public static PlanTotals Planned(PlanFacts facts, Guid? accountId)
    {
        bool InScope(PlanLineFact l) => accountId is null || l.AccountId == accountId;
        var lines = facts.Lines;
        var incomeLines = lines.Where(l => l.Kind == PlanLineKind.Income && InScope(l)).ToList();
        var reserveLines = lines.Where(l => l.Kind == PlanLineKind.Expense && l.Nature == PlanLineNature.Reserve && InScope(l)).ToList();
        var expenseLines = lines.Where(l => l.Kind == PlanLineKind.Expense && l.Nature != PlanLineNature.Reserve && InScope(l)).ToList();
        var transfers = lines.Where(l => l.Kind == PlanLineKind.Transfer && (accountId is null || l.AccountId == accountId || l.DestinationAccountId == accountId)).ToList();

        decimal? expense; var limitsNotAttributable = false;
        if (accountId is null)
        {
            var limited = facts.Limits.Select(l => l.CategoryId).ToHashSet();
            var outside = expenseLines.Where(l => l.CategoryId is not { } c || !limited.Contains(c)).Sum(l => l.Amount);
            expense = facts.Limits.Count == 0 && expenseLines.Count == 0 ? null : facts.Limits.Sum(l => l.Amount) + outside;
        }
        else
        {
            expense = expenseLines.Count == 0 ? null : expenseLines.Sum(l => l.Amount);
            limitsNotAttributable = facts.Limits.Count > 0;
        }
        return new(incomeLines.Count == 0 ? null : incomeLines.Sum(l => l.Amount), incomeLines.Count, expense, expenseLines.Count, reserveLines.Sum(l => l.Amount), reserveLines.Count,
            facts.Limits.Count, limitsNotAttributable, transfers.Count,
            transfers.Where(t => accountId is null || t.AccountId == accountId).Sum(t => t.Amount), transfers.Where(t => accountId is null || t.DestinationAccountId == accountId).Sum(t => t.Amount));
    }

    /// <summary>Receita planejada do indicador: o plano mensal, quando definido; senão as previsões do período (comportamento anterior da T10.1).</summary>
    public static (decimal? Amount, string? Source) PlannedIncome(PlanTotals plan, PlannedFacts planned) =>
        plan.Income is { } income ? (income, Sources.MonthlyPlan) : planned.IncomeCount > 0 ? (planned.Income, "PlannedTransactions") : (null, null);

    /// <summary>
    /// Despesa planejada na base de consumo: só o plano mensal substitui a base anterior quando há linhas de despesa; sem elas valem as metas por categoria
    /// (Budget) e, em todas as contas, a meta agregada. Metas agregadas e por categoria nunca se somam.
    /// </summary>
    public static (decimal? Amount, string? Source) PlannedConsumption(PlanTotals plan, decimal? budgetTarget, decimal? aggregateTarget, bool allAccounts)
    {
        if (plan.ExpenseLines > 0 && plan.Expense is { } expense) return (expense, Sources.MonthlyPlan);
        if (!allAccounts) return (null, null);
        if (budgetTarget is { } budget && budget > 0m) return (budget, "Budget");
        return aggregateTarget is { } aggregate ? (aggregate, "AggregateTarget") : (null, null);
    }

    public static decimal? Result(decimal? income, decimal? expense) => income is { } i && expense is { } e ? i - e : null;

    /// <summary>Soma das linhas detalhadas de uma categoria com limite (em todas as contas), sem reservas.</summary>
    public static decimal Itemized(PlanFacts facts, Guid categoryId) =>
        facts.Lines.Where(l => l.Kind == PlanLineKind.Expense && l.Nature != PlanLineNature.Reserve && l.CategoryId == categoryId).Sum(l => l.Amount);

    // ---- Dias úteis e receita por hora ------------------------------------------------------------------------------------------------------

    /// <summary>Dias do mês: corridos, de segunda a sexta, os de segunda a sexta cadastrados como feriado/não trabalhado e os dias úteis restantes.</summary>
    public static (int CalendarDays, int Weekdays, int NonWorkingWeekdays, int BusinessDays) CountDays(DateOnly month, IReadOnlySet<DateOnly> nonWorking)
    {
        var days = DateTime.DaysInMonth(month.Year, month.Month);
        int weekdays = 0, excluded = 0;
        for (var day = 1; day <= days; day++)
        {
            var date = new DateOnly(month.Year, month.Month, day);
            if (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;
            weekdays++;
            if (nonWorking.Contains(date)) excluded++;
        }
        return (days, weekdays, excluded, weekdays - excluded);
    }

    /// <summary>Valor esperado = valor/hora × horas por dia × dias faturáveis, em centavos exatos. Nem todo dia útil é faturável: os dias são informados.</summary>
    public static decimal HourlyAmount(decimal hourlyRate, decimal hoursPerDay, int billableDays) =>
        decimal.Round(hourlyRate * hoursPerDay * billableDays, Money.MaxScale, MidpointRounding.AwayFromZero);

    public static Dictionary<string, string[]> ValidateHourly(decimal? hourlyRate, decimal? hoursPerDay, int? billableDays, int businessDays)
    {
        var errors = new Dictionary<string, string[]>();
        if (hourlyRate is not { } rate || rate <= 0m) errors["hourlyRate"] = ["Informe o valor por hora, maior que zero."];
        else if (Money.Validate(rate, "O valor por hora") is { } rateError) errors["hourlyRate"] = [rateError];
        if (hoursPerDay is not { } hours || hours <= 0m || hours > 24m) errors["hoursPerDay"] = ["Informe as horas por dia, maiores que zero e até 24."];
        else if (decimal.Round(hours, 2) != hours) errors["hoursPerDay"] = ["As horas por dia devem ter no máximo duas casas decimais."];
        if (billableDays is not { } days || days <= 0) errors["billableDays"] = ["Informe a quantidade prevista de dias faturáveis, maior que zero."];
        else if (days > businessDays) errors["billableDays"] = [$"O mês tem {businessDays} dia(s) útil(eis) no calendário configurado; os dias faturáveis não podem exceder esse total."];
        return errors;
    }

    // ---- Seis meses ----------------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Risco do mês, a partir do que o motor financeiro já decidiu. Alto: saldo projetado negativo na Agenda. Médio: meta por categoria excedida, despesa
    /// projetada acima da planejada ou (no mês corrente) resultado projetado abaixo do planejado. Desconhecido: saldo indisponível ou cobertura incompleta
    /// sem outro sinal (nunca "baixo" sem dados). Baixo: nenhum sinal e cobertura completa. Meses futuros não comparam resultado: a receita ainda não
    /// realizada não é inventada, então o resultado projetado só tem os compromissos conhecidos.
    /// </summary>
    public static (string Level, IReadOnlyList<string> Reasons) Risk(
        string balanceStatus, string periodKind, decimal? plannedExpense, decimal projectedExpense, decimal? plannedResult, decimal projectedResult, bool dataComplete)
    {
        var reasons = new List<string>(); var level = "Low";
        if (balanceStatus == "NegativeProjectedBalance") { level = "High"; reasons.Add(RiskReasons.NegativeProjectedBalance); }
        if (balanceStatus == "AboveTarget") { if (level == "Low") level = "Medium"; reasons.Add(RiskReasons.CategoryLimitExceeded); }
        if (plannedExpense is { } planned && projectedExpense > planned) { if (level == "Low") level = "Medium"; reasons.Add(RiskReasons.ProjectedExpenseAbovePlan); }
        if (periodKind == "Current" && plannedResult is { } result && projectedResult < result) { if (level == "Low") level = "Medium"; reasons.Add(RiskReasons.ResultBelowPlan); }
        if (balanceStatus == "Unavailable") reasons.Add(RiskReasons.BalanceUnavailable);
        if (!dataComplete) reasons.Add(RiskReasons.IncompleteCoverage);
        if (level == "Low" && (balanceStatus == "Unavailable" || !dataComplete)) level = "Unknown";
        return (level, reasons);
    }

    public static int RiskRank(string level) => level switch { "High" => 3, "Medium" => 2, "Unknown" => 1, _ => 0 };

    /// <summary>Cobertura das obrigações conhecidas pelo saldo projetado da Agenda: sem saldo negativo = Covered; negativo = Shortfall; indisponível = Unknown.</summary>
    public static string ObligationsCoverage(decimal? closing, decimal? minimumProjected, DateOnly? firstNegative) =>
        closing is null ? "Unknown" : firstNegative is not null || closing < 0m || minimumProjected < 0m ? "Shortfall" : "Covered";
}
