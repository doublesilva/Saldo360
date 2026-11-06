namespace Saldo360.Application.Budgets;

/// <summary>Leitura somente leitura das camadas que o resolvedor combina (alterações de regras, registros mensais históricos e total agregado). Nunca grava.</summary>
public interface IBudgetLayerReader
{
    /// <summary>Camadas das competências em [from, until). Padrões com vigência anterior a <paramref name="from"/> são incluídos (valem para o intervalo).</summary>
    Task<BudgetLayers> ReadAsync(Guid workspaceId, DateOnly from, DateOnly until, CancellationToken cancellationToken);
}
