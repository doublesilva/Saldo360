using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Saldo360.Domain.Budgets;
using Saldo360.Domain.CreditCards;

namespace Saldo360.Application.Bootstrap;

/// <summary>T09.2 source adapter, not another financial engine. Defaults/exclusions are approved decisions.</summary>
public sealed class BudgetSpreadsheetMapper
{
    private readonly List<SpreadsheetPlanItem> items = [];
    private static readonly string[] Categories = ["Alimentação", "Assinaturas", "Financiamento", "Moradia", "Operação PJ", "Outros", "Saúde", "Serviços", "Tributos", "Variáveis"];
    private static readonly HashSet<string> Singles = ["LAN-006", "LAN-007", "LAN-BL-20261010-MERCADO", "LAN-NU-20261011-DOMINOS"];
    private static readonly HashSet<string> BankRecurrences = ["PJ-001", "PJ-002", "PJ-003", "PJ-004", "PF-001", "PF-002", "PF-003", "PF-004", "PF-005", "PF-006", "PF-008"];
    private static readonly HashSet<string> SpecificPlans = ["PF-003-202610", "PF-003-202611", "PF-002-202610", "PF-IPTU-202610", "PF-IPTU-202611", "PF-IPTU-202612", "PF-TAX-202702"];
    private static readonly HashSet<string> CardRecurrences = ["REC-001", "REC-002", "REC-003", "REC-ZAF-001", "REC-BL-KINDLE-001", "REC-BL-ADFREE-001"];
    private static readonly HashSet<string> UnsupportedParameters = ["Valor hora PJ", "Horas por dia útil", "Reserva preventiva tributos", "Percentual de referência recuperação", "Meta parcelas e recorrências", "Meta alimentação/imprevistos", "Provisão alimentação/imprevistos", "Início da provisão"];
    private static readonly HashSet<string> ReferenceParameters = ["Meta mensal cartões", "Reserva Crediário Solar", "Saldo inicial", "Mês inicial", "Horizonte em meses", "Meta mensal fixos PF", "Meta mensal total PF"];
    private const string Remote = "LAN-BL-AMZ-701-9749415-1047439";
    private readonly record struct Row(string Sheet, int Number, SpreadsheetCell[] Cells)
    {
        public string T(int column) => column < Cells.Length ? Cells[column].Text : "";
        public SpreadsheetCell C(int column) => Cells[column];
        public string Location => $"'{Sheet}'!A{Number}:{(Sheet == "Lançamentos" ? "Q" : Sheet == "Parâmetros" ? "D" : Sheet == "Histórico compromissos 08-10" ? "J" : "L")}{Number}";
        public string Id => Sheet == "Lançamentos" ? T(11) : T(0);
    }

    public SpreadsheetImportPlan Map(Guid workspaceId, SpreadsheetSnapshot snapshot)
    {
        items.Clear();
        if (workspaceId == Guid.Empty) throw new ArgumentException("ExplicitWorkspaceRequired");
        var launches = Rows(snapshot, "Lançamentos");
        var fixedRows = Rows(snapshot, "Despesas Fixas");
        Header(snapshot, "Lançamentos", 11, "ID"); Header(snapshot, "Lançamentos", 12, "Tipo");
        Header(snapshot, "Despesas Fixas", 0, "ID"); Header(snapshot, "Metas Mensais", 4, "Meta recuperação aprovada");
        Header(snapshot, "Movimentos Conta", 5, "Saldo após movimento");
        foreach (var group in launches.Concat(fixedRows).GroupBy(r => r.Id, StringComparer.Ordinal))
            if (group.Key.Length == 0 || group.Count() != 1) Issue("SOURCE:" + group.Key, "DuplicateSourceId", group.First().Location);
        foreach (var category in Categories)
            Add("CATEGORY:" + category, SpreadsheetEntity.Category, new(category), "T09.2 §5");
        Add("ACCOUNT:ItauPF", SpreadsheetEntity.Account, new("Itaú PF", -13826.92m, new(2026, 10, 8), Institution: "Itaú", Scope: "PF"), "T09.2 §4 / 'Movimentos Conta'!F9");
        Add("ACCOUNT:ItauEmpresas", SpreadsheetEntity.Account, new("Itaú Empresas", Institution: "Itaú", Scope: "PJ"), "T09.2 §4");
        foreach (var card in new[] { "Black", "Nubank", "Credicard", "Zaffari/Mercado" })
        {
            // Brand alone does not establish the issuer of Credicard/Zaffari. Never search other private documents.
            var explicitIssuers = launches.Where(r => r.T(7) == card).SelectMany(r => Regex.Matches(r.T(10), @"Emissor confirmado:\s*([^;\r\n]+);", RegexOptions.IgnoreCase).Select(m => m.Groups[1].Value.Trim())).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var issuer = card switch
            {
                "Black" when launches.Any(r => r.T(7) == card && r.T(10).Contains("PDF Itaú", StringComparison.Ordinal)) => "Itaú",
                "Nubank" when launches.Any(r => r.T(7) == card) => "Nubank",
                _ => null
            };
            var ambiguous = explicitIssuers.Length > 1 || explicitIssuers.Length == 1 && issuer is not null && !issuer.Equals(explicitIssuers[0], StringComparison.OrdinalIgnoreCase);
            issuer ??= explicitIssuers.Length == 1 ? explicitIssuers[0] : null;
            Add(CardRef(card), SpreadsheetEntity.Card, new(card, 10000m, Institution: issuer, Day: 5, Number: 10),
                "T09.2 §6 / 'Lançamentos'!H1:H100", status: issuer is null || ambiguous ? BootstrapDisposition.Invalid : BootstrapDisposition.Created,
                reason: ambiguous ? "AmbiguousIssuerEvidence" : issuer is null ? "MissingIssuerEvidence" : "ApprovedCalendarAndLimit;IssuerSourceEvidence");
        }
        foreach (var r in fixedRows) Safe(r, () => MapFixed(r));
        foreach (var r in launches.Where(r => r.T(12) != "Parcelado")) Safe(r, () => MapLaunch(r));
        foreach (var group in launches.Where(r => r.T(12) == "Parcelado").GroupBy(r => GroupId(r.Id), StringComparer.Ordinal))
            Safe(group.First(), () => MapInstallments(group.Key, group.OrderBy(r => r.C(1).Date()).ToArray()));
        foreach (var r in Rows(snapshot, "Histórico compromissos 08-10"))
        {
            if (launches.Count(l => l.Id == r.Id) != 1) Issue("HISTORY:" + r.Id, "HistoryIdentityNotUniqueInLaunches", r.Location);
            else Exclude("HISTORY:" + r.Id, "Histórico repetido", "AlreadyRepresentedInLaunches", r.Location);
        }
        foreach (var r in Rows(snapshot, "Movimentos Conta")) Safe(r, () =>
        {
            if (r.C(0).Date() > new DateOnly(2026, 10, 8)) Issue("MOVEMENT:" + r.Number, "MovementOutsideApprovedOpeningSnapshot", r.Location);
            else Exclude("MOVEMENT:" + r.Number, "Movimento absorvido no saldo-base", "AbsorbedInOpeningBalance", r.Location);
        });
        var movements = Rows(snapshot, "Movimentos Conta");
        if (movements.Count == 0 || movements[^1].C(5).Decimal() != -13826.92m || movements[^1].C(0).Date() != new DateOnly(2026, 10, 8))
            Issue("OPENING:PF", "OpeningSnapshotChanged", "'Movimentos Conta'!A1:L10");
        foreach (var r in Rows(snapshot, "Metas Mensais")) Safe(r, () => MapTargets(r));
        if (Rows(snapshot, "Metas Mensais").Count != 12) Issue("TARGETS", "ExpectedTwelveCompetences", "'Metas Mensais'!A1:L13");
        foreach (var r in Rows(snapshot, "Parâmetros"))
        {
            if (UnsupportedParameters.Contains(r.T(0)) || ReferenceParameters.Contains(r.T(0)))
                Exclude("PARAMETER:" + r.T(0), r.T(0), UnsupportedParameters.Contains(r.T(0)) ? "UnsupportedButKnown:NoFinancialAggregate;NotAnAdditionalExpenseOrIncome" :
                    r.T(0) == "Saldo inicial" ? "SupersededOpeningSnapshot" : "ReferenceOnly:AlreadyMappedOrNotAFact", r.Location, SpreadsheetEntity.Parameter);
            else items.Add(new("PARAMETER:" + r.T(0), SpreadsheetEntity.Parameter, new(r.T(0)), r.Location, Status: BootstrapDisposition.Unmapped, Reason: "NeedsReview:UnknownParameter"));
        }
        // Explicit source coverage: a missing row cannot silently produce a shorter import.
        foreach (var id in BankRecurrences.Concat(SpecificPlans).Append("PF-007"))
            if (fixedRows.Count(r => r.Id == id) != 1) Issue("EXPECTED:" + id, "MissingOrDuplicateExpectedId", "Despesas Fixas");
        foreach (var id in CardRecurrences.Concat(Singles).Append(Remote))
            if (launches.Count(r => r.Id == id) != 1) Issue("EXPECTED:" + id, "MissingOrDuplicateExpectedId", "Lançamentos");
        if (launches.Count(r => r.T(12) == "Parcelado") != 34) Issue("INSTALLMENT_ROWS", "ExpectedThirtyFourInstallmentRows", "Lançamentos");
        if (items.Where(i => i.IsEntity).GroupBy(i => i.ExternalReference).Any(g => g.Count() > 1)) Issue("PROVENANCE", "DuplicatePlannedProvenance", "Plan");
        return new(workspaceId, snapshot.LogicalHash, snapshot.ReadAtUtc, items.ToArray());
    }

    private void MapFixed(Row r)
    {
        if (r.Id == "PF-007") { Exclude(r.Id, "Plano de saúde inativo", "InactiveByApprovedDecision", r.Location); return; }
        if (r.T(10) != "Sim" || r.T(9).Length != 0) throw new FormatException("UnexpectedInactiveOrCardFixedRow");
        var start = r.C(6).Date(); var end = r.T(7).Length == 0 ? (DateOnly?)null : r.C(7).Date();
        var day = r.T(5).Length == 0 ? 10 : r.C(5).Integer();
        if (day is < 1 or > 31) throw new FormatException("InvalidDueDay");
        var date = Day(start, day);
        var account = r.T(1) switch { "PF" => "ACCOUNT:ItauPF", "PJ" => "ACCOUNT:ItauEmpresas", _ => throw new FormatException("UnknownAccountScope") };
        var estimated = IsEstimated(r.T(11));
        var payload = new SpreadsheetPayload(r.T(2), Money(r.C(4)), date, account, Category: CategoryRef(r.T(3)), EndDate: end, Day: day, Competence: Month(start), IsEstimated: estimated);
        if (SpecificPlans.Contains(r.Id)) Add(r.Id + ":planned", SpreadsheetEntity.PlannedTransaction, payload, r.Location, JsonSerializer.Serialize(new { estimated, provision = r.Id == "PF-TAX-202702" }));
        else if (BankRecurrences.Contains(r.Id)) Add(r.Id + ":recurrence", SpreadsheetEntity.AccountRecurrence, payload, r.Location);
        else Issue(r.Id, "UnapprovedFixedRow", r.Location);
    }

    private void MapLaunch(Row r)
    {
        if (r.Id == Remote) { Exclude(r.Id, "Controle remoto — R$ 33,90", "NeedsReview:ExcludedByApprovedDecision", r.Location); return; }
        if (r.T(3) == "Ignorar") { Exclude(r.Id, "Exemplo Ignorar", "ExampleNotAFact", r.Location); return; }
        if (r.T(7).Length == 0)
        {
            if (r.T(0).Length > 0 && r.C(0).Date() <= new DateOnly(2026, 10, 8)) Exclude(r.Id, "Histórico bancário absorvido", "AbsorbedInOpeningBalance", r.Location);
            else Issue(r.Id, "UnapprovedNonCardLaunch", r.Location);
            return;
        }
        var card = CardRef(r.T(7)); var category = CategoryRef(r.T(4)); var competence = r.C(1).Date();
        if (competence.Day != 1) throw new FormatException("CompetenceMustBeFirstDay");
        if (r.T(16) != "Sim") throw new FormatException("UnexpectedInactiveCardRow");
        if (CardRecurrences.Contains(r.Id))
        {
            if (r.T(12) != "Recorrente") throw new FormatException("SourceTypeChanged");
            // Actual observed charge dates, not the competence's first day. Evidence is in the source notes.
            var evidence = r.Id switch { "REC-001" => "05/10", "REC-002" => "27/09", "REC-BL-KINDLE-001" => "23/09", "REC-BL-ADFREE-001" => "12/09", _ => null };
            DateOnly? charge = null;
            if (r.T(0).Length > 0) charge = r.C(0).Date();
            else if (evidence is not null && r.T(10).Contains(evidence, StringComparison.Ordinal) && competence.Year == 2026 && competence.Month == 10)
                charge = DateOnly.ParseExact(evidence + "/2026", "dd/MM/yyyy", CultureInfo.InvariantCulture);
            var valid = charge.HasValue && CreditCardInvoiceCalendarCalculator.ForInstallment(charge.Value, 1, 5, 10).Competence == competence.ToString("yyyy-MM");
            Add(r.Id + ":recurrence", SpreadsheetEntity.CardRecurrence,
                new(r.T(5), Money(r.C(8)), charge, Card: card, Category: category, Day: charge?.Day, Competence: competence,
                    IsEstimated: IsEstimated(r.T(10))), r.Location,
                JsonSerializer.Serialize(new { chargeDateEvidence = evidence, expectedInvoiceCompetence = competence }),
                valid ? BootstrapDisposition.Created : BootstrapDisposition.Invalid,
                charge is null ? "MissingChargeDate:CannotValidateInvoiceCompetence" : valid ? "ObservedChargeDate;CalendarValidated" : "InvoiceCompetenceMismatch");
        }
        else if (Singles.Contains(r.Id))
        {
            if (r.T(12) != "Único") throw new FormatException("SourceTypeChanged");
            var date = r.C(0).Date();
            CreatePurchase(r.Id, r.T(5), card, category, date, Money(r.C(8)), [new(1, competence, Money(r.C(8)), r.Id)], r.Location);
        }
        else Issue(r.Id, "UnapprovedSingleOrRecurrence", r.Location);
    }

    private sealed record Occurrence(int Number, DateOnly Competence, decimal Amount, string SourceId, string? Location = null);
    private void MapInstallments(string groupId, Row[] rows)
    {
        var first = rows[0]; var card = CardRef(first.T(7)); var category = CategoryRef(first.T(4));
        if (rows.Any(r => CardRef(r.T(7)) != card || CategoryRef(r.T(4)) != category)) throw new FormatException("SplitGroupReferenceMismatch");
        var occurrences = new List<Occurrence>(); int? totalCount = null;
        foreach (var r in rows)
        {
            if (r.T(16) != "Sim") throw new FormatException("UnexpectedInactiveCardRow");
            // A note may describe a future continuation (Kalunga 2/3) of the row's current 1/3.
            // Prefer the explicit installment in the description; do not parse a contract date there.
            var fraction = Regex.Match(r.T(5), @"parcelas?\s+(\d{1,3})/(\d{1,3})(?![\d/])", RegexOptions.IgnoreCase);
            if (!fraction.Success) fraction = Regex.Match(r.T(10), @"(?<![\d/])(\d{1,3})/(\d{1,3})(?![\d/])");
            var count = r.C(13).Integer();
            var number = fraction.Success ? int.Parse(fraction.Groups[1].Value) : 1;
            var knownCount = fraction.Success ? int.Parse(fraction.Groups[2].Value) : (int?)null;
            // Complete rows with explicit total/count in prose still carry a full known schedule.
            if (groupId is "PAR-BL-021" or "PAR-NU-005" or "PAR-NU-006" or "PAR-BL-023") { number = 1; knownCount = count; }
            else if (!fraction.Success) throw new FormatException("MissingExplicitInstallmentNumber");
            if (number < 1 || count < 1 || count > 120 || knownCount is < 1 or > 120 || knownCount.HasValue && number + count - 1 > knownCount)
                throw new FormatException("InvalidInstallmentSequence");
            if (totalCount.HasValue && knownCount.HasValue && totalCount != knownCount) throw new FormatException("SplitGroupCountMismatch");
            totalCount ??= knownCount;
            var month = r.C(1).Date(); if (month.Day != 1) throw new FormatException("CompetenceMustBeFirstDay");
            for (var i = 0; i < count; i++) occurrences.Add(new(number + i, month.AddMonths(i), Money(r.C(8)), r.Id, r.Location));
            if (r.T(14).Length > 0 && Month(r.C(14).Date()) != month.AddMonths(count - 1)) throw new FormatException("ExplicitEndMismatch");
        }
        if (occurrences.Select(o => o.Number).Distinct().Count() != occurrences.Count || occurrences.Select(o => o.Competence).Distinct().Count() != occurrences.Count)
            throw new FormatException("OverlappingKnownSchedule");
        decimal? originalTotal = null; DateOnly? originalDate = null;
        foreach (var r in rows)
        {
            var match = Regex.Match(r.T(10), @"(?:^|\.\s*)(?:Compra total\s+R\$|Total(?: original)?\s*:?\s*R\$)\s*([\d.]+,\d{2})", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                var parsed = decimal.Parse(match.Groups[1].Value, CultureInfo.GetCultureInfo("pt-BR"));
                if (originalTotal.HasValue && originalTotal != parsed) throw new FormatException("SplitOriginalTotalMismatch");
                originalTotal = parsed;
            }
            DateOnly? date = r.T(0).Length > 0 ? r.C(0).Date() : null;
            var dateMatch = Regex.Match(r.T(10), @"Compra informada em (\d{2}/\d{2}/\d{4})", RegexOptions.IgnoreCase);
            if (dateMatch.Success) date = DateOnly.ParseExact(dateMatch.Groups[1].Value, "dd/MM/yyyy", CultureInfo.InvariantCulture);
            if (date.HasValue && originalDate.HasValue && date != originalDate) throw new FormatException("SplitOriginalDateMismatch");
            originalDate ??= date;
        }
        var full = originalTotal.HasValue && originalDate.HasValue && totalCount == occurrences.Count && occurrences[0].Number == 1
            && occurrences.Sum(o => o.Amount) == originalTotal;
        var compatible = full && occurrences.All(o =>
        {
            var cents = decimal.ToInt64(originalTotal!.Value * 100); var amount = (cents / totalCount!.Value + (o.Number <= cents % totalCount.Value ? 1 : 0)) / 100m;
            return o.Amount == amount && CreditCardInvoiceCalendarCalculator.ForInstallment(originalDate!.Value, o.Number, 5, 10).Competence == o.Competence.ToString("yyyy-MM");
        });
        if (compatible) CreatePurchase(first.Id, first.T(5), card, category, originalDate!.Value, originalTotal!.Value, occurrences, first.Location);
        else foreach (var o in occurrences)
            Add(o.SourceId + ":installment:" + o.Number, SpreadsheetEntity.KnownInstallment,
                new(rows.Single(r => r.Id == o.SourceId).T(5), o.Amount, Card: card, Category: category, Number: o.Number, Count: totalCount, Competence: o.Competence,
                    Day: 10, IsEstimated: Regex.IsMatch(first.T(10), "estima", RegexOptions.IgnoreCase)),
                rows.Single(r => r.Id == o.SourceId).Location,
                JsonSerializer.Serialize(new { sourceId = o.SourceId, mode = "KnownObligation", originalPurchaseNotReconstructed = true }),
                reason: groupId == "PAR-NU-004" ? "ExplicitUnequalSchedule:111.78/111.76/111.76" : "KnownHistoricalInstallment;NoOriginalTotalOrDateInferred");
    }

    private void CreatePurchase(string id, string name, string card, string category, DateOnly date, decimal total, IReadOnlyList<Occurrence> schedule, string location)
    {
        var valid = schedule.All(o => CreditCardInvoiceCalendarCalculator.ForInstallment(date, o.Number, 5, 10).Competence == o.Competence.ToString("yyyy-MM"));
        var purchaseRef = id + ":purchase";
        Add(purchaseRef, SpreadsheetEntity.Purchase, new(name, total, date, Card: card, Category: category, Count: schedule.Count), location,
            JsonSerializer.Serialize(new { sourceIds = schedule.Select(o => o.SourceId).Distinct(StringComparer.Ordinal).ToArray() }),
            status: valid ? BootstrapDisposition.Created : BootstrapDisposition.Invalid, reason: valid ? "CompletePurchase;DomainCalendarValidated" : "InvoiceCompetenceMismatch");
        foreach (var o in schedule)
        {
            var invoiceRef = card + ":invoice:" + o.Competence.ToString("yyyy-MM");
            if (!items.Any(i => i.ExternalReference == invoiceRef))
                Add(invoiceRef, SpreadsheetEntity.Invoice, new("Fatura derivada de compra", Card: card, Date: date, Number: o.Number, Competence: o.Competence), location);
            Add(o.SourceId + ":installment:" + o.Number, SpreadsheetEntity.Installment,
                new(name, o.Amount, Card: card, Category: category, Number: o.Number, Count: schedule.Count, Competence: o.Competence, Purchase: purchaseRef, Invoice: invoiceRef), o.Location ?? location);
        }
    }

    private void MapTargets(Row r)
    {
        var month = r.C(0).Date();
        if (month != new DateOnly(2026, 10, 1).AddMonths(r.Number - 2)) throw new FormatException("UnexpectedTargetCompetence");
        for (var col = 1; col <= 4; col++)
        {
            if (r.C(col).Formula is not null || r.C(col).RawNumber is null) throw new FormatException("TargetMustBeLiteral");
            var type = col switch { 1 => MonthlyAggregateTargetType.FixedExpenses, 2 => MonthlyAggregateTargetType.CreditCards, 3 => MonthlyAggregateTargetType.TotalExpenses, _ => MonthlyAggregateTargetType.RecoveryAmount };
            var recovery = col == 4 ? ApprovedRecoverySnapshot.FromRaw(r.C(col).RawNumber!) : null;
            var amount = recovery?.Amount ?? Money(r.C(col));
            if (col < 4 && amount != (col == 1 ? 7000 : col == 2 ? 5000 : 12000)) throw new FormatException("ApprovedTargetChanged");
            Add($"META-{month:yyyy-MM}:{type}", SpreadsheetEntity.MonthlyAggregateTarget, new(type.ToString(), amount, Competence: month, TargetType: type), r.Location, recovery?.MetadataJson);
        }
    }

    private static List<Row> Rows(SpreadsheetSnapshot s, string name) => s.Sheet(name).Data.Select(r => new Row(name, r.Number, r.Cells)).ToList();
    private void Header(SpreadsheetSnapshot s, string sheet, int column, string expected)
    { if (s.Sheet(sheet).Rows[0][column].Text != expected) throw new InvalidOperationException("SourceLayoutChanged"); }
    private static DateOnly Month(DateOnly d) => new(d.Year, d.Month, 1);
    private static DateOnly Day(DateOnly d, int day) => new(d.Year, d.Month, Math.Min(day, DateTime.DaysInMonth(d.Year, d.Month)));
    private static decimal Money(SpreadsheetCell c)
    { var value = c.Decimal(); if (value <= 0 || decimal.Round(value, 2) != value) throw new FormatException("InvalidMoney"); return value; }
    private static bool IsEstimated(string note) => !Regex.IsMatch(note, "Real conhecido|Recorrência confirmada", RegexOptions.IgnoreCase)
        && Regex.IsMatch(note, "estima|provis|câmbio", RegexOptions.IgnoreCase);
    private static string CategoryRef(string name) => "CATEGORY:" + (Categories.SingleOrDefault(c => c.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? throw new FormatException("UnknownCategory"));
    private static string CardRef(string name) => "CARD:" + (name switch { "Black" => "Black", "Nubank" or "Nu Empresas" => "Nubank", "Credicard" => "Credicard", "Zaffari/Mercado" or "Zaffari" => "Zaffari/Mercado", _ => throw new FormatException("UnknownCard") });
    private static string GroupId(string id) => id switch { "PAR-NU-004-OUT" or "PAR-NU-004-FUT" => "PAR-NU-004", "PAR-BL-022-01" or "PAR-BL-022-02" => "PAR-BL-022", _ => id };
    private void Add(string id, SpreadsheetEntity kind, SpreadsheetPayload payload, string location, string? metadata = null,
        BootstrapDisposition status = BootstrapDisposition.Created, string reason = "CreatePlanned") => items.Add(new(id, kind, payload, location, metadata, status, reason));
    private void Exclude(string id, string name, string reason, string location, SpreadsheetEntity kind = SpreadsheetEntity.Exclusion)
        => items.Add(new(id, kind, new(name), location, Status: BootstrapDisposition.Unmapped, Reason: reason, ExcludedByApprovedDecision: true));
    private void Issue(string id, string reason, string location) => Add(id, SpreadsheetEntity.Validation, new("Preflight"), location, status: BootstrapDisposition.Invalid, reason: reason);
    private void Safe(Row r, Action action)
    {
        var start = items.Count;
        try { action(); }
        catch (Exception e) when (e is FormatException or ArgumentException or OverflowException or IndexOutOfRangeException or InvalidOperationException)
        { items.RemoveRange(start, items.Count - start); Issue(r.Id, e is FormatException && e.Message.All(char.IsAsciiLetter) ? e.Message : "InvalidSourceRow", r.Location); }
    }
}
