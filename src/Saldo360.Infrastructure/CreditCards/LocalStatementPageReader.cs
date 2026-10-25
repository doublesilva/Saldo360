using System.Diagnostics;
using Saldo360.Application.CreditCards;

namespace Saldo360.Infrastructure.CreditCards;

internal sealed class LocalStatementPageReader
{
    public static bool Usable(string text) => text.Length >= 40 && text.Count(char.IsLetterOrDigit) >= 25
        && text.Count(c => c == '\ufffd' || char.IsControl(c) && c is not ('\r' or '\n' or '\t')) * 100 <= text.Length;

    public async Task<CreditCardPdfExtractedPage> ReadAsync(byte[] unlockedPage, CreditCardPdfExtractedPage page, bool ocr, CancellationToken ct)
    {
        var root = Path.GetFullPath(Path.GetTempPath());
        var directory = Directory.CreateTempSubdirectory("saldo360-page-").FullName;
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            var pdf = Path.Combine(directory, "input.pdf"); var prefix = Path.Combine(directory, "page");
            await File.WriteAllBytesAsync(pdf, unlockedPage, ct);
            await RunAsync("pdftoppm", ["-f", "1", "-singlefile", "-scale-to", "2200", "-png", pdf, prefix], ct);
            var imagePath = prefix + ".png";
            if (new FileInfo(imagePath).Length is <= 8 or > 4 * 1024 * 1024) throw new InvalidDataException();
            var image = await File.ReadAllBytesAsync(imagePath, ct);
            if (!ocr) return page with { ImagePng = image, ProcessingStatus = "NativeWithImage" };
            var output = Path.Combine(directory, "ocr");
            await RunAsync("tesseract", [imagePath, output, "-l", "por", "--psm", "3"], ct);
            if (new FileInfo(output + ".txt").Length > 1024 * 1024) throw new InvalidDataException();
            var text = await File.ReadAllTextAsync(output + ".txt", ct);
            return page with { Text = text, Words = null, TextSource = "OCR", ImagePng = image,
                ProcessingStatus = Usable(text) ? "OCRWithImage" : "Unreadable" };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return page with { ProcessingStatus = "Unavailable" }; }
        finally
        {
            // Only the unique directory created above, beneath the resolved temporary root.
            if (Path.GetFullPath(directory).StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task RunAsync(string executable, string[] args, CancellationToken ct)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (var arg in args) start.ArgumentList.Add(arg); // Never a password or CPF.
        using var process = Process.Start(start) ?? throw new IOException();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var drain = process.StandardError.BaseStream.CopyToAsync(Stream.Null, timeout.Token);
        try
        {
            while (!process.HasExited)
            {
                timeout.Token.ThrowIfCancellationRequested(); process.Refresh();
                if (process.WorkingSet64 > 256L * 1024 * 1024) throw new IOException();
                await Task.Delay(40, timeout.Token);
            }
            await drain;
            if (process.ExitCode != 0) throw new IOException();
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); }
        }
    }
}
