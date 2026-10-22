using System.Globalization;
using System.Text.Json;
using Saldo360.Domain.Common;

namespace Saldo360.Application.Bootstrap;

/// <summary>Explicit source snapshot conversion, never recomputed from transactions. Raw lexical value stays intact.</summary>
public sealed record ApprovedRecoverySnapshot(decimal Amount, string MetadataJson)
{
    public static ApprovedRecoverySnapshot FromRaw(string raw)
    {
        if (!decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || value < 0)
            throw new ArgumentException("Snapshot de recuperação inválido.");
        var amount = decimal.Round(value, 2, MidpointRounding.AwayFromZero);
        if (Money.Validate(amount, "Recuperação") is not null) throw new ArgumentException("Snapshot fora do limite monetário.");
        return new(amount, JsonSerializer.Serialize(new { sourceRaw = raw, rounding = "AwayFromZero", scale = 2 }));
    }
}
