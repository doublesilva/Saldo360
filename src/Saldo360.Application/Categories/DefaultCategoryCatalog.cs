using Saldo360.Domain.Categories;

namespace Saldo360.Application.Categories;

/// <summary>Categoria do catálogo padrão com as suas subcategorias (no máximo dois níveis, como no cadastro).</summary>
public sealed record DefaultCategoryDefinition(string Name, CategoryType Type, IReadOnlyList<string> Subcategories);

/// <summary>
/// Catálogo inicial de categorias. É só uma fonte de dados: cada orçamento recebe cópias próprias e editáveis, então alterar o catálogo vale para
/// novos orçamentos (ou para a ação explícita "Adicionar categorias padrão") e nunca sobrescreve personalizações existentes.
/// </summary>
public interface IDefaultCategoryCatalog
{
    /// <summary>Sobe a cada mudança do catálogo; gravada no orçamento na inicialização.</summary>
    int Version { get; }

    IReadOnlyList<DefaultCategoryDefinition> Categories { get; }
}

/// <summary>Sem categorias: usado onde a inicialização automática não deve criar nada (testes que montam o próprio cadastro).</summary>
public sealed class EmptyDefaultCategoryCatalog : IDefaultCategoryCatalog
{
    public static EmptyDefaultCategoryCatalog Instance { get; } = new();

    public int Version => 0;

    public IReadOnlyList<DefaultCategoryDefinition> Categories { get; } = [];
}

/// <summary>Catálogo vigente: 10 categorias de despesa e 4 de receita. Sem categorias para bancos, cartões, pessoas, PF/PJ ou projetos.</summary>
public sealed class StandardDefaultCategoryCatalog : IDefaultCategoryCatalog
{
    public static StandardDefaultCategoryCatalog Instance { get; } = new();

    public int Version => 1;

    public IReadOnlyList<DefaultCategoryDefinition> Categories { get; } =
    [
        Expense("Alimentação", "Mercado e feira", "Refeições fora e delivery"),
        Expense("Moradia", "Contas da casa", "Aluguel e financiamento", "Manutenção e reformas", "Móveis e decoração"),
        Expense("Transporte", "Deslocamentos", "Manutenção do veículo", "Financiamento do veículo"),
        Expense("Saúde e bem-estar", "Atendimento e plano de saúde", "Farmácia e cuidados pessoais", "Esportes e atividades físicas"),
        Expense("Educação", "Escola e materiais", "Cursos e formação"),
        Expense("Lazer e compras pessoais", "Passeios e viagens", "Assinaturas e clubes", "Roupas e compras pessoais", "Presentes e festas"),
        Expense("Tecnologia e comunicação", "Internet e telefonia", "Equipamentos", "Software e serviços digitais"),
        Expense("Impostos e seguros", "Impostos PF", "Impostos PJ", "Seguros"),
        Expense("Serviços profissionais", "Contabilidade", "Outros serviços profissionais"),
        Expense("Encargos financeiros", "Juros e multas", "IOF e tarifas"),
        Income("Salários e benefícios", "Salário", "Extras e benefícios"),
        Income("Prestação de serviços", "Serviços recorrentes", "Trabalhos avulsos"),
        Income("Rendimentos", "Rendimentos financeiros"),
        Income("Outras receitas", "Venda de bens", "Recebimentos extraordinários"),
    ];

    private static DefaultCategoryDefinition Expense(string name, params string[] subcategories) => new(name, CategoryType.Expense, subcategories);

    private static DefaultCategoryDefinition Income(string name, params string[] subcategories) => new(name, CategoryType.Income, subcategories);
}
