using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Security.AccessControl;
using System.Security.Principal;
using Saldo360.Application.CreditCards;
using Saldo360.Infrastructure.CreditCards;

try
{
    if (args.SequenceEqual(new[] { "--help" }))
    {
        Console.WriteLine("--allow-openai --input PDF_OR_DIRECTORY --output-dir NEW_PRIVATE_DIRECTORY --repository-root REPO --key-file PRIVATE_FILE | --key-env-file EXPLICIT_ENV_FILE [--password-file PRIVATE_FILE] [--model gpt-4o-mini] [--max-calls 1] [--ocr-image saldo360-ocr-tests:local]"); return 0;
    }
    var flags = new Dictionary<string, string>(StringComparer.Ordinal); var allowed = new HashSet<string>(["--input", "--output-dir", "--repository-root", "--key-file", "--key-env-file", "--password-file", "--model", "--max-calls", "--ocr-image"]);
    var consent = false;
    for (var i = 0; i < args.Length; i++)
    {
        if (args[i] == "--allow-openai" && !consent) { consent = true; continue; }
        if (!allowed.Contains(args[i]) || flags.ContainsKey(args[i]) || i + 1 >= args.Length) throw new ArgumentException();
        flags.Add(args[i], args[++i]);
    }
    if (!consent) throw new ArgumentException(); // Before any input/credential read or output creation.
    var repository = Path.GetFullPath(flags["--repository-root"]).TrimEnd(Path.DirectorySeparatorChar);
    if (!File.Exists(Path.Combine(repository, "Saldo360.slnx"))) throw new ArgumentException();
    bool InsideRepository(string path) => Path.GetFullPath(path).StartsWith(repository + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || Path.GetFullPath(path) == repository;
    void RejectLinkedPath(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint)) throw new ArgumentException();
    }
    var output = Path.GetFullPath(flags["--output-dir"]);
    RejectLinkedPath(output);
    if (InsideRepository(output) || Directory.Exists(output) || File.Exists(output)) throw new ArgumentException();
    if (flags.ContainsKey("--key-file") == flags.ContainsKey("--key-env-file")) throw new ArgumentException();
    string ReadPrivate(string file, int max, bool external)
    {
        RejectLinkedPath(file);
        if (external && InsideRepository(file) || new FileInfo(file).Length > max || File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint)) throw new ArgumentException();
        if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(file) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite)) != 0) throw new ArgumentException();
        return File.ReadAllText(file).TrimEnd('\r', '\n');
    }
    string key;
    if (flags.TryGetValue("--key-file", out var keyFile)) key = ReadPrivate(keyFile, 4096, true);
    else
    {
        var lines = ReadPrivate(flags["--key-env-file"], 1024 * 1024, false).Split('\n')
            .Select(l => l.TrimStart('\uFEFF')).Where(l => l.StartsWith("OPEN_AI_TEST_API_KEY=", StringComparison.Ordinal)).ToArray();
        if (lines.Length != 1) throw new LabSetupException(lines.Length == 0 ? "LabTestCredentialMissing" : "LabTestCredentialAmbiguous");
        key = lines[0]["OPEN_AI_TEST_API_KEY=".Length..].Trim().Trim('"', '\'');
    }
    if (string.IsNullOrWhiteSpace(key) || key.Any(char.IsControl)) throw new ArgumentException();
    var password = flags.TryGetValue("--password-file", out var passwordFile) ? ReadPrivate(passwordFile, 512, true) : null;
    if (password?.Length > 128) throw new ArgumentException();
    var model = flags.GetValueOrDefault("--model", "gpt-4o-mini"); var image = flags.GetValueOrDefault("--ocr-image", "saldo360-ocr-tests:local");
    if (!Regex.IsMatch(model, "^[A-Za-z0-9.-]{1,80}$") || !Regex.IsMatch(image, "^[A-Za-z0-9/_.:-]{1,150}$")) throw new ArgumentException();
    if (!int.TryParse(flags.GetValueOrDefault("--max-calls", "1"), out var maxCalls) || maxCalls is < 1 or > 20) throw new ArgumentException();
    var input = Path.GetFullPath(flags["--input"]);
    var files = Directory.Exists(input) ? Directory.GetFiles(input, "*.pdf").Order(StringComparer.Ordinal).ToArray() : [input];
    if (files.Length is 0 or > 20 || files.Any(f => !f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) || new FileInfo(f).Length is <= 0 or > 10 * 1024 * 1024)) throw new ArgumentException();
    // A private, new operation directory is also a durable no-overwrite reservation. No automatic retry.
    Directory.CreateDirectory(output);
    if (OperatingSystem.IsWindows())
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(output).SetAccessControl(security);
    }
    else File.SetUnixFileMode(output, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    using var pipeline = new StatementExtractionLabPipeline(key, model, image, maxCalls);
    var summary = new List<object>();
    var failed = false;
    var stopPaidCalls = false;
    using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(10));
    foreach (var file in files)
    {
        await using var pdf = File.OpenRead(file);
        var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(pdf, deadline.Token)); pdf.Position = 0;
        var reportPath = Path.Combine(output, hash + ".json");
        await using var report = new FileStream(reportPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        object data;
        try
        {
            if (stopPaidCalls) throw new CreditCardStatementAgentUnavailableException("Execução interrompida.", failureCode: "LabStoppedAfterFailure");
            var extraction = await pipeline.ExtractAsync(pdf, password, deadline.Token);
            failed |= extraction.Primary is null || extraction.OutputCoverage?.FailedPages > 0;
            stopPaidCalls |= extraction.OutputCoverage?.Batches.Any(b => b.FailureCode is not null) == true;
            data = new { documentHash = hash, provider = "OpenAI", model, scope = "PurchasesOnly", promptVersion = CreditCardStatementAgentPrompt.PurchasesOnlyVersion, extraction, financialMutationExecuted = false };
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            failed = true;
            data = new { documentHash = hash, failureCode = e is CreditCardStatementAgentUnavailableException known ? known.FailureCode
                : e is CreditCardPdfReadException pdfError ? pdfError.Failure.ToString() : "LabDocumentFailed", financialMutationExecuted = false };
        }
        await JsonSerializer.SerializeAsync(report, data, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }, deadline.Token);
        summary.Add(new { documentHash = hash, report = hash + ".json" });
    }
    await using var summaryFile = new FileStream(Path.Combine(output, "summary.json"), FileMode.CreateNew, FileAccess.Write, FileShare.None);
    await JsonSerializer.SerializeAsync(summaryFile, new { provider = "OpenAI", model, scope = "PurchasesOnly", promptVersion = CreditCardStatementAgentPrompt.PurchasesOnlyVersion, calls = pipeline.Calls, maxCalls, documents = summary, financialMutationExecuted = false }, cancellationToken: deadline.Token);
    Console.WriteLine(JsonSerializer.Serialize(new { documents = files.Length, calls = pipeline.Calls, maxCalls, financialMutationExecuted = false })); return failed ? 2 : 0;
}
catch (LabSetupException error) { Console.Error.WriteLine(error.Code + "; no provider call executed."); return 1; }
catch (Exception) { Console.Error.WriteLine("LabArgumentsOrExecutionFailed; details suppressed. Use --help. No automatic retry."); return 1; }

sealed class LabSetupException(string code) : Exception
{
    public string Code { get; } = code;
}
