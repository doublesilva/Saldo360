using System.Globalization;
using System.Text.RegularExpressions;

namespace Saldo360.Application.CreditCards.Projection;

/// <summary>
/// Representação de apresentação (e de classificação) de uma parcela de cartão: "Mercado Livre — Parcela 2/4". A descrição original do banco NUNCA é alterada nem apagada: ela segue em
/// <see cref="OriginalDescription"/>, junto com a numeração que o banco escreveu na descrição (<see cref="OriginalNumber"/>/<see cref="OriginalTotal"/>) e a usada pelo motor (<see cref="Number"/>/<see cref="Total"/>).
/// Divergência entre as duas é sinalizada, nunca corrigida: uma série ambígua não é renumerada.
/// </summary>
public sealed record InstallmentLabel(string Title, string Display, string OriginalDescription, int? Number, int? Total, int? OriginalNumber, int? OriginalTotal, bool Inconsistent)
{
    private static readonly Regex Marker = new(@"\(?\b(\d{1,3})\s*/\s*(\d{1,3})\b\)?", RegexOptions.CultureInvariant);
    private static readonly Regex Trailing = new(@"\s*\((\d{1,3})\s*/\s*(\d{1,3})\)\s*$", RegexOptions.CultureInvariant);
    private static readonly Regex Wording = new(@"\b(?:parcela|parc|prc)\b\.?\s*$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Dictionary<string, string> Known = new(StringComparer.OrdinalIgnoreCase) { ["mercadolivre"] = "Mercado Livre", ["mercadopago"] = "Mercado Pago" };

    /// <summary>
    /// <paramref name="number"/>/<paramref name="total"/> são os dados estruturados confiáveis (motor, banco). Sem eles, um sufixo final "(n/t)" acrescentado pelo sistema é lido como estruturado e a
    /// numeração que sobra na descrição passa a ser a "original" do banco.
    /// </summary>
    public static InstallmentLabel Build(string? description, int? number, int? total)
    {
        var original = description?.Trim() ?? string.Empty;
        var text = original;
        if (number is null || total is null)
        {
            var system = Trailing.Match(text);
            if (system.Success) { number = int.Parse(system.Groups[1].Value, CultureInfo.InvariantCulture); total = int.Parse(system.Groups[2].Value, CultureInfo.InvariantCulture); }
        }
        text = Trailing.Replace(text, string.Empty);
        // Numeração escrita pelo banco na descrição (a última, quando há mais de uma).
        var banks = Marker.Matches(text);
        int? originalNumber = null, originalTotal = null;
        if (banks.Count > 0 && banks[^1] is var last)
        {
            originalNumber = int.Parse(last.Groups[1].Value, CultureInfo.InvariantCulture); originalTotal = int.Parse(last.Groups[2].Value, CultureInfo.InvariantCulture);
            text = Marker.Replace(text, " ");
        }
        text = Wording.Replace(text, string.Empty);
        var title = Tidy(text);
        var inconsistent = originalNumber is not null && number is not null && (originalNumber != number || originalTotal != total);
        var display = number is { } n && total is { } t ? $"{title} — Parcela {n}/{t}" : title;
        return new(title, display, original, number, total, originalNumber, originalTotal, inconsistent);
    }

    /// <summary>Nome do estabelecimento legível: "Mercadolivre*Mercado" vira "Mercado Livre"; sem sufixo repetido nem asteriscos.</summary>
    private static string Tidy(string text)
    {
        var parts = text.Split('*', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string merchant;
        if (parts.Length == 0) merchant = string.Empty;
        else if (parts.Length == 1) merchant = parts[0];
        else merchant = parts[1].Length > 0 && parts[0].StartsWith(parts[1].Split(' ')[0], StringComparison.OrdinalIgnoreCase) ? parts[0] : string.Join(' ', parts);
        merchant = string.Join(' ', merchant.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (Known.TryGetValue(merchant, out var alias)) return alias;
        var letters = merchant.Where(char.IsLetter).ToList();
        return letters.Count > 0 && (letters.All(char.IsUpper) || letters.All(char.IsLower)) ? CultureInfo.GetCultureInfo("pt-BR").TextInfo.ToTitleCase(merchant.ToLowerInvariant()) : merchant;
    }
}
