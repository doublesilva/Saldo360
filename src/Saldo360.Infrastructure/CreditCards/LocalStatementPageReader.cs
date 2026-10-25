using System.Diagnostics;
using Saldo360.Application.CreditCards;

namespace Saldo360.Infrastructure.CreditCards;

internal sealed class LocalStatementPageReader(string? dockerImage = null)
{
    public static bool Usable(string text) => text.Length >= 40 && text.Count(char.IsLetterOrDigit) >= 25
        && text.Count(c => c == '\ufffd' || char.IsControl(c) && c is not ('\r' or '\n' or '\t')) * 100 <= text.Length;

    public async Task<CreditCardPdfExtractedPage> ReadAsync(byte[] unlockedPage, CreditCardPdfExtractedPage page, bool ocr, CancellationToken ct)
    {
        var root = Path.GetFullPath(Path.GetTempPath());
        var directory = Directory.CreateTempSubdirectory("saldo360-page-").FullName;
        var stage = "Render";
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            var pdf = Path.Combine(directory, "input.pdf"); var prefix = Path.Combine(directory, "page");
            await File.WriteAllBytesAsync(pdf, unlockedPage, ct);
            await RunAsync("pdftoppm", ["-f", "1", "-singlefile", "-scale-to", "2200", "-png", pdf, prefix], directory, ct);
            stage = "ReadImage";
            var imagePath = prefix + ".png";
            if (new FileInfo(imagePath).Length is <= 8 or > 4 * 1024 * 1024) throw new InvalidDataException();
            var image = await File.ReadAllBytesAsync(imagePath, ct);
            if (!ocr) return page with { ImagePng = image, ProcessingStatus = "NativeWithImage" };
            var output = Path.Combine(directory, "ocr");
            stage = "Ocr";
            await RunAsync("tesseract", [imagePath, output, "-l", "por", "--psm", "3"], directory, ct);
            stage = "ReadText";
            if (new FileInfo(output + ".txt").Length > 1024 * 1024) throw new InvalidDataException();
            var text = await File.ReadAllTextAsync(output + ".txt", ct);
            return page with { Text = text, Words = null, TextSource = "OCR", ImagePng = image,
                ProcessingStatus = Usable(text) ? "OCRWithImage" : "Unreadable" };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            var code = error switch
            {
                FileNotFoundException => "MissingOutput", InvalidDataException => "OutputLimit",
                System.ComponentModel.Win32Exception => "ProcessStart", OperationCanceledException => "Timeout",
                IOException => "ToolIO", _ => "PreparationFailed"
            };
            return page with { ProcessingStatus = "Unavailable", PreparationFailureCode = stage + code };
        }
        finally
        {
            // Only the unique directory created above, beneath the resolved temporary root.
            if (Path.GetFullPath(directory).StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                Directory.Delete(directory, recursive: true);
        }
    }

    private async Task RunAsync(string executable, string[] args, string directory, CancellationToken ct)
    {
        var container = dockerImage is null ? null : "saldo360-page-" + Guid.NewGuid().ToString("N");
        var start = new ProcessStartInfo(dockerImage is null ? executable : "docker") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        if (dockerImage is not null)
        {
            foreach (var arg in new[] { "run", "--rm", "--name", container!, "--pull", "never", "--network", "none", "--memory", "256m", "--cpus", "1", "--mount", $"type=bind,source={directory},target=/work", "--workdir", "/work", "--entrypoint", executable, dockerImage }) start.ArgumentList.Add(arg);
        }
        foreach (var arg in args) start.ArgumentList.Add(dockerImage is null ? arg : arg.Replace(directory, "/work", StringComparison.Ordinal).Replace('\\', '/')); // Never a password or CPF.
        using var process = Process.Start(start) ?? throw new IOException();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var drain = process.StandardError.BaseStream.CopyToAsync(Stream.Null, timeout.Token);
        var outputDrain = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, timeout.Token);
        try
        {
            while (!process.HasExited)
            {
                timeout.Token.ThrowIfCancellationRequested();
                // Docker enforces the actual tool's memory limit. Its short-lived client is not the tool.
                if (dockerImage is null)
                {
                    try
                    {
                        process.Refresh();
                        if (!process.HasExited && process.WorkingSet64 > 256L * 1024 * 1024) throw new IOException();
                    }
                    catch (InvalidOperationException) when (process.HasExited) { }
                    catch (System.ComponentModel.Win32Exception) when (process.HasExited) { }
                }
                await Task.Delay(40, timeout.Token);
            }
            await Task.WhenAll(drain, outputDrain);
            if (process.ExitCode != 0) throw new IOException();
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None);
                if (container is not null) await RemoveOwnedContainerAsync(container);
            }
        }
    }

    private static async Task RemoveOwnedContainerAsync(string container)
    {
        // Exact random name generated by this invocation; never enumerate or remove other containers.
        var start = new ProcessStartInfo("docker") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "rm", "--force", container }) start.ArgumentList.Add(arg);
        using var cleanup = Process.Start(start);
        if (cleanup is null) return;
        var stdout = cleanup.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
        var stderr = cleanup.StandardError.BaseStream.CopyToAsync(Stream.Null);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await cleanup.WaitForExitAsync(deadline.Token); }
        catch (OperationCanceledException) { if (!cleanup.HasExited) cleanup.Kill(entireProcessTree: true); }
        await cleanup.WaitForExitAsync(CancellationToken.None);
        await Task.WhenAll(stdout, stderr);
    }
}
