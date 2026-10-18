using Saldo360.Application.CreditCards;

namespace Saldo360.Infrastructure.CreditCards;

internal sealed class CreditCardPdfParserResolver
{
    private readonly IReadOnlyList<Parser> _parsers =
    [
        new(ItauCreditCardPdfParser.CanParse, ItauCreditCardPdfParser.Parse),
        new(NubankCreditCardPdfParser.CanParse, NubankCreditCardPdfParser.Parse),
    ];

    public CreditCardPdfPreview? TryParse(CreditCardPdfExtractedDocument document, string fileName, string? competence) =>
        _parsers.FirstOrDefault(parser => parser.CanParse(document))?.Parse(document, fileName, competence);

    private sealed record Parser(
        Func<CreditCardPdfExtractedDocument, bool> CanParse,
        Func<CreditCardPdfExtractedDocument, string, string?, CreditCardPdfPreview> Parse);
}
