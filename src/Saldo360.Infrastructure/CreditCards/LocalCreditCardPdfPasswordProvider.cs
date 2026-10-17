using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Saldo360.Application.CreditCards;

namespace Saldo360.Infrastructure.CreditCards;

internal sealed class LocalCreditCardPdfPasswordProvider(IConfiguration configuration, IHostEnvironment environment)
    : ICreditCardPdfPasswordProvider
{
    private const string SectionName = "CreditCardPdfPasswordMock";

    public Task<CreditCardPdfPasswordResolution> ResolveAsync(Guid holderUserId, string issuer, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if ((!environment.IsDevelopment() && !environment.IsEnvironment("Testing")) ||
            !configuration.GetValue<bool>($"{SectionName}:Enabled"))
            return Task.FromResult(CreditCardPdfPasswordResolution.Unavailable);

        var identity = configuration.GetSection($"{SectionName}:Identities:{holderUserId:D}");
        var first5 = Normalize(identity["First5"], 5);
        var first3 = Normalize(identity["First3"], 3);
        if (first3 is null && first5 is not null) first3 = first5[..3];

        var preferred = configuration[$"{SectionName}:IssuerStrategies:{issuer.Trim()}"];
        var candidates = preferred switch
        {
            "First3" => Distinct(first3, first5),
            "First5" => Distinct(first5, first3),
            _ => Distinct(first5, first3),
        };
        return Task.FromResult(new CreditCardPdfPasswordResolution(candidates));
    }

    private static string? Normalize(string? value, int length) =>
        value is { Length: var actual } && actual == length && value.All(char.IsAsciiDigit) ? value : null;

    private static IReadOnlyList<string> Distinct(params string?[] values) =>
        values.Where(value => value is not null).Select(value => value!).Distinct(StringComparer.Ordinal).Take(2).ToArray();
}
