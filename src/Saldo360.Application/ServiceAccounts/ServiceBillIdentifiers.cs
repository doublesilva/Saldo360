using System.Text.RegularExpressions;
using Saldo360.Application.CreditCards;
using Saldo360.Domain.ServiceAccounts;

namespace Saldo360.Application.ServiceAccounts;

public static class ServiceBillIdentifiers
{
    public static IReadOnlyList<ExtractedServiceIdentifier> Extract(CreditCardPdfExtractedDocument document)
    {
        var found = new List<ExtractedServiceIdentifier>();
        var patterns = new (ServiceIdentifierType Type, string Label)[] {
            (ServiceIdentifierType.UtilityCustomerNumber, @"(?:UC|Unidade\s+Consumidora)(?:\s+(?<role>geradora))?"),
            (ServiceIdentifierType.CustomerCode, @"C[oó]digo\s+(?:do\s+)?Cliente"),
            (ServiceIdentifierType.InstallationNumber, @"Instala[cç][aã]o"),
            (ServiceIdentifierType.ContractNumber, @"Contrato") };
        foreach (var page in document.Pages)
        {
            var text = page.Words is { Count: > 0 } ? string.Join("\n", page.Words.GroupBy(w => Math.Round(w.Top / 3)).OrderBy(g => g.Key)
                .Select(g => string.Join(" ", g.OrderBy(w => w.X).Select(w => w.Text)))) : page.Text;
            foreach (var (type, label) in patterns)
            {
                // Label required. Do not scan arbitrary numbers, CPF, totals or substrings as identity.
                var value = type == ServiceIdentifierType.UtilityCustomerNumber ? @"[0-9][0-9.\-/]*(?:[ \t]+[0-9][0-9.\-/]*)*" : @"[A-Z0-9][A-Z0-9.\-/]{2,79}";
                foreach (Match match in Regex.Matches(text, @"(?<![\p{L}\p{N}])" + label + @"[ \t]*[:\-]?[ \t]*(" + value + @")(?![\p{L}\p{N}.\-/])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
                {
                    try
                    {
                        var normalized = ServiceIdentifierNormalization.Normalize(type, match.Groups[1].Value);
                        found.Add(new(type, normalized, ServiceIdentifierNormalization.Mask(normalized), "High")
                        { Context = match.Groups["role"].Success ? "GenerationUnit" : "ExplicitLabel" });
                    }
                    catch (ArgumentException) { /* An invalid printed code is not a usable strong identifier. */ }
                }
            }
        }
        var distinct = found.DistinctBy(i => (i.Type, i.NormalizedValue)).ToList();
        return distinct.Count > 50 ? [] : distinct;
    }
}
