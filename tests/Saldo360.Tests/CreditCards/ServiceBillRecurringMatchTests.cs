using Saldo360.Application.CreditCards;

namespace Saldo360.Tests.CreditCards;

public sealed class ServiceBillRecurringMatchTests
{
    private static readonly Guid DocumentId = Guid.NewGuid();

    [Theory]
    [InlineData("ElectricityBill", "CEEE", "CEEE", "200.00", "187.43")]
    [InlineData("WaterBill", "DMAE", "DMAE", "100.00", "91.20")]
    [InlineData("TelecomBill", "Vivo", "Vivo", "380.00", "367.42")]
    public async Task MatchesUniqueStrongCandidateRegardlessOfAmount(string type, string issuer, string description, string projected, string billed)
    {
        var occurrence = Candidate(description, projected, new DateOnly(2026, 10, 20));
        var result = await Service(occurrence).MatchAsync(Guid.NewGuid(), DocumentId, Extraction(type, issuer, billed), CancellationToken.None);
        Assert.Equal("Matched", result.MatchStatus); Assert.Equal(occurrence.OccurrenceId, result.MatchedOccurrenceId);
        Assert.Equal(projected, Assert.Single(result.Candidates).ProjectedAmount); Assert.Equal(billed, result.ExtractedAmount);
        Assert.Contains("IssuerExactNormalizedMatch", result.Candidates[0].Evidence);
    }

    [Fact]
    public async Task CompatibleCandidateWithoutStrongIdentityNeedsReview()
    {
        var result = await Service(Candidate("Internet residencial", "150.00", new(2026, 10, 21)))
            .MatchAsync(Guid.NewGuid(), DocumentId, Extraction("TelecomBill", null, "140.00"), CancellationToken.None);
        Assert.Equal("NeedsReview", result.MatchStatus); Assert.Single(result.Candidates);
    }

    [Fact]
    public async Task MultipleStrongCandidatesAreAmbiguous()
    {
        var result = await Service(Candidate("Vivo", "100.00", new(2026, 10, 20)), Candidate("Vivo", "200.00", new(2026, 10, 21)))
            .MatchAsync(Guid.NewGuid(), DocumentId, Extraction("TelecomBill", "Vivo", "190.00"), CancellationToken.None);
        Assert.Equal("Ambiguous", result.MatchStatus); Assert.Equal(2, result.Candidates.Count);
    }

    [Fact]
    public async Task DifferentCompetenceOrNoCompatibleCandidateIsNotFound()
    {
        var repository = new Repository([Candidate("CEEE", "200.00", new(2026, 11, 20))], enforceCompetence: true);
        var result = await new ServiceBillRecurringMatchService(repository).MatchAsync(Guid.NewGuid(), DocumentId, Extraction("ElectricityBill", "CEEE", "180.00"), CancellationToken.None);
        Assert.Equal("NotFound", result.MatchStatus);
    }

    [Theory]
    [InlineData("NeedsReview")]
    [InlineData("Rejected")]
    public async Task NonReliableExtractionIsRejected(string assessment)
    {
        var result = await Service().MatchAsync(Guid.NewGuid(), DocumentId, Extraction("WaterBill", "DMAE", "10.00", assessment), CancellationToken.None);
        Assert.Equal("Rejected", result.MatchStatus);
    }

    private static ServiceBillRecurringMatchService Service(params ServiceBillRecurringCandidateData[] values) => new(new Repository(values));
    private static ServiceBillRecurringCandidateData Candidate(string description, string amount, DateOnly due) => new(Guid.NewGuid(), Guid.NewGuid(), description, description, due, decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture));
    private static ServiceBillExtractionOutput Extraction(string type, string? issuer, string amount, string assessment = "Reliable") => new(new(type, issuer, "2026-10", "2026-10-20", amount, null, null, "High", "Deterministic", null), assessment);
    private sealed class Repository(IEnumerable<ServiceBillRecurringCandidateData> values, bool enforceCompetence = false) : IServiceBillRecurringCandidateRepository
    {
        public Task<IReadOnlyList<ServiceBillRecurringCandidateData>> ListAsync(Guid familyId, string competence, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ServiceBillRecurringCandidateData>>(values.Where(value => !enforceCompetence || value.DueDate.ToString("yyyy-MM") == competence).ToList());
    }
}
