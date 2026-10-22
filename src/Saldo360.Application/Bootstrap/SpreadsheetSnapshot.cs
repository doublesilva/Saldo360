using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Saldo360.Application.Bootstrap;

/// <summary>Private, bounded native-Sheets capture. Never serialize this into application logs.</summary>
public sealed record SpreadsheetSnapshot(int SchemaVersion, string SourceId, string SourceDocument, string SourceUrl,
    DateTimeOffset ReadAtUtc, DateTimeOffset ModifiedAtUtc, string Locale, string TimeZone,
    string[] Ranges, SpreadsheetSheet[] Sheets)
{
    public const string Document = "Orçamento Diego 2026-2027";
    public const string NativeId = "1D9gdAWdNqMc4nv_XwDGExe8OjRe9zrCesIe6_Q-iWfk";
    public const string System = "BudgetSpreadsheet";
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() }
    };

    public static SpreadsheetSnapshot Parse(string json)
    {
        var source = JsonSerializer.Deserialize<SpreadsheetSnapshot>(json, Json) ?? throw new InvalidOperationException("InvalidSnapshot");
        if (source.SchemaVersion != 1 || source.SourceId != NativeId || source.SourceDocument != Document
            || source.Locale != "pt_BR" || source.TimeZone != "America/Sao_Paulo"
            || source.ReadAtUtc == default || source.ModifiedAtUtc > source.ReadAtUtc
            || source.Sheets.Select(x => x.Name).Distinct(StringComparer.Ordinal).Count() != source.Sheets.Length)
            throw new InvalidOperationException("InvalidSnapshotIdentity");
        var required = new[] { "'Parâmetros'!A1:D22", "'Metas Mensais'!A1:L13", "'Despesas Fixas'!A1:L30", "'Lançamentos'!A1:Q100", "'Histórico compromissos 08-10'!A1:J65", "'Movimentos Conta'!A1:L10" };
        if (!source.Ranges.Order().SequenceEqual(required.Order()) || source.Sheets.Length != 6
            || source.Sheets.Any(x => x.Rows.Length > 100 || x.Rows.Any(r => r.Length > 17)))
            throw new InvalidOperationException("InvalidSnapshotRanges");
        foreach (var cell in source.Sheets.SelectMany(s => s.Rows).SelectMany(r => r))
        {
            if (cell.Value.ValueKind is JsonValueKind.Array or JsonValueKind.Object or JsonValueKind.Undefined)
                throw new InvalidOperationException("InvalidCellValue");
            if (cell.RawNumber is not null && (cell.Formula is not null || cell.Value.ValueKind != JsonValueKind.Number
                || decimal.Parse(cell.RawNumber, NumberStyles.Float, CultureInfo.InvariantCulture) != cell.Value.GetDecimal()))
                throw new InvalidOperationException("RawNumberMismatch");
        }
        return source;
    }

    // Exclude transport timestamps/formatting. Include identity, consumed ranges, literal lexical values and formulas.
    public string LogicalHash => Hash(new { SchemaVersion, SourceId, SourceDocument, Locale, TimeZone,
        Ranges = Ranges.Order(StringComparer.Ordinal), Sheets = Sheets.OrderBy(x => x.Name, StringComparer.Ordinal)
            .Select(s => new { s.Name, Rows = s.Rows.Select(r => r.Select(c => new { c.Value, c.Formula, c.RawNumber })) }) });

    public SpreadsheetSheet Sheet(string name) => Sheets.Single(s => s.Name == name);
    public void RequireUnchanged(SpreadsheetSnapshot fresh)
    {
        if (fresh.ReadAtUtc <= ReadAtUtc || fresh.SourceId != SourceId || fresh.ModifiedAtUtc != ModifiedAtUtc || fresh.LogicalHash != LogicalHash)
            throw new InvalidOperationException("SourceChangedNewDryRunRequired");
    }
    public static string Hash<T>(T value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
        JsonSerializer.Serialize(value, new JsonSerializerOptions(Json) { WriteIndented = false }))));
}

public sealed record SpreadsheetSheet(string Name, SpreadsheetCell[][] Rows)
{
    public IEnumerable<(int Number, SpreadsheetCell[] Cells)> Data => Rows.Skip(1).Select((r, i) => (Number: i + 2, Cells: r))
        .Where(x => x.Cells.Any(c => c.Text.Length > 0));
}
public sealed record SpreadsheetCell(JsonElement Value, string? Formula, string? RawNumber, string? Display)
{
    [JsonIgnore] public string Text => Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? "" : Value.ToString().Trim();
    public decimal Decimal() => decimal.Parse(Text, NumberStyles.Float, CultureInfo.InvariantCulture);
    public int Integer()
    {
        var n = Decimal(); if (decimal.Truncate(n) != n) throw new FormatException("IntegerRequired"); return checked((int)n);
    }
    public DateOnly Date() => DateOnly.FromDateTime(new DateTime(1899, 12, 30).AddDays(Integer()));
}
