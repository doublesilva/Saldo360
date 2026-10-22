using System.Globalization;
using System.Text.Json;
using Saldo360.Application.Bootstrap;

namespace Saldo360.Tests.Support;

/// <summary>Invented values/descriptions/notes, retaining contractual source IDs and approved corner cases. No workbook export, documents or credentials.</summary>
internal static class BudgetSpreadsheetFixture
{
    public static readonly DateTimeOffset ReadAt = new(2026, 10, 22, 18, 0, 0, TimeSpan.Zero);
    public static SpreadsheetCell Cell(object? value) => new(JsonSerializer.SerializeToElement(value), null,
        value is decimal or int ? Convert.ToString(value, CultureInfo.InvariantCulture) : null, null);
    public static SpreadsheetCell[] Row(params object?[] values) => values.Select(Cell).ToArray();
    public static int Serial(int month, int day = 1) => new DateOnly(month < 10 ? 2027 : 2026, month, day).DayNumber - new DateOnly(1899, 12, 30).DayNumber;
    public static SpreadsheetSnapshot Source()
    {
        var fixedRows = new List<SpreadsheetCell[]> { Row("ID", "Escopo", "Descrição", "Categoria", "Valor mensal", "Dia vencimento", "Início", "Fim", "Forma", "Cartão", "Ativa", "Observações") };
        foreach (var id in new[] { "PJ-001", "PJ-002", "PJ-003", "PJ-004", "PF-001", "PF-002", "PF-003", "PF-004", "PF-005", "PF-006", "PF-007", "PF-008" })
            fixedRows.Add(Row(id, id.StartsWith("PJ") ? "PJ" : "PF", "Synthetic fixed " + id, "Serviços", 10m, id == "PF-005" ? 6 : id == "PF-003" ? 11 : id == "PF-006" ? 24 : null,
                Serial(10), id == "PF-006" ? Serial(2, 28) : null, "Boleto", null, id == "PF-007" ? "Não" : "Sim", "Synthetic note"));
        foreach (var id in new[] { "PF-003-202610", "PF-003-202611", "PF-002-202610", "PF-IPTU-202610", "PF-IPTU-202611", "PF-IPTU-202612", "PF-TAX-202702" })
        {
            var month = int.Parse(id[^2..]);
            fixedRows.Add(Row(id, "PF", "Synthetic planned " + id, "Tributos", 10m, id.StartsWith("PF-IPTU") ? 8 : null, Serial(month), Serial(month, DateTime.DaysInMonth(month < 10 ? 2027 : 2026, month)), "Boleto", null, "Sim", "estimada/provisão"));
        }
        var launches = new List<SpreadsheetCell[]> { Row("Data", "Competência", "Escopo", "Movimento", "Categoria", "Descrição", "Forma", "Cartão", "Valor", "Status", "Observações", "ID", "Tipo", "Qtd parcelas desde competência", "Fim informado (opcional)", "Fim calculado", "Ativa") };
        SpreadsheetCell[] Launch(string id, string type, string? card, decimal amount, int competence = 10, int? date = null, int? count = null, string note = "", string description = "Synthetic")
            => Row(date, Serial(competence), "PF", id == "LAN-001" ? "Ignorar" : "Despesa", "Assinaturas", description, "Crédito", card, amount, "Pendente", note, id, type, count, null, null, "Sim");
        launches.Add(Launch("LAN-001", "Único", null, 1, date: Serial(10)));
        for (var i = 2; i <= 5; i++) launches.Add(Launch("LAN-00" + i, "Único", null, 1, date: Serial(10, 8)));
        foreach (var id in new[] { "LAN-006", "LAN-007", "LAN-BL-20261010-MERCADO", "LAN-NU-20261011-DOMINOS" })
            launches.Add(Launch(id, "Único", id.Contains("NU-") ? "Nubank" : "Black", 10, 11, Serial(10, 8)));
        launches.Add(Launch("LAN-BL-AMZ-701-9749415-1047439", "Único", "Black", 33.90m, 11, Serial(10, 13)));
        foreach (var id in new[] { "REC-001", "REC-002", "REC-003", "REC-ZAF-001", "REC-BL-KINDLE-001", "REC-BL-ADFREE-001" })
            launches.Add(Launch(id, "Recorrente", id == "REC-003" ? "Nubank" : id == "REC-ZAF-001" ? "Zaffari/Mercado" : "Black", 10,
                id == "REC-ZAF-001" ? 11 : 10, count: null, note: "em 05/10, 27/09, 23/09 e 12/09; PDF Itaú"));
        launches.Add(Launch("PAR-NU-004-OUT", "Parcelado", "Nubank", 111.78m, count: 1, note: "Continuação nas parcelas 2/3 e 3/3", description: "Synthetic — parcela 1/3"));
        launches.Add(Launch("PAR-NU-004-FUT", "Parcelado", "Nubank", 111.76m, 11, count: 2, note: "Nov 2/3; dez 3/3"));
        launches.Add(Launch("PAR-BL-022-01", "Parcelado", "Black", 97.79m, 11, Serial(10, 9), 1, "Compra total R$ 293,35 em 3x aprovada em 09/10/2026", "Synthetic — parcela 1/3"));
        launches.Add(Launch("PAR-BL-022-02", "Parcelado", "Black", 97.78m, 12, Serial(10, 9), 2, "Compra total R$ 293,35 em 3x aprovada em 09/10/2026", "Synthetic — parcelas 2/3 e 3/3"));
        launches.Add(Launch("PAR-BL-002", "Parcelado", "Black", 320.71m, count: 4, note: "Out 7/10; Fonte: PDF Itaú"));
        foreach (var id in new[] { "PAR-BL-021", "PAR-NU-005", "PAR-NU-006", "PAR-BL-023" })
            launches.Add(Launch(id, "Parcelado", id.StartsWith("PAR-NU") ? "Nubank" : "Black", 10m, 11, Serial(10, 8), 3, "Total R$ 30,00; compra informada em 08/10/2026"));
        for (var i = 0; i < 25; i++) launches.Add(Launch("PAR-SYNTHETIC-" + i, "Parcelado", i == 0 ? "Credicard" : "Black", 10, count: 2, note: "Out 2/3; nov 3/3; Fonte: PDF Itaú"));
        var history = new[] { Row("ID") }.Concat(launches.Skip(1).Take(37).Select(r => Row(r[11].Text))).ToArray();
        var targets = new List<SpreadsheetCell[]> { Row("Competência", "Meta fixos PF", "Meta cartões", "Meta total PF", "Meta recuperação aprovada") };
        for (var i = 0; i < 12; i++) targets.Add(Row(Serial((9 + i) % 12 + 1), 7000m, 5000m, 12000m, i == 0 ? 1270.3759999999995m : 0m));
        return new(1, SpreadsheetSnapshot.NativeId, SpreadsheetSnapshot.Document, "https://docs.google.com/spreadsheets/d/" + SpreadsheetSnapshot.NativeId + "/edit",
            ReadAt, ReadAt.AddDays(-5), "pt_BR", "America/Sao_Paulo",
            ["'Parâmetros'!A1:D22", "'Metas Mensais'!A1:L13", "'Despesas Fixas'!A1:L30", "'Lançamentos'!A1:Q100", "'Histórico compromissos 08-10'!A1:J65", "'Movimentos Conta'!A1:L10"],
            [new("Lançamentos", launches.ToArray()), new("Despesas Fixas", fixedRows.ToArray()), new("Histórico compromissos 08-10", history), new("Metas Mensais", targets.ToArray()),
                new("Parâmetros", [Row("Parâmetro", "Valor"), Row("Valor hora PJ", 100m), Row("Horas por dia útil", 8m)]),
                new("Movimentos Conta", [Row("Data", "Competência", "Descrição", "Tipo", "Valor com sinal", "Saldo após movimento"), Row(Serial(10, 8), Serial(10), "Synthetic snapshot", "Saldo", -13826.92m, -13826.92m)])]);
    }

    public static SpreadsheetSnapshot ExecutableSyntheticSource()
    {
        var s = Source();
        foreach (var row in s.Sheet("Lançamentos").Rows.Skip(1))
        {
            if (row[7].Text is "Credicard" or "Zaffari/Mercado") row[10] = Cell(row[10].Text + "; Emissor confirmado: Synthetic issuer;");
            if (row[11].Text is "REC-003" or "REC-ZAF-001") row[0] = Cell(row[1].Value.GetInt32());
        }
        return s;
    }
}
