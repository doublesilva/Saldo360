namespace Saldo360.Application.CreditCards.Projection;

/// <summary>
/// Composição de UMA fatura (cartão × competência) separada em consumo do orçamento, obrigação da fatura e caixa. Somente leitura: nada é gravado e nenhum lançamento fictício fecha a conta.
/// <c>ComponentsGap</c> é o que o total projetado tem além (ou aquém) da soma das parcelas individualizadas: deve ser zero numa fatura aberta, e é o ponto onde uma diferença se revela.
/// <c>UnattributedToBank</c> é o que o banco informa a mais que a composição conhecida de uma fatura fechada: componente não individualizado, nunca distribuído por suposição.
/// </summary>
public sealed record CardCompetenceDiagnosis(
    Guid CardId, string CardName, string Scope, string Competence, InvoicePhase Phase,
    decimal Registered, decimal KnownInstallments, decimal EstimatedInstallments, decimal CommittedSubscriptions, decimal EstimatedSubscriptions, decimal PendingPurchases,
    decimal? OfficialTotal, decimal Projected, decimal Obligation, decimal Paid, decimal Remaining, decimal UnattributedToBank, decimal ComponentsGap, IReadOnlyList<string> Reasons, decimal InvoiceFinancing = 0m)
{
    /// <summary>Soma das linhas individualizadas que o usuário vê (parcelas conhecidas e estimadas, assinaturas e compras pendentes), sem a composição já registrada.</summary>
    public decimal IndividualizedLines => KnownInstallments + EstimatedInstallments + CommittedSubscriptions + EstimatedSubscriptions + PendingPurchases + InvoiceFinancing;
}

public static class CardCompetenceDiagnostic
{
    public static IReadOnlyList<CardCompetenceDiagnosis> Build(CardProjection projection)
    {
        var occurrences = projection.Occurrences ?? [];
        var scope = projection.Card.PayerScope ?? projection.Card.Scope ?? "Sem escopo";
        return projection.Invoices.Select(invoice =>
        {
            var mine = occurrences.Where(o => o.Competence == invoice.Competence).ToList();
            decimal Sum(Func<CardOccurrence, bool> filter) => mine.Where(filter).Sum(o => o.Amount);
            var known = Sum(o => o.Kind == "KnownInstallment"); var estimatedInstallments = Sum(o => o.Kind == "EstimatedInstallment");
            var committedSubscriptions = Sum(o => o.Kind is "Recurring" or "ManualRecurring" && o.Quality == "Known");
            var estimatedSubscriptions = Sum(o => o.Kind is "Recurring" or "ManualRecurring" && o.Quality != "Known");
            var pending = Sum(o => o.Kind == "Pending");
            var closed = invoice.Phase == InvoicePhase.Closed;
            // Fatura fechada: o total do banco prevalece e as parcelas individualizadas já foram absorvidas por ele; o que sobra sobre a composição conhecida não é distribuído.
            var unattributed = closed && invoice.BankTotal is { } bank ? bank - invoice.Composition - invoice.Financed : 0m;
            var gap = closed ? 0m : invoice.ProjectedTotal - invoice.Composition - invoice.Financed - known - estimatedInstallments - committedSubscriptions - estimatedSubscriptions - pending;
            return new CardCompetenceDiagnosis(projection.Card.Id, projection.Card.Name, scope, invoice.Competence, invoice.Phase, invoice.Composition, known, estimatedInstallments, committedSubscriptions,
                estimatedSubscriptions, pending, invoice.BankTotal, invoice.ProjectedTotal, invoice.Obligation, invoice.Paid, invoice.Remaining, unattributed, gap, invoice.Flags, invoice.Financed);
        }).OrderBy(d => d.Competence, StringComparer.Ordinal).ToList();
    }
}
