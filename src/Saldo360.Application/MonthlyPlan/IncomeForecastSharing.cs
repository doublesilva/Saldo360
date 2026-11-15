namespace Saldo360.Application.MonthlyPlan;

/// <summary>
/// Compartilha previsões de receita imutáveis somente dentro de uma janela explícita de leitura.
/// Recortes iguais podem ser reaproveitados na mesma fronteira de consistência; snapshots transacionais
/// usam fronteiras próprias e nunca recebem um resultado produzido por outra transação.
/// </summary>
public interface IIncomeForecastSharing
{
    /// <summary>Falso restaura o caminho original para comparação no mesmo banco.</summary>
    bool Enabled { get; set; }
    /// <summary>Abre a janela da leitura pública. Janelas aninhadas compartilham o mesmo conteúdo.</summary>
    IDisposable Begin();
    /// <summary>Descarta todos os resultados da leitura corrente.</summary>
    void Invalidate();
    /// <summary>Previsões carregadas e chamadas atendidas pelo compartilhamento.</summary>
    (int Database, int Reused) Reads { get; }
}
