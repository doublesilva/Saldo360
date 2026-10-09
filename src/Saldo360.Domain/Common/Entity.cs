namespace Saldo360.Domain.Common;

/// <summary>
/// Base para as entidades do domínio, identificadas por um <see cref="Guid"/>.
/// </summary>
public abstract class Entity
{
    public Guid Id { get; protected init; } = Guid.CreateVersion7();
}
