using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Saldo360.Application.Bootstrap;
using Saldo360.Infrastructure.Bootstrap;
using Saldo360.Infrastructure.Persistence;

// Administrative CLI only. Never called by API, deployment, login, scheduler or a reset.
try
{
    var switches = new HashSet<string> { "--dry-run", "--execute" };
    var values = new HashSet<string> { "--workspace-id", "--source", "--output", "--approved-report", "--approved-report-hash", "--fresh-source", "--destination-snapshot" };
    var flags = new Dictionary<string, string?>();
    for (var i = 0; i < args.Length; i++)
    {
        var key = args[i];
        if ((!switches.Contains(key) && !values.Contains(key)) || flags.ContainsKey(key)) throw new InvalidOperationException("InvalidArguments");
        flags[key] = switches.Contains(key) ? null : ++i < args.Length ? args[i] : throw new InvalidOperationException("MissingArgument");
    }
    string Required(string name) => flags.GetValueOrDefault(name) is { Length: > 0 } value ? value : throw new InvalidOperationException("MissingExplicitArgument");
    if (flags.ContainsKey("--dry-run") == flags.ContainsKey("--execute") || !Guid.TryParse(Required("--workspace-id"), out var w) || w == Guid.Empty)
        throw new InvalidOperationException("ExplicitModeAndWorkspaceRequired");
    var source = SpreadsheetSnapshot.Parse(await File.ReadAllTextAsync(Required("--source")));
    async Task<int> WriteReport(SpreadsheetDryRunResult report)
    {
        var output = Required("--output");
        await using var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await JsonSerializer.SerializeAsync(file, report, SpreadsheetSnapshot.Json); await file.FlushAsync(); file.Flush(true);
        Console.WriteLine(JsonSerializer.Serialize(new { mode = "DryRun", report.WorkspaceId, report.SourceHash, report.PlanHash, report.ReportHash,
            report.DatabaseReadOnly, report.DestinationMode, report.CanExecute, report.CountsByAggregate, report.CountsByStatus, report.ProvenanceRecords,
            report.PlannedNewProvenanceRecords, financialMutationExecuted = false }, SpreadsheetSnapshot.Json));
        return 0;
    }
    if (flags.ContainsKey("--destination-snapshot"))
    {
        if (!flags.ContainsKey("--dry-run") || flags.Keys.Any(k => k is "--approved-report" or "--approved-report-hash" or "--fresh-source")) throw new InvalidOperationException("DestinationSnapshotIsDryRunOnly");
        if (File.Exists(Required("--output"))) throw new InvalidOperationException("OutputAlreadyExists");
        var destination = ReadOnlyBudgetDestinationSnapshot.Parse(await File.ReadAllTextAsync(Required("--destination-snapshot")));
        return await WriteReport(destination.DryRun(w, source));
    }
    var connection = Environment.GetEnvironmentVariable("ConnectionStrings__Saldo360");
    if (string.IsNullOrWhiteSpace(connection)) throw new InvalidOperationException("ExplicitConnectionRequired");
    if (flags.ContainsKey("--dry-run"))
    {
        if (flags.Keys.Any(k => k is "--approved-report" or "--approved-report-hash" or "--fresh-source")) throw new InvalidOperationException("UnexpectedExecuteArguments");
        var readOnly = new NpgsqlConnectionStringBuilder(connection);
        readOnly.Options = (readOnly.Options + " -c default_transaction_read_only=on").Trim();
        connection = readOnly.ConnectionString;
    }
    var options = new DbContextOptionsBuilder<Saldo360DbContext>(); Saldo360DbContext.Configure(options, connection);
    await using var db = new Saldo360DbContext(options.Options);
    var importer = new BudgetSpreadsheetImporter(db, TimeProvider.System);
    if (flags.ContainsKey("--dry-run"))
    {
        var output = Required("--output"); if (File.Exists(output)) throw new InvalidOperationException("OutputAlreadyExists");
        return await WriteReport(await importer.DryRunAsync(w, source)); // A blocked report is a successful dry-run, never execution approval.
    }
    else
    {
        if (flags.ContainsKey("--output")) throw new InvalidOperationException("UnexpectedDryRunArguments");
        var approval = JsonSerializer.Deserialize<SpreadsheetDryRunResult>(await File.ReadAllTextAsync(Required("--approved-report")), SpreadsheetSnapshot.Json)
            ?? throw new InvalidOperationException("InvalidApprovedReport");
        var fresh = SpreadsheetSnapshot.Parse(await File.ReadAllTextAsync(Required("--fresh-source")));
        var count = await importer.ExecuteApprovedAsync(w, source, fresh, approval, Required("--approved-report-hash"));
        Console.WriteLine(JsonSerializer.Serialize(new { mode = "Execute", workspaceId = w, createdEntities = count }, SpreadsheetSnapshot.Json));
        return 0;
    }
}
catch (Exception error)
{
    // Never print provider error messages, connection strings, source rows or environment variables.
    Console.Error.WriteLine("Budget bootstrap refused/failed. ErrorType=" + error.GetType().Name);
    if (error is InvalidOperationException && error.Message.All(char.IsAsciiLetter)) Console.Error.WriteLine("Code=" + error.Message);
    return 1;
}
