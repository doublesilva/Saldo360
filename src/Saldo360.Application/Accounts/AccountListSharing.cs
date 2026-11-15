namespace Saldo360.Application.Accounts;

/// <summary>
/// Compartilha a listagem completa e ordenada de contas somente dentro de uma janela explícita da requisição.
/// Os consumidores derivam contas ativas, PF, principal e conta selecionada da mesma projeção; leituras de saldo,
/// abertura e movimentos continuam independentes.
/// </summary>
public interface IAccountListSharing
{
    /// <summary>Falso restaura o caminho original e existe para comparar os dois caminhos no mesmo banco.</summary>
    bool Enabled { get; set; }
    /// <summary>Abre a janela de leitura. Janelas aninhadas compartilham o mesmo conteúdo.</summary>
    IDisposable Begin();
    /// <summary>Descarta o retrato compartilhado; a próxima listagem completa volta ao banco.</summary>
    void Invalidate();
    /// <summary>Listagens completas que foram ao banco e derivações atendidas pela janela.</summary>
    (int Database, int Reused) Reads { get; }
}
