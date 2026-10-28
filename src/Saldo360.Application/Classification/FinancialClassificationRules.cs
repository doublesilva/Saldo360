using System.Text;
using System.Text.RegularExpressions;
using Saldo360.Domain.Categories;

namespace Saldo360.Application.Classification;

/// <summary>Versões que identificam, na auditoria, qual conjunto de regras produziu uma classificação.</summary>
public static class FinancialClassificationVersions
{
    public const string Rules = "rule:2026-10-28.1";
    public const string Catalog = "pluggy-catalog:2026-10-28.1";
    public const string Prompt = "2026-10-28.1";
    public static string Ai(string model) => $"ai:{model}:{Prompt}";
}

/// <summary>Resultado de uma regra determinística: o movimento não vira consumo categorizado (ou precisa de decisão humana).</summary>
public sealed record FinancialRuleOutcome(string Reason, bool NeedsReview);

/// <summary>
/// Regras do domínio aplicadas ANTES de qualquer categorização de consumo. São estas regras, e não o modelo de linguagem, que decidem o que
/// não é consumo: pagamento de fatura, transferência entre contas do titular, transferência a terceiros e estorno. Só usam campos estruturados
/// (categoria externa, tipo de operação); a descrição nunca decide.
/// </summary>
public static class FinancialClassificationRules
{
    private static readonly HashSet<string> OwnTransferCategories = new(StringComparer.Ordinal) { "same person transfer", "transfer internal", "transfers" };
    private static readonly HashSet<string> ThirdPartyTransferCategories = new(StringComparer.Ordinal)
        { "transfer pix", "transfer ted", "transfer doc", "transfer bank slip", "third party transfer pix", "third party transfer ted", "third party transfer" };

    public static FinancialRuleOutcome? Evaluate(FinancialClassificationInput input)
    {
        var category = FinancialCategoryMatching.Key(input.ExternalCategory);
        var operation = input.OperationType?.Trim().ToUpperInvariant();

        if (category == "credit card payment")
            return new("Pagamento de fatura de cartão não é consumo", NeedsReview: input.IsCardPurchase);
        if (operation is "ESTORNO" or "DEVOLUCAO" || category.Contains("refund", StringComparison.Ordinal) || category.Contains("chargeback", StringComparison.Ordinal)
            || category.Contains("estorno", StringComparison.Ordinal))
            return new("Estorno ou ajuste: classificar manualmente", NeedsReview: true);
        if (OwnTransferCategories.Contains(category))
            return new("Transferência entre contas do titular não é consumo", NeedsReview: false);
        if (ThirdPartyTransferCategories.Contains(category))
            return new("Transferência a terceiros: a finalidade não é identificável pelos dados estruturados", NeedsReview: true);
        return null;
    }
}

/// <summary>Categoria sugerida pelo catálogo: nomes aceitos (a primeira é a canônica, criada quando nenhuma existir) e o tipo.</summary>
public sealed record FinancialCatalogEntry(CategoryType Type, IReadOnlyList<string> Names);

/// <summary>
/// Mapeamento entre as categorias da Pluggy (inglês, estruturadas) e categorias do Saldo360. Só entram associações inequívocas. A busca por
/// uma categoria existente ignora caixa, acento, pontuação e espaçamento, e aceita apelidos; só quando nada equivalente existe (nem inativa)
/// a categoria canônica é proposta para criação.
/// </summary>
public static class FinancialExternalCategoryCatalog
{
    private static readonly Dictionary<string, FinancialCatalogEntry> Expense = BuildExpense();
    private static readonly Dictionary<string, FinancialCatalogEntry> Income = BuildIncome();

    private static void Add(Dictionary<string, FinancialCatalogEntry> map, CategoryType type, string[] keys, params string[] names)
    {
        foreach (var key in keys) map[FinancialCategoryMatching.Key(key)] = new FinancialCatalogEntry(type, names);
    }

    private static Dictionary<string, FinancialCatalogEntry> BuildExpense()
    {
        var m = new Dictionary<string, FinancialCatalogEntry>(StringComparer.Ordinal);
        const CategoryType e = CategoryType.Expense;
        Add(m, e, ["groceries", "supermarkets", "food and drinks", "alimentacao", "mercado", "supermercado"], "Alimentação", "Mercado", "Supermercado");
        Add(m, e, ["eating out", "restaurants", "food delivery", "restaurantes"], "Restaurantes", "Alimentação");
        Add(m, e, ["shopping", "online shopping", "electronics", "office supplies", "compras"], "Compras");
        Add(m, e, ["bookstore"], "Livros e papelaria", "Compras");
        Add(m, e, ["houseware", "home improvement"], "Casa", "Moradia");
        Add(m, e, ["housing", "moradia"], "Moradia", "Casa");
        Add(m, e, ["rent", "aluguel"], "Aluguel", "Moradia");
        Add(m, e, ["clothing"], "Vestuário", "Roupas");
        Add(m, e, ["services", "servicos"], "Serviços");
        Add(m, e, ["digital services", "video streaming", "music streaming", "streaming", "subscriptions", "assinaturas"], "Assinaturas", "Serviços digitais", "Serviços");
        Add(m, e, ["telecommunications", "internet", "mobile", "phone"], "Telecomunicações", "Internet e telefone", "Internet");
        Add(m, e, ["electricity"], "Energia elétrica", "Energia", "Luz");
        Add(m, e, ["water"], "Água", "Água e esgoto");
        Add(m, e, ["gas stations", "fuel"], "Combustível", "Transporte");
        Add(m, e, ["vehicle maintenance", "automotive"], "Manutenção do veículo", "Veículo", "Transporte");
        Add(m, e, ["parking", "tolls and in vehicle payment", "taxi and ride-hailing", "public transport", "transportation", "transport", "transporte"], "Transporte");
        Add(m, e, ["vehicle ownership taxes and fees"], "Impostos do veículo", "Tributos", "Impostos");
        Add(m, e, ["insurance"], "Seguros");
        Add(m, e, ["taxes", "income taxes", "tax on financial operations"], "Tributos", "Impostos");
        Add(m, e, ["bank fees", "interests charged"], "Tarifas e juros", "Tarifas bancárias", "Juros");
        Add(m, e, ["pharmacy"], "Farmácia", "Saúde");
        Add(m, e, ["healthcare", "hospital clinics and labs", "health", "saude"], "Saúde");
        Add(m, e, ["wellness", "wellness and fitness", "gyms and fitness centers"], "Academia e bem-estar", "Bem-estar", "Saúde");
        Add(m, e, ["pet supplies and vet"], "Pets");
        Add(m, e, ["cinema, theater and concerts", "leisure", "entertainment", "sports goods", "lazer"], "Lazer");
        Add(m, e, ["gambling"], "Apostas", "Lazer");
        Add(m, e, ["kids and toys"], "Crianças", "Lazer");
        Add(m, e, ["education", "kindergarten", "educacao"], "Educação");
        Add(m, e, ["accomodation", "accommodation", "travel"], "Viagens", "Hospedagem");
        Add(m, e, ["donations"], "Doações");
        return m;
    }

    private static Dictionary<string, FinancialCatalogEntry> BuildIncome()
    {
        var m = new Dictionary<string, FinancialCatalogEntry>(StringComparer.Ordinal);
        const CategoryType i = CategoryType.Income;
        Add(m, i, ["proceeds interests and dividends", "investment income", "interest earned"], "Rendimentos", "Rendimentos de investimentos");
        Add(m, i, ["salary", "income", "payroll", "salario"], "Salário");
        return m;
    }

    public static FinancialCatalogEntry? Find(string? externalCategory, CategoryType type)
    {
        var key = FinancialCategoryMatching.Key(externalCategory);
        if (key.Length == 0) return null;
        return (type == CategoryType.Income ? Income : Expense).GetValueOrDefault(key);
    }

    /// <summary>
    /// Categoria existente (ativa, do tipo certo) equivalente ao que a Pluggy informou. O próprio nome externo também vale (o usuário pode ter
    /// uma categoria com o mesmo nome). Não cria nada.
    /// </summary>
    public static Guid? ResolveExisting(string? externalCategory, CategoryType type, IReadOnlyCollection<Pluggy.MappableCategory> categories)
    {
        var key = FinancialCategoryMatching.Key(externalCategory);
        if (key.Length == 0) return null;
        var candidates = new List<string> { key };
        if (Find(externalCategory, type) is { } entry) candidates.AddRange(entry.Names);
        foreach (var name in candidates)
            if (FinancialCategoryMatching.Find(categories, name, type) is { } match) return match.Id;
        return null;
    }

    /// <summary>Nome canônico a criar quando nenhum nome aceito existe, em nenhum estado (inclusive inativa, que o usuário desativou de propósito).</summary>
    public static string? ProposeNewName(string? externalCategory, CategoryType type, IReadOnlyCollection<Pluggy.MappableCategory> categories)
    {
        if (Find(externalCategory, type) is not { } entry) return null;
        if (entry.Names.Any(n => FinancialCategoryMatching.ExistsInAnyState(categories, n, type))) return null;
        return entry.Names[0];
    }
}

/// <summary>
/// Minimização de dados antes de qualquer envio ao modelo: remove documentos, números, chaves Pix, e-mails, telefones e identificadores, e
/// recusa (deixa para revisão manual) o que não pode ser anonimizado sem perder o sentido: transferências a pessoas e nomes de pessoas.
/// </summary>
public static partial class FinancialDescriptionSanitizer
{
    public const int MaxLength = 80;

    private static readonly HashSet<string> BusinessMarkers = new(StringComparer.Ordinal)
    {
        "ltda", "me", "epp", "eireli", "sa", "cia", "comercio", "servicos", "supermercado", "supermercados", "mercado", "farmacia", "drogaria", "posto",
        "restaurante", "padaria", "loja", "lojas", "auto", "banco", "pagamento", "pgto", "compra", "boleto", "tarifa", "seguro", "seguros", "energia", "agua",
        "telecom", "vivo", "claro", "tim", "oi", "uber", "ifood", "amazon", "netflix", "spotify", "google", "apple", "mercadolivre", "shopee", "pix",
        "iof", "juros", "fatura", "cartao", "assinatura", "clinica", "hospital", "escola", "colegio", "academia", "hotel", "app", "store", "pay", "market",
    };

    public static bool TrySanitize(string? description, string? operationType, string? externalCategory, bool isCardPurchase, out string sanitized)
    {
        sanitized = string.Empty;
        if (string.IsNullOrWhiteSpace(description)) return false;
        var operation = operationType?.Trim().ToUpperInvariant();
        var category = FinancialCategoryMatching.Key(externalCategory);
        // Transferências a pessoas: o texto é o nome do titular da outra ponta; não há como anonimizar sem perder o sentido.
        if (operation is "PIX" or "TED" or "DOC" || (operation?.StartsWith("TRANSFERENCIA", StringComparison.Ordinal) ?? false)
            || category.StartsWith("transfer", StringComparison.Ordinal) || category.StartsWith("same person", StringComparison.Ordinal) || category.StartsWith("third party", StringComparison.Ordinal))
            return false;

        var original = description.Trim();
        var text = original;
        foreach (var pattern in new[] { Email(), Url(), Document(), Uuid(), Phone(), MaskedNumber(), LongDigits(), LongToken() }) text = pattern.Replace(text, " ");
        text = KeepSafe().Replace(text, " ");
        text = string.Join(' ', text.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (text.Length > MaxLength) text = text[..MaxLength].TrimEnd();

        if (text.Count(char.IsLetter) < 3) return false;
        // Perdeu o sentido ao ser saneada: restaram menos da metade das palavras (com letras) da descrição original.
        var originalWords = original.Split(' ', StringSplitOptions.RemoveEmptyEntries).Count(w => w.Any(char.IsLetter));
        var keptWords = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Count(w => w.Count(char.IsLetter) >= 2);
        if (originalWords > 0 && keptWords * 2 < originalWords) return false;

        if (!isCardPurchase && LooksLikePersonName(text)) return false;
        sanitized = text;
        return true;
    }

    /// <summary>Duas ou mais palavras alfabéticas, sem dígitos nem marcador de empresa ou comércio: provavelmente o nome de uma pessoa.</summary>
    private static bool LooksLikePersonName(string text)
    {
        if (text.Any(char.IsDigit)) return false;
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 2 || words.Length > 6) return false;
        if (words.Any(w => w.Any(c => !char.IsLetter(c)))) return false;
        return !words.Select(w => FinancialCategoryMatching.Key(w)).Any(BusinessMarkers.Contains);
    }

    [GeneratedRegex(@"[\w.+-]+@[\w-]+\.[\w.-]+", RegexOptions.CultureInvariant)] private static partial Regex Email();
    [GeneratedRegex(@"(https?://|www\.)\S+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex Url();
    [GeneratedRegex(@"\b\d{2,3}[.\s]?\d{3}[.\s]?\d{3}[/.\-]?\d{0,4}[\-.\s]?\d{2}\b", RegexOptions.CultureInvariant)] private static partial Regex Document();
    [GeneratedRegex(@"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b", RegexOptions.CultureInvariant)] private static partial Regex Uuid();
    [GeneratedRegex(@"(\+?55\s?)?\(?\d{2}\)?[\s-]?9?\d{4}[\s-]?\d{4}\b", RegexOptions.CultureInvariant)] private static partial Regex Phone();
    [GeneratedRegex(@"[*xX•]{2,}\s?\d{2,}|\d{2,}\s?[*xX•]{2,}", RegexOptions.CultureInvariant)] private static partial Regex MaskedNumber();
    [GeneratedRegex(@"\d{5,}", RegexOptions.CultureInvariant)] private static partial Regex LongDigits();
    [GeneratedRegex(@"\b[A-Za-z0-9]{20,}\b", RegexOptions.CultureInvariant)] private static partial Regex LongToken();
    [GeneratedRegex(@"[^\p{L}\p{N}&*./\- ]", RegexOptions.CultureInvariant)] private static partial Regex KeepSafe();
}
