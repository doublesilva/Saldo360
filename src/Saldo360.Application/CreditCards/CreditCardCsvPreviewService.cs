using System.Globalization;
using System.Text;
using Saldo360.Application.Workspaces;

namespace Saldo360.Application.CreditCards;

public sealed class CreditCardCsvPreviewService(
    IFinancialWorkspaceRepository workspaces,
    IFinancialCreditCardPurchaseRepository purchases) : ICreditCardCsvPreviewService
{
    public const long MaxFileBytes = 5 * 1024 * 1024;
    public const int MaxRows = 10_000;

    public async Task<CreditCardCsvPreviewResult> PreviewAsync(
        Guid workspaceId,
        Guid cardId,
        Guid userId,
        Stream csv,
        long length,
        CreditCardCsvPreviewRequest request,
        CancellationToken cancellationToken)
    {
        if (await workspaces.GetOwnerRoleAsync(workspaceId, userId, cancellationToken).ConfigureAwait(false) is null
            || !await purchases.IsActiveCardAsync(workspaceId, cardId, cancellationToken).ConfigureAwait(false))
            return new(CreditCardCsvPreviewAccess.NotFound, null, null);
        if (length > MaxFileBytes)
            return Invalid("file", $"O arquivo excede o limite de {MaxFileBytes / (1024 * 1024)} MB.");

        try
        {
            var csvData = await CsvReader.ReadAsync(csv, MaxRows, ParseDelimiter(request.Delimiter), cancellationToken).ConfigureAwait(false);
            var records = csvData.Records;
            if (records.Count == 0) return Invalid("file", "O arquivo CSV está vazio.");
            var delimiter = csvData.Delimiter;
            var headers = records[0].Values.Select((value, index) => (Name: NormalizeHeader(value), Index: index)).ToList();
            var mappingResult = Mapping.Create(request, delimiter, headers);
            if (!mappingResult.IsValid) return new(CreditCardCsvPreviewAccess.Invalid, null, mappingResult.Errors);
            var mapping = mappingResult.Mapping!;
            var previewRows = new List<CreditCardCsvPreviewRow>();
            for (var index = 1; index < records.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                previewRows.Add(NormalizeRow(records[index].Values, index + 1, mapping));
            }

            var valid = previewRows.Count(row => row.Status == "Valid");
            var warning = previewRows.Count(row => row.Status == "Warning");
            var invalid = previewRows.Count(row => row.Status == "Invalid");
            return new(CreditCardCsvPreviewAccess.Allowed,
                new(previewRows.Count, valid, warning, invalid, csvData.IgnoredRows, mapping.ToDto(), previewRows), null);
        }
        catch (CsvReadException exception)
        {
            return Invalid("file", exception.Message);
        }
        catch (PreviewValidationException exception)
        {
            return Invalid(exception.Field, exception.Message);
        }
        catch (DecoderFallbackException)
        {
            return Invalid("file", "A codificação do arquivo não é UTF-8 válida.");
        }
    }

    private static CreditCardCsvPreviewResult Invalid(string field, string message) =>
        new(CreditCardCsvPreviewAccess.Invalid, null, new Dictionary<string, string[]> { [field] = [message] });

    private static char? ParseDelimiter(string? value) => value switch
    {
        null or "" => null,
        "," => ',',
        ";" => ';',
        _ => throw new PreviewValidationException("delimiter", "O separador deve ser vírgula ou ponto e vírgula."),
    };

    private static string NormalizeHeader(string value) => value.Trim().TrimStart('\uFEFF');

    private static CreditCardCsvPreviewRow NormalizeRow(IReadOnlyList<string> values, int rowNumber, Mapping mapping)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        string Get(string? column) => column is null || !mapping.Columns.TryGetValue(column, out var index) || index >= values.Count ? "" : values[index].Trim();
        var dateText = Get(mapping.DateColumn);
        var description = CollapseSpaces(Get(mapping.DescriptionColumn));
        var amountText = Get(mapping.AmountColumn);
        var normalizedDate = ParseDate(dateText, mapping.DateFormat, errors);
        var amount = ParseAmount(amountText, mapping.DecimalFormat, errors);
        if (description.Length == 0) errors.Add("Descrição obrigatória ausente.");
        if (amount is null && amountText.Length > 0 && errors.Count == 0) errors.Add("Valor inválido.");
        if (amount is { } parsed && parsed < 0) warnings.Add("Valor negativo requer revisão; não foi classificado automaticamente como compra.");
        var installmentNumber = ParseOptionalInt(Get(mapping.InstallmentNumberColumn), "Número da parcela", errors);
        var installmentCount = ParseOptionalInt(Get(mapping.InstallmentCountColumn), "Quantidade de parcelas", errors);
        if (installmentNumber is > 0 && installmentCount is > 0 && installmentNumber > installmentCount) warnings.Add("Número da parcela maior que a quantidade total.");
        if (values.Count < mapping.RequiredColumnCount) errors.Add("Linha incompleta: faltam colunas mapeadas.");
        var status = errors.Count > 0 ? "Invalid" : warnings.Count > 0 ? "Warning" : "Valid";
        return new(rowNumber, normalizedDate, description, amount is null ? null : amount.Value.ToString("0.00", CultureInfo.InvariantCulture), installmentNumber, installmentCount, EmptyToNull(Get(mapping.ExternalIdColumn)), EmptyToNull(Get(mapping.CategoryColumn)), EmptyToNull(Get(mapping.CompetenceColumn)), status, errors, warnings);
    }

    private static string? ParseDate(string value, string? format, ICollection<string> errors)
    {
        if (value.Length == 0) { errors.Add("Data obrigatória ausente."); return null; }
        string[] formats = format is null or "" ? ["yyyy-MM-dd", "dd/MM/yyyy"] : [format];
        if (DateOnly.TryParseExact(value, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) return date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        errors.Add("Data inválida ou formato não suportado.");
        return null;
    }

    private static decimal? ParseAmount(string value, string format, ICollection<string> errors)
    {
        if (value.Length == 0) { errors.Add("Valor obrigatório ausente."); return null; }
        var text = value.Replace("R$", "", StringComparison.OrdinalIgnoreCase).Trim();
        if (format.Equals("Auto", StringComparison.OrdinalIgnoreCase)
            && ((text.Count(ch => ch == ',') == 1 && text.Count(ch => ch == '.') == 0 && text.IndexOf(',') <= 3 && text.Length - text.IndexOf(',') - 1 == 3)
                || (text.Count(ch => ch == '.') == 1 && text.Count(ch => ch == ',') == 0 && text.IndexOf('.') <= 3 && text.Length - text.IndexOf('.') - 1 == 3)))
        {
            errors.Add("Valor ambíguo; informe decimalFormat como PtBr ou Invariant.");
            return null;
        }
        var culture = format.Equals("PtBr", StringComparison.OrdinalIgnoreCase) ? CultureInfo.GetCultureInfo("pt-BR")
            : format.Equals("Invariant", StringComparison.OrdinalIgnoreCase) ? CultureInfo.InvariantCulture
            : text.Contains(',') ? CultureInfo.GetCultureInfo("pt-BR") : CultureInfo.InvariantCulture;
        if (decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowThousands, culture, out var amount)) return amount;
        errors.Add("Valor inválido ou formato decimal não suportado.");
        return null;
    }

    private static int? ParseOptionalInt(string value, string label, ICollection<string> errors)
    {
        if (value.Length == 0) return null;
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) && result > 0) return result;
        errors.Add($"{label} inválido.");
        return null;
    }

    private static string CollapseSpaces(string value) => string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    private static string? EmptyToNull(string value) => value.Length == 0 ? null : value;

    private sealed record Mapping(
        string DateColumn, string DescriptionColumn, string AmountColumn, string? CategoryColumn,
        string? InstallmentNumberColumn, string? InstallmentCountColumn, string? ExternalIdColumn, string? CompetenceColumn,
        char Delimiter, string? DateFormat, string DecimalFormat, Dictionary<string, int> Columns)
    {
        public int RequiredColumnCount => Columns.Values.DefaultIfEmpty(-1).Max() + 1;
        public CreditCardCsvPreviewMapping ToDto() => new(DateColumn, DescriptionColumn, AmountColumn, CategoryColumn, InstallmentNumberColumn, InstallmentCountColumn, ExternalIdColumn, CompetenceColumn, Delimiter.ToString(), DateFormat, DecimalFormat);

        public static MappingResult Create(CreditCardCsvPreviewRequest request, char delimiter, IReadOnlyList<(string Name, int Index)> headers)
        {
            var errors = new Dictionary<string, string[]>();
            var names = headers.Select(item => item.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            string Required(string? requested, string fallback, string field)
            {
                var value = string.IsNullOrWhiteSpace(requested) ? fallback : requested.Trim();
                if (!names.Contains(value))
                {
                    var aliases = field switch
                    {
                        "dateColumn" => new[] { "Date", "date", "Data", "data" },
                        "descriptionColumn" => new[] { "Description", "description", "Descrição", "descricao" },
                        "amountColumn" => new[] { "Amount", "amount", "Valor", "valor" },
                        _ => Array.Empty<string>(),
                    };
                    value = aliases.FirstOrDefault(names.Contains) ?? value;
                }
                if (!names.Contains(value)) errors[field] = [$"A coluna '{value}' não foi encontrada no cabeçalho."];
                return value;
            }
            string? Optional(string? value, string field) {
                if (string.IsNullOrWhiteSpace(value)) return null;
                var normalized = value.Trim(); if (!names.Contains(normalized)) errors[field] = [$"A coluna '{normalized}' não foi encontrada no cabeçalho."];
                return normalized;
            }
            var date = Required(request.DateColumn, "Data", "dateColumn");
            var description = Required(request.DescriptionColumn, "Descrição", "descriptionColumn");
            var amount = Required(request.AmountColumn, "Valor", "amountColumn");
            var category = Optional(request.CategoryColumn, "categoryColumn");
            var installmentNumber = Optional(request.InstallmentNumberColumn, "installmentNumberColumn");
            var installmentCount = Optional(request.InstallmentCountColumn, "installmentCountColumn");
            var externalId = Optional(request.ExternalIdColumn, "externalIdColumn");
            var competence = Optional(request.CompetenceColumn, "competenceColumn");
            var columns = headers.Where(item => new[] { date, description, amount, category, installmentNumber, installmentCount, externalId, competence }.Contains(item.Name, StringComparer.OrdinalIgnoreCase)).ToDictionary(item => item.Name, item => item.Index, StringComparer.OrdinalIgnoreCase);
            return new(errors.Count == 0, errors, new(date, description, amount, category, installmentNumber, installmentCount, externalId, competence, delimiter, request.DateFormat, string.IsNullOrWhiteSpace(request.DecimalFormat) ? "Auto" : request.DecimalFormat.Trim(), columns));
        }
    }

    private sealed record MappingResult(bool IsValid, IDictionary<string, string[]> Errors, Mapping? Mapping);
    private sealed class PreviewValidationException(string field, string message) : Exception(message)
    {
        public string Field { get; } = field;
    }

    private sealed record CsvRecord(IReadOnlyList<string> Values);
    private sealed record CsvData(char Delimiter, List<CsvRecord> Records, int IgnoredRows);

    private static class CsvReader
    {
        public static async Task<CsvData> ReadAsync(Stream stream, int maxRows, char? configuredDelimiter, CancellationToken cancellationToken)
        {
            using var reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true, leaveOpen: true);
            var text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            if (text.Length == 0) return new(',', [], 0);
            var delimiter = configuredDelimiter ?? DetectDelimiter(text);
            var result = new List<CsvRecord>();
            var ignoredRows = 0;
            var record = new List<string>(); var field = new StringBuilder(); var quoted = false; var quoteClosed = false; var line = 1;
            for (var index = 0; index < text.Length; index++)
            {
                var ch = text[index];
                if (ch == '"')
                {
                    if (quoted && index + 1 < text.Length && text[index + 1] == '"') { field.Append('"'); index++; continue; }
                    if (quoted) { quoted = false; quoteClosed = true; continue; }
                    if (!quoteClosed && field.Length == 0) { quoted = true; continue; }
                    throw new CsvReadException($"CSV malformado próximo à linha {line}.");
                }
                if (ch == '\n' && !quoted) { record.Add(field.ToString().TrimEnd('\r')); field.Clear(); if (record.Count == 1 && string.IsNullOrWhiteSpace(record[0])) ignoredRows++; else result.Add(new(record.ToArray())); record.Clear(); line++; if (result.Count > maxRows + 1) throw new CsvReadException($"O arquivo excede o limite de {maxRows} linhas de dados."); quoteClosed = false; continue; }
                if (ch == delimiter && !quoted) { record.Add(field.ToString()); field.Clear(); quoteClosed = false; continue; }
                field.Append(ch);
                if (quoteClosed && !char.IsWhiteSpace(ch) && ch != delimiter) throw new CsvReadException($"CSV malformado próximo à linha {line}.");
            }
            if (quoted) throw new CsvReadException($"CSV malformado: aspas não fechadas na linha {line}.");
            if (field.Length > 0 || record.Count > 0) { record.Add(field.ToString()); if (record.Count == 1 && string.IsNullOrWhiteSpace(record[0])) ignoredRows++; else result.Add(new(record.ToArray())); }
            return new(delimiter, result, ignoredRows);
        }

        private static char DetectDelimiter(string text)
        {
            var commas = 0; var semicolons = 0; var quoted = false;
            foreach (var ch in text)
            {
                if (ch == '"') quoted = !quoted;
                else if (!quoted && ch == ',') commas++;
                else if (!quoted && ch == ';') semicolons++;
                else if (!quoted && ch == '\n') break;
            }
            return semicolons > commas ? ';' : ',';
        }
    }

    private sealed class CsvReadException(string message) : Exception(message);
}
