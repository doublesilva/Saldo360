using Saldo360.Domain.Common;

namespace Saldo360.Domain.Workspaces;

public enum FinancialWorkspaceType { Personal, Shared }
public enum FinancialWorkspaceRole { Owner }

public sealed class FinancialWorkspace : Entity
{
    private FinancialWorkspace() { Name = string.Empty; }
    public Guid OwnerUserId { get; private init; }
    public string Name { get; private set; }
    public FinancialWorkspaceType Type { get; private init; }
    public bool IsActive { get; private set; } = true;
    public DateTimeOffset CreatedAt { get; private init; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public uint Version { get; private set; }

    public static FinancialWorkspace CreatePersonal(Guid owner, DateTimeOffset now)
    {
        if (owner == Guid.Empty) throw new ArgumentException("Proprietário obrigatório.");
        return new() { OwnerUserId = owner, Name = "Meu orçamento", Type = FinancialWorkspaceType.Personal, CreatedAt = now, UpdatedAt = now };
    }
}
