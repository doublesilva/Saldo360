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

    [Fact]
    public async Task ExplicitReferenceBindingIsStrongEvidenceWithoutDescriptionSimilarity()
    {
        var candidate = new ServiceBillRecurringCandidateData(Guid.NewGuid(), Guid.NewGuid(), "Despesa mensal", "Serviços",
            new DateOnly(2026, 10, 20), 200m, AccountBindingMatch: true);
        var result = await Service(candidate).MatchAsync(Guid.NewGuid(), DocumentId,
            Extraction("ElectricityBill", null, "187.43", accountReference: "UC-123456"), CancellationToken.None);
        Assert.Equal("Matched", result.MatchStatus);
        Assert.Contains("AccountReferenceBindingMatch", Assert.Single(result.Candidates).Evidence);
    }

    [Theory]
    [InlineData(" uc-123.456 ", "UC123456")]
    [InlineData("linha (51) 9999-0000", "LINHA5199990000")]
    public void ReferenceIdentityIsStableAndMasked(string left, string right)
    {
        Assert.Equal(ServiceReferenceIdentity.Hash(left), ServiceReferenceIdentity.Hash(right));
        Assert.DoesNotContain("123456", ServiceReferenceIdentity.Mask(left)!);
        Assert.EndsWith(ServiceReferenceIdentity.Normalize(left)![^4..], ServiceReferenceIdentity.Mask(left));
    }

    [Theory]
    [InlineData("1234")]
    [InlineData("A")]
    public void ShortReferencesAreNeverReturnedInFull(string value) => Assert.NotEqual(value, ServiceReferenceIdentity.Mask(value));

    private static ServiceBillRecurringMatchService Service(params ServiceBillRecurringCandidateData[] values) => new(new Repository(values));
    private static ServiceBillRecurringCandidateData Candidate(string description, string amount, DateOnly due) => new(Guid.NewGuid(), Guid.NewGuid(), description, description, due, decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture));
    private static ServiceBillExtractionOutput Extraction(string type, string? issuer, string amount, string assessment = "Reliable", string? accountReference = null, string? serviceReference = null) => new(new(type, issuer, "2026-10", "2026-10-20", amount, accountReference, serviceReference, "High", "Deterministic", null), assessment);
    private sealed class Repository(IEnumerable<ServiceBillRecurringCandidateData> values, bool enforceCompetence = false) : IServiceBillRecurringCandidateRepository
    {
        public Task<IReadOnlyList<ServiceBillRecurringCandidateData>> ListAsync(Guid familyId, string competence, string documentType, string? accountReferenceHash, string? serviceReferenceHash, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ServiceBillRecurringCandidateData>>(values.Where(value => !enforceCompetence || value.DueDate.ToString("yyyy-MM") == competence).ToList());
    }
}
