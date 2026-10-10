namespace Saldo360.Domain.Common;

/// <summary>
/// Regras monetárias comuns (BRL): centavos exatos, sem arredondamento. Valores com mais de duas casas
/// decimais, inclusive zeros à direita como 1.500, são recusados, não ajustados.
/// </summary>
public static class Money
{
    /// <summary>Casas decimais aceitas (centavos).</summary>
    public const int MaxScale = 2;

    /// <summary>
    /// Limite absoluto (exclusivo): 13 dígitos inteiros + 2 decimais = 15 dígitos significativos, que o
    /// frontend representa sem perda.
    /// </summary>
    public const decimal Limit = 10_000_000_000_000m;

    /// <summary>Mensagem de erro para escala ou limite inválidos, ou null se o valor for aceitável.</summary>
    /// <param name="subject">Sujeito da mensagem, ex.: "O saldo inicial", "O valor".</param>
    public static string? Validate(decimal amount, string subject)
    {
        if (amount.Scale > MaxScale)
        {
            return $"{subject} deve ter no máximo duas casas decimais.";
        }

        return Math.Abs(amount) >= Limit ? $"{subject} deve ter no máximo 13 dígitos antes da vírgula." : null;
    }
}
