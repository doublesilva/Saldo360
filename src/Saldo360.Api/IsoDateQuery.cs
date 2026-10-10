using System.Globalization;

namespace Saldo360.Api;

/// <summary>
/// Datas na query string só no formato yyyy-MM-dd. O binding padrão de <see cref="DateOnly"/> usa a cultura
/// invariante e aceitaria "01/09/2026" como 9 de janeiro, trocando dia e mês em silêncio.
/// </summary>
internal static class IsoDateQuery
{
    public const string Format = "yyyy-MM-dd";

    /// <summary>Ausente → null sem erro; fora do formato ou inexistente → mensagem em <paramref name="errors"/>.</summary>
    public static DateOnly? Parse(string? value, string field, Dictionary<string, string[]> errors)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        if (DateOnly.TryParseExact(value, Format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            return date;
        }

        errors[field] = ["Data inválida. Use o formato yyyy-MM-dd."];
        return null;
    }
}
