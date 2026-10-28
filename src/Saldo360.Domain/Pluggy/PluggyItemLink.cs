using System.Text.RegularExpressions;
using Saldo360.Domain.Common;

namespace Saldo360.Domain.Pluggy;

/// <summary>
/// Vínculo local entre um Item da Pluggy (obtido no Dashboard) e o orçamento/usuário autorizados.
/// Não guarda credenciais bancárias nem dados financeiros: a POC é somente leitura.
/// </summary>
public sealed class PluggyItemLink : Entity
{
    public const int LabelMaxLength = 120;
    private PluggyItemLink() { PluggyItemId = string.Empty; }
    public Guid WorkspaceId { get; private init; }
    public Guid UserId { get; private init; }
    public string PluggyItemId { get; private init; }
    public string? Label { get; private init; }
    public DateTimeOffset CreatedAt { get; private init; }

    public static bool IsValidItemId(string? value) =>
        value is { Length: 36 } && Regex.IsMatch(value, "^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$");

    public static PluggyItemLink Create(Guid workspaceId, Guid userId, string pluggyItemId, string? label, DateTimeOffset now)
    {
        if (workspaceId == Guid.Empty || userId == Guid.Empty) throw new ArgumentException("Orçamento ou usuário inválido.");
        if (!IsValidItemId(pluggyItemId?.Trim())) throw new ArgumentException("Identificador de Item inválido.");
        label = string.IsNullOrWhiteSpace(label) ? null : label.Trim();
        if (label is not null && (label.Length > LabelMaxLength || label.Any(char.IsControl))) throw new ArgumentException("Rótulo inválido.");
        return new() { WorkspaceId = workspaceId, UserId = userId, PluggyItemId = pluggyItemId!.Trim().ToLowerInvariant(), Label = label, CreatedAt = now };
    }
}
