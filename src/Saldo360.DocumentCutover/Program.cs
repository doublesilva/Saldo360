using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Saldo360.Infrastructure.Persistence;
using Saldo360.Infrastructure.Workspaces;

// Separate offline tool; never registered as an HTTP endpoint or invoked during deploy/reset.
try
{
    var allowed = new HashSet<string> { "--dry-run", "--apply", "--workspace-id", "--expected-count", "--plan", "--maintenance-window-confirmed", "--backup-confirmed" };
    var flags = new Dictionary<string, string?>();
    for (var i = 0; i < args.Length; i++)
    {
        var key = args[i];
        if (!allowed.Contains(key) || flags.ContainsKey(key)) throw new InvalidOperationException("InvalidArguments");
        flags[key] = key is "--workspace-id" or "--expected-count" or "--plan"
            ? ++i < args.Length ? args[i] : throw new InvalidOperationException("MissingArgument") : null;
    }
    if (flags.ContainsKey("--dry-run") == flags.ContainsKey("--apply")
        || !flags.TryGetValue("--workspace-id", out var workspaceText) || !Guid.TryParse(workspaceText, out var workspace) || workspace == Guid.Empty
        || !flags.TryGetValue("--expected-count", out var countText) || !int.TryParse(countText, out var count) || count <= 0
        || !flags.TryGetValue("--plan", out var planFile) || string.IsNullOrWhiteSpace(planFile))
        throw new InvalidOperationException("ExplicitModeWorkspaceCountAndPlanRequired");
    var connection = Environment.GetEnvironmentVariable("ConnectionStrings__Saldo360");
    var root = Environment.GetEnvironmentVariable("FinancialDocuments__StoragePath");
    if (string.IsNullOrWhiteSpace(connection) || string.IsNullOrWhiteSpace(root)) throw new InvalidOperationException("ExplicitConnectionAndStorageRootRequired");
    if (flags.ContainsKey("--dry-run"))
    {
        var readOnly = new NpgsqlConnectionStringBuilder(connection);
        readOnly.Options = (readOnly.Options + " -c default_transaction_read_only=on").Trim();
        connection = readOnly.ConnectionString;
    }
    var options = new DbContextOptionsBuilder<Saldo360DbContext>();
    Saldo360DbContext.Configure(options, connection);
    await using var db = new Saldo360DbContext(options.Options);
    var tool = new FinancialDocumentWorkspaceCutover(db, root);
    var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
    if (flags.ContainsKey("--dry-run"))
    {
        if (File.Exists(planFile)) throw new InvalidOperationException("PlanAlreadyExists");
        var readOnly = await db.Database.SqlQueryRaw<string>("SELECT current_setting('default_transaction_read_only') AS \"Value\"").SingleAsync();
        if (readOnly != "on") throw new InvalidOperationException("ReadOnlyConnectionRequired");
        var plan = await tool.DryRunAsync(workspace, count, CancellationToken.None);
        await using var output = new FileStream(planFile, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await JsonSerializer.SerializeAsync(output, plan, json); await output.FlushAsync(); output.Flush(true);
        Console.WriteLine(JsonSerializer.Serialize(new { mode = "DryRun", databaseReadOnly = true, documents = plan.Count, legacyCandidates = plan.Count(x => x.SourceStorageReference != x.TargetStorageReference), financialMutationExecuted = false, filesCopied = false }, json));
    }
    else
    {
        if (!flags.ContainsKey("--maintenance-window-confirmed") || !flags.ContainsKey("--backup-confirmed")) throw new InvalidOperationException("MaintenanceAndBackupApprovalRequired");
        await using var input = File.OpenRead(planFile);
        var plan = await JsonSerializer.DeserializeAsync<List<DocumentCutoverEntry>>(input, json) ?? throw new InvalidOperationException("InvalidPlan");
        Console.WriteLine(JsonSerializer.Serialize(await tool.ApplyAsync(workspace, count, plan, true, CancellationToken.None), json));
    }
    return 0;
}
catch (Exception error)
{
    // Do not print connection strings, SQL parameters, document contents or provider messages.
    Console.Error.WriteLine("Document cutover refused/failed; review state before retry. ErrorType=" + error.GetType().Name);
    if (error is InvalidOperationException && error.Message.All(char.IsAsciiLetter)) Console.Error.WriteLine("Code=" + error.Message);
    return 1;
}
