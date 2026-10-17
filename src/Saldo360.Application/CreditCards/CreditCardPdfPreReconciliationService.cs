using System.Globalization;
using System.Security.Cryptography;

namespace Saldo360.Application.CreditCards;

public sealed class CreditCardPdfPreReconciliationService(
    ICreditCardPdfPreviewService previewService,
    ICreditCardPdfPreReconciliationRepository repository) : ICreditCardPdfPreReconciliationService
{
    public async Task<CreditCardPdfPreReconciliationResult> PreReconcileAsync(
        Guid familyId, Guid cardId, Guid userId, Stream pdf, long length, string fileName,
        string? issuerProfile, string? competence, string? dateFormat, string? password,
        CancellationToken cancellationToken)
    {
        if (length <= 0 || length > CreditCardPdfPreviewService.MaxFileBytes)
            return Invalid("file", length <= 0 ? "O arquivo PDF está vazio." : "O arquivo excede o limite de 10 MB.");

        using var content = new MemoryStream((int)length);
        await pdf.CopyToAsync(content, cancellationToken);
        var bytes = content.ToArray();
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        content.Position = 0;

        var previewResult = await previewService.PreviewAsync(
            familyId, cardId, userId, content, bytes.LongLength, fileName,
            issuerProfile, competence, dateFormat, password, cancellationToken);
        if (previewResult.Access != CreditCardPdfPreviewAccess.Allowed)
            return new(previewResult.Access, null, previewResult.Errors);

        var preview = previewResult.Preview!;
        var snapshot = await repository.GetSnapshotAsync(
            familyId, cardId, userId, preview.Statement.Competence, cancellationToken);
        if (snapshot is null) return new(CreditCardPdfPreviewAccess.NotFound, null, null);

        var destination = ValidateDestination(familyId, cardId, userId, preview.Statement, snapshot);
        var destinationConflict = destination.Invoice == "Conflict";
        var items = preview.Items.Select(item => Classify(hash, item, snapshot.Operations, destinationConflict)).ToList();
        var counts = new CreditCardPdfPreReconciliationCounts(
            Count(CreditCardPdfComponentClassification.Create),
            Count(CreditCardPdfComponentClassification.AlreadyExists),
            Count(CreditCardPdfComponentClassification.NeedsReview),
            Count(CreditCardPdfComponentClassification.Conflict),
            Count(CreditCardPdfComponentClassification.Unsupported),
            Count(CreditCardPdfComponentClassification.Informational));

        var impactItems = items.Where(item => item.IncludedInCurrentTotal
            && item.Classification is "Create" or "NeedsReview"
            && decimal.TryParse(item.Amount, NumberStyles.Number | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _)).ToList();
        var debit = impactItems.Where(item => item.Nature == "Debit").Sum(item => decimal.Parse(item.Amount!, CultureInfo.InvariantCulture));
        var credit = impactItems.Where(item => item.Nature == "Credit").Sum(item => Math.Abs(decimal.Parse(item.Amount!, CultureInfo.InvariantCulture)));
        var impact = new CreditCardPdfEstimatedImpact("Uncategorized", Money(debit), Money(credit), Money(debit - credit), impactItems.Count);
        var limitations = new List<string>
        {
            "O modelo atual do cartão não armazena a identificação mascarada; esse vínculo exige revisão humana.",
            "Coincidências de descrição, data e valor são apenas candidatas e nunca comprovam duplicidade.",
            "A chave do documento e dos componentes é calculada em memória e não é persistida nesta etapa somente leitura.",
            "Encargos, créditos e estornos ainda não possuem fluxo de importação definitivo por PDF.",
        };
        var report = new CreditCardPdfPreReconciliationReport(
            $"pdf-sha256:{hash}", preview.Parser,
            new(preview.Statement.Issuer, preview.Statement.CardLastDigits, preview.Statement.Competence,
                preview.Statement.ClosingDate, preview.Statement.DueDate, preview.Statement.TotalAmount), preview.Reconciliation,
            destination, counts, items, [impact], limitations);
        return new(CreditCardPdfPreviewAccess.Allowed, report, null);

        int Count(CreditCardPdfComponentClassification value) =>
            items.Count(item => item.Classification == value.ToString());
    }

    private static CreditCardPdfDestinationValidation ValidateDestination(
        Guid familyId, Guid cardId, Guid userId, CreditCardPdfPreviewStatement statement,
        CreditCardPdfPreReconciliationSnapshot snapshot)
    {
        var notes = new List<string>();
        var holder = snapshot.HolderUserId is null ? "NeedsReview"
            : snapshot.HolderUserId == userId ? "Validated" : "NeedsReview";
        if (holder != "Validated") notes.Add("O titular configurado no cartão não pôde ser confirmado pela identidade autenticada.");
        var invoice = "NeedsReview";
        if (snapshot.Invoice is { } existing)
        {
            var closingMatches = statement.ClosingDate is null || statement.ClosingDate == existing.ClosingDate.ToString("yyyy-MM-dd");
            var dueMatches = statement.DueDate is null || statement.DueDate == existing.DueDate.ToString("yyyy-MM-dd");
            invoice = closingMatches && dueMatches ? "Validated" : "Conflict";
            if (invoice == "Conflict") notes.Add("Fechamento ou vencimento diverge da fatura persistida para a competência.");
        }
        else notes.Add("Não existe fatura persistida para validar a competência informada pelo documento.");
        notes.Add("A identificação mascarada não pode ser validada porque o cadastro atual não armazena os últimos dígitos.");
        return new(familyId, cardId, "Validated", "Validated", holder, "NeedsReview", invoice, snapshot.Invoice?.Id, notes);
    }

    private static CreditCardPdfPreReconciliationItem Classify(
        string hash, CreditCardPdfPreviewItem item,
        IReadOnlyList<CreditCardPdfExistingOperation> operations, bool destinationConflict)
    {
        var componentId = $"pdf:{hash}:p{item.Page}:r{item.RowNumber}";
        var reasons = new List<string>();
        var informational = item.Type is "PreviousBalance" or "FinancedBalance" or "Payment" or "FutureInstallment";
        var unsupported = item.Type is "Fee" or "Credit" or "Refund";
        CreditCardPdfExistingOperation? match = null;
        var classification = informational
            ? CreditCardPdfComponentClassification.Informational
            : unsupported ? CreditCardPdfComponentClassification.Unsupported
            : CreditCardPdfComponentClassification.NeedsReview;

        var strong = operations.Where(operation => operation.ExternalId == componentId).ToList();
        if (strong.Count > 0)
        {
            match = strong[0];
            classification = FactsMatch(item, match)
                ? CreditCardPdfComponentClassification.AlreadyExists
                : CreditCardPdfComponentClassification.Conflict;
            reasons.Add(classification == CreditCardPdfComponentClassification.AlreadyExists
                ? "Identidade externa estável e conteúdo conferem com o registro existente."
                : "A identidade externa já existe, mas o conteúdo financeiro diverge.");
        }
        else if (!informational && !unsupported && destinationConflict)
        {
            classification = CreditCardPdfComponentClassification.Conflict;
            reasons.Add("A identificação da fatura diverge do ciclo persistido.");
        }
        else if (!informational && !unsupported)
        {
            var candidates = operations.Where(operation => IsCandidate(item, operation)).ToList();
            if (candidates.Count == 1)
            {
                match = candidates[0];
                reasons.Add("Existe uma operação candidata, mas data, descrição e valor isolados não comprovam identidade.");
            }
            else if (candidates.Count > 1) reasons.Add("Existem múltiplas operações candidatas; é necessária seleção explícita.");
            else if (item.Type is "Purchase" or "InternationalPurchase" && item.InstallmentNumber is null)
            {
                classification = CreditCardPdfComponentClassification.Create;
                reasons.Add("Compra integral suportada, sem identidade existente nem operação candidata no cartão selecionado.");
            }
            else reasons.Add("Parcela isolada não comprova a compra original e exige revisão antes de importar.");
        }
        else if (informational)
        {
            reasons.Add(item.Type switch
            {
                "Payment" => "Pagamento informado na fatura não deve criar nova despesa bancária.",
                "FutureInstallment" => "Parcela futura é informativa e não deve duplicar obrigações.",
                _ => "Saldo histórico ou informação de fechamento não representa nova compra.",
            });
        }
        else reasons.Add("A natureza financeira requer fluxo de domínio específico e não será convertida em compra.");

        return new(componentId, item.RowNumber, item.Page, item.Type, item.Nature,
            item.TransactionDate, item.OriginalDescription ?? item.Description, item.Amount,
            item.InstallmentNumber, item.InstallmentCount, item.IncludedInCurrentTotal,
            classification.ToString(), match?.EntityType, match?.EntityId, reasons);
    }

    private static bool FactsMatch(CreditCardPdfPreviewItem item, CreditCardPdfExistingOperation operation) =>
        ParseDate(item.TransactionDate) == operation.Date
        && ParseAmount(item.Amount) == operation.Amount
        && (item.InstallmentNumber is null || item.InstallmentNumber == operation.InstallmentNumber)
        && (item.InstallmentCount is null || item.InstallmentCount == operation.InstallmentCount);

    private static bool IsCandidate(CreditCardPdfPreviewItem item, CreditCardPdfExistingOperation operation) =>
        ParseDate(item.TransactionDate) == operation.Date
        && ParseAmount(item.Amount) == operation.Amount
        && (item.InstallmentNumber is null || item.InstallmentNumber == operation.InstallmentNumber)
        && (item.InstallmentCount is null || item.InstallmentCount == operation.InstallmentCount);

    private static DateOnly? ParseDate(string? value) => DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;
    private static decimal? ParseAmount(string? value) => decimal.TryParse(value, NumberStyles.Number | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var amount) ? amount : null;
    private static string Money(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);
    private static CreditCardPdfPreReconciliationResult Invalid(string field, string message) =>
        new(CreditCardPdfPreviewAccess.Invalid, null, new Dictionary<string, string[]> { [field] = [message] });
}
