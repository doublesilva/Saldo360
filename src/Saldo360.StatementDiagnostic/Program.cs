using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Saldo360.Application;
using Saldo360.Application.CreditCards;
using Saldo360.Infrastructure;
using Saldo360.Infrastructure.CreditCards;

// Local administrative tool. No web listener, hosted service startup, Gmail or financial writer.
try
{
    if (args.Length == 1 && args[0] == "--help")
    {
        Console.WriteLine("--allow-openai --workspace-id GUID --user-id GUID --card-id GUID --document-id GUID --output NEW_PRIVATE_JSON [--password-file PRIVATE_FILE]");
        return 0;
    }
    var flags = new Dictionary<string, string>();
    var allowed = new HashSet<string> { "--workspace-id", "--user-id", "--card-id", "--document-id", "--output", "--password-file" };
    var consent = false;
    for (var i = 0; i < args.Length; i++)
    {
        if (args[i] == "--allow-openai" && !consent) { consent = true; continue; }
        if (!allowed.Contains(args[i]) || flags.ContainsKey(args[i]) || i + 1 >= args.Length) throw new ArgumentException();
        flags.Add(args[i], args[++i]);
    }
    Guid Id(string name) => flags.TryGetValue(name, out var value) && Guid.TryParse(value, out var id) && id != Guid.Empty ? id : throw new ArgumentException();
    var workspace = Id("--workspace-id"); var user = Id("--user-id"); var card = Id("--card-id"); var document = Id("--document-id");
    if (!consent || workspace != CardEmailSearchAuthorization.AuthorizedPersonalId || !flags.TryGetValue("--output", out var output)) throw new ArgumentException();
    var config = new ConfigurationBuilder().AddEnvironmentVariables().Build();
    var keyFile = config["CreditCardStatementAgent:ApiKeyFile"];
    var root = config["FinancialDocuments:StoragePath"];
    if (string.IsNullOrWhiteSpace(keyFile) || string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(config.GetConnectionString("Saldo360"))) throw new ArgumentException();
    if (config["CreditCardStatementAgent:Model"] is { } model && model != "gpt-4o-mini") throw new ArgumentException();
    // Scoped to this standalone process. Never change API, Compose, .env, scheduler or global flags.
    var settings = new Dictionary<string, string?>
    {
        ["CreditCardStatementAgent:Enabled"] = "true", ["CreditCardStatementAgent:Mode"] = "AgentPrimary",
        ["CreditCardStatementAgent:Provider"] = "OpenAI", ["CreditCardStatementAgent:Model"] = "gpt-4o-mini",
        ["CreditCardStatementAgent:Endpoint"] = "https://api.openai.com/v1/", ["CreditCardStatementAgent:ApiKey"] = ""
    };
    var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true, EnvironmentName = "Production" });
    builder.Configuration.AddConfiguration(config).AddInMemoryCollection(settings);
    builder.Logging.ClearProviders();
    builder.Services.AddApplication().AddInfrastructure(builder.Configuration);
    builder.Services.AddSingleton<IFinancialDocumentStorage>(new StatementDiagnosticReadOnlyStorage(root));
    builder.Services.AddScoped<StoredStatementDiagnostic>();
    using var host = builder.Build(); // Deliberately never Start/Run hosted services.
    using var scope = host.Services.CreateScope();
    string? password = null;
    if (flags.TryGetValue("--password-file", out var passwordFile))
    {
        if (new FileInfo(passwordFile).Length > 512) throw new ArgumentException();
        password = (await File.ReadAllTextAsync(passwordFile)).TrimEnd('\r', '\n');
        if (password.Length > 128) throw new ArgumentException();
    }
    var fileOptions = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
    if (!OperatingSystem.IsWindows()) fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    await using var reportFile = new FileStream(output, fileOptions); // Fail before any paid request if output is unavailable/existing.
    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
    var report = await scope.ServiceProvider.GetRequiredService<StoredStatementDiagnostic>().RunAsync(workspace, document, card, user, password, timeout.Token);
    await JsonSerializer.SerializeAsync(reportFile, report, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
    Console.WriteLine(JsonSerializer.Serialize(new { report.Status, report.Source, report.Provider, report.Model, report.DatabaseReadOnly, report.FailureCode,
        extractionValidation = report.Extraction?.ValidationStatus, reconciliation = report.Extraction?.Primary?.Reconciliation.Status,
        documentWarnings = report.Extraction?.DocumentWarnings, report.ConsolidationStatus, report.ConsolidationBlockReason,
        financialMutationExecuted = false, inboxMutationExecuted = false }));
    return report.Status == "Reliable" ? 0 : 2;
}
catch (ArgumentException) { Console.Error.WriteLine("InvalidExplicitDiagnosticArgumentsOrConfiguration; use --help. No credentials are accepted on the command line."); return 1; }
catch (OperationCanceledException) { Console.Error.WriteLine("DiagnosticCancelledOrTimedOut"); return 1; }
catch (Exception) { Console.Error.WriteLine("DiagnosticUnavailable; verify database, private storage, output and credential file permissions. Details suppressed."); return 1; }
