namespace Saldo360.Application.CreditCards.Projection;

/// <summary>
/// Compartilha, dentro de uma janela explícita da requisição, a leitura dos fatos de TODOS os cartões do orçamento (faturas, parcelas, ajustes, pagamentos e demais fatos de <see cref="ICardProjectionRepository.LoadAsync"/>).
/// Essa leitura não depende da conta pedida: cada chamador recorta o pagador em memória depois. Fora da janela, ou com <see cref="Enabled"/> falso, cada chamada lê do banco como sempre.
/// Uso do cálculo PF, que repete o Plano × Realizado de cada conta PF na mesma requisição. Execução sequencial; nada é gravado.
/// </summary>
public interface ICardFactsSharing
{
    /// <summary>Falso = caminho original (uma leitura por chamada). Existe para comparar os dois caminhos e para desligar o reaproveitamento.</summary>
    bool Enabled { get; set; }
    /// <summary>Abre a janela (pode ser aninhada); ao fechar a mais externa, o conteúdo é descartado.</summary>
    IDisposable Begin();
    /// <summary>Descarta o conteúdo da janela: a próxima leitura volta ao banco (por exemplo, quando o retrato da requisição mudou e será refeito).</summary>
    void Invalidate();
    /// <summary>Leituras que foram ao banco e leituras atendidas pela janela, desde a criação do escopo (diagnóstico e testes).</summary>
    (int Database, int Reused) Reads { get; }
}
