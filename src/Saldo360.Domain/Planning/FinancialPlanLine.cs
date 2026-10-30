using Saldo360.Domain.Common;

namespace Saldo360.Domain.Planning;

public enum PlanLineKind { Income, Expense, Transfer }

/// <summary>
/// Natureza da linha de receita ou despesa. <see cref="Reserve"/> (reserva ou provisão) é só despesa: nunca é saída bancária realizada e por isso
/// fica fora da base de consumo comparada com o realizado.
/// </summary>
public enum PlanLineNature { Recurring, Fixed, Variable, Extraordinary, Reserve }

/// <summary>Dados editáveis de uma linha de plano. Nenhum campo é derivado de movimentações: o plano é meta, não lançamento.</summary>
public sealed record PlanLineDetails(
    PlanLineKind Kind, PlanLineNature? Nature, string Description, Guid? AccountId, Guid? DestinationAccountId, Guid? CategoryId, decimal Amount,
    DateOnly? ExpectedDate, decimal? HourlyRate, decimal? HoursPerDay, int? BillableDays);

/// <summary>
/// T10.2: uma linha do planejamento mensal (competência): receita esperada, despesa planejada ou transferência prevista entre contas. É META do orçamento:
/// não gera lançamento, previsão, pagamento nem transferência, e nunca altera saldo. <see cref="OriginalAmount"/> é o valor aprovado na criação e nunca
/// muda; <see cref="Amount"/> é o valor vigente (revisado). A remoção é lógica (arquivamento), e o histórico fica em <see cref="FinancialPlanHistoryEntry"/>.
/// Uma receita por hora guarda também os parâmetros do cálculo (valor/hora, horas por dia e dias faturáveis); o valor esperado é calculado pelo backend.
/// </summary>
public sealed class FinancialPlanLine : Entity
{
    public const int DescriptionMaxLength = 200;

    private FinancialPlanLine() { Description = string.Empty; }

    public Guid WorkspaceId { get; private init; }
    public DateOnly Competence { get; private init; }
    public PlanLineKind Kind { get; private init; }
    public PlanLineNature? Nature { get; private set; }
    public string Description { get; private set; }
    /// <summary>Conta da linha. Nulo = consolidado (vale para a visão de todas as contas). Na transferência é a conta de origem.</summary>
    public Guid? AccountId { get; private set; }
    public Guid? DestinationAccountId { get; private set; }
    public Guid? CategoryId { get; private set; }
    public decimal OriginalAmount { get; private init; }
    public decimal Amount { get; private set; }
    public DateOnly? ExpectedDate { get; private set; }
    public decimal? HourlyRate { get; private set; }
    public decimal? HoursPerDay { get; private set; }
    public int? BillableDays { get; private set; }
    public Guid? CopiedFromLineId { get; private init; }
    public bool IsArchived { get; private set; }
    public DateTimeOffset CreatedAt { get; private init; }
    public Guid CreatedByUserId { get; private init; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public Guid UpdatedByUserId { get; private set; }
    public DateTimeOffset? ArchivedAt { get; private set; }
    public Guid? ArchivedByUserId { get; private set; }

    public bool IsHourly => HourlyRate is not null;

    public static FinancialPlanLine Create(Guid workspaceId, DateOnly competence, PlanLineDetails details, Guid userId, DateTimeOffset now, Guid? copiedFromLineId = null)
    {
        if (workspaceId == Guid.Empty || userId == Guid.Empty) throw new ArgumentException("Orçamento ou usuário inválido.");
        if (competence.Day != 1) throw new ArgumentException("A competência deve ser o primeiro dia do mês.", nameof(competence));
        var line = new FinancialPlanLine
        {
            WorkspaceId = workspaceId, Competence = competence, Kind = details.Kind, OriginalAmount = details.Amount, CopiedFromLineId = copiedFromLineId,
            CreatedAt = now, CreatedByUserId = userId, UpdatedAt = now, UpdatedByUserId = userId,
        };
        line.Apply(details);
        return line;
    }

    /// <summary>Revisa o valor vigente e os dados da linha. O tipo, a competência e o valor original não mudam; linha arquivada não é revisada.</summary>
    public void Revise(PlanLineDetails details, Guid userId, DateTimeOffset now)
    {
        if (IsArchived) throw new InvalidOperationException("Linha de planejamento arquivada não pode ser revisada.");
        if (details.Kind != Kind) throw new ArgumentException("O tipo da linha não pode mudar.", nameof(details));
        Apply(details);
        UpdatedAt = now;
        UpdatedByUserId = userId;
    }

    public bool Archive(Guid userId, DateTimeOffset now)
    {
        if (IsArchived) return false;
        IsArchived = true;
        ArchivedAt = now;
        ArchivedByUserId = userId;
        UpdatedAt = now;
        UpdatedByUserId = userId;
        return true;
    }

    private void Apply(PlanLineDetails d)
    {
        if (string.IsNullOrWhiteSpace(d.Description) || d.Description.Trim().Length > DescriptionMaxLength) throw new ArgumentException("Descrição inválida.");
        if (d.Amount <= 0m || Money.Validate(d.Amount, "O valor") is not null) throw new ArgumentException("Valor inválido.");
        if (d.ExpectedDate is { } expected && (expected.Year != Competence.Year || expected.Month != Competence.Month)) throw new ArgumentException("A data esperada deve estar na competência.");
        if (d.Kind == PlanLineKind.Transfer)
        {
            if (d.AccountId is null || d.DestinationAccountId is null || d.AccountId == d.DestinationAccountId) throw new ArgumentException("A transferência exige duas contas diferentes.");
            if (d.Nature is not null || d.CategoryId is not null || d.HourlyRate is not null) throw new ArgumentException("A transferência não tem natureza, categoria nem cálculo por hora.");
        }
        else
        {
            if (d.DestinationAccountId is not null) throw new ArgumentException("Somente a transferência tem conta de destino.");
            if (d.Nature is null) throw new ArgumentException("Informe a natureza da linha.");
            if (d.Nature == PlanLineNature.Reserve && d.Kind != PlanLineKind.Expense) throw new ArgumentException("Reserva é somente despesa.");
        }
        var hourly = new object?[] { d.HourlyRate, d.HoursPerDay, d.BillableDays };
        if (hourly.Any(x => x is not null) && (hourly.Any(x => x is null) || d.Kind != PlanLineKind.Income)) throw new ArgumentException("O cálculo por hora é somente de receita e exige os três parâmetros.");
        Nature = d.Nature;
        Description = d.Description.Trim();
        AccountId = d.AccountId;
        DestinationAccountId = d.DestinationAccountId;
        CategoryId = d.CategoryId;
        Amount = d.Amount;
        ExpectedDate = d.ExpectedDate;
        HourlyRate = d.HourlyRate;
        HoursPerDay = d.HoursPerDay;
        BillableDays = d.BillableDays;
    }
}
