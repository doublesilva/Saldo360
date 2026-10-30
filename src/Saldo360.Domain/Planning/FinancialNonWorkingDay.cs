using Saldo360.Domain.Common;

namespace Saldo360.Domain.Planning;

public enum NonWorkingDayKind { Holiday, NonWorking }

/// <summary>
/// T10.2: dia que o usuário cadastrou como feriado ou dia não trabalhado, para o cálculo de dias úteis da receita por hora. O calendário é configurável
/// e começa vazio: o sistema não presume feriados. Dia útil é segunda a sexta que não esteja neste cadastro; ainda assim nem todo dia útil é faturável,
/// e a quantidade de dias faturáveis é informada na linha de receita.
/// </summary>
public sealed class FinancialNonWorkingDay : Entity
{
    public const int DescriptionMaxLength = 100;

    private FinancialNonWorkingDay() { Description = string.Empty; }

    public Guid WorkspaceId { get; private init; }
    public DateOnly Date { get; private init; }
    public NonWorkingDayKind Kind { get; private init; }
    public string Description { get; private init; }
    public DateTimeOffset CreatedAt { get; private init; }
    public Guid CreatedByUserId { get; private init; }

    public static FinancialNonWorkingDay Create(Guid workspaceId, DateOnly date, NonWorkingDayKind kind, string description, Guid userId, DateTimeOffset now)
    {
        if (workspaceId == Guid.Empty || userId == Guid.Empty) throw new ArgumentException("Orçamento ou usuário inválido.");
        if (!Enum.IsDefined(kind)) throw new ArgumentException("Tipo inválido.", nameof(kind));
        var text = (description ?? string.Empty).Trim();
        if (text.Length is 0 or > DescriptionMaxLength) throw new ArgumentException("Descrição inválida.", nameof(description));
        return new() { WorkspaceId = workspaceId, Date = date, Kind = kind, Description = text, CreatedAt = now, CreatedByUserId = userId };
    }
}
